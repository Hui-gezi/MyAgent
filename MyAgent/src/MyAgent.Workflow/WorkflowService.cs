using System.Text.Json;
using Jint;
using Jint.Native;
using MyAgent.Core;
using MyAgent.Subagents;

namespace MyAgent.Workflow;

public sealed record WorkflowMeta(
    string Name,
    string Description,
    string? WhenToUse = null,
    List<WorkflowPhase>? Phases = null);

public sealed record WorkflowPhase(string Title, string? Detail = null);

public enum WorkflowStopReason { Completed, Cancelled, Error }

public sealed record WorkflowResult(
    JsonElement Value,
    WorkflowStopReason StopReason,
    string? Error,
    string? ErrorCode,
    int AgentsStarted);

/// <summary>
/// PTC-equivalent workflow engine. Runs a model-authored JavaScript body inside Jint (no fs,
/// network, timer or Node APIs) with hooks: agent / pipeline / parallel / phase / log / args.
/// `agent()` starts a real subagent through SubagentRuntime and awaits its result; a failed
/// child resolves to null. `run_in_background` registers a job (handled by the WorkflowTool).
/// </summary>
public sealed class WorkflowService
{
    public const int MaxItemsPerCall = 4096;
    public const int DefaultMaxAgentsPerRun = 40;

    private readonly SubagentRuntime _subagents;
    private readonly int _maxAgentsPerRun;

    public WorkflowService(SubagentRuntime subagents, int maxAgentsPerRun = DefaultMaxAgentsPerRun)
    {
        _subagents = subagents;
        _maxAgentsPerRun = maxAgentsPerRun;
    }

    public Task<WorkflowResult> RunAsync(
        Session session,
        WorkflowMeta meta,
        string script,
        JsonElement args,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var runId = $"wf_{Guid.NewGuid():N}";
        EmitRunStart(session, runId, meta.Name);
        foreach (var phase in meta.Phases ?? [])
            EmitPhase(session, runId, phase.Title);

        var state = new RunState(session, runId, meta, log);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var engine = new Engine(options =>
            {
                options.LimitMemory(64_000_000);
                options.TimeoutInterval(TimeSpan.FromMinutes(10));
                options.MaxStatements(200_000);
            });
            state.Engine = engine;

            // Parse args as real JSON so arrays/maps become genuine JS values (List<T> wrappers
            // produced by FromObject are not JS arrays and break pipeline/iteration).
            engine.SetValue("__argsJson", args.GetRawText());
            engine.Execute("var args = JSON.parse(__argsJson);");
            engine.SetValue("phase", new Action<string>(title => EmitPhase(session, runId, title)));
            engine.SetValue("log", new Action<string>(message =>
            {
                EmitLog(session, runId, message);
                log?.Invoke(message);
            }));
            engine.SetValue("agent", new Func<string, JsValue?, object?>((prompt, opts) =>
                RunAgent(state, prompt, opts, cancellationToken)));
            engine.SetValue("parallel", new Func<JsValue, object?[]>(thunks =>
                RunParallel(state, thunks)));
            engine.SetValue("__pipelineHost", new Func<JsValue, JsValue, object?[]>((items, stages) =>
                RunPipeline(state, items, stages)));
            engine.SetValue("__parallelHost", new Func<JsValue, object?[]>(thunks =>
                RunParallel(state, thunks)));

            // Define the model-facing hooks in JS so rest/variadic args are collected into arrays
            // before crossing the host boundary (Jint delegates cannot bind C# `params`).
            engine.Execute("""
                var pipeline = function(items, ...stages) { return __pipelineHost(items, stages); };
                var parallel = function(thunks) { return __parallelHost(thunks); };
                """);

            var wrapped = "(async () => {\n" + script + "\n})()";
            var result = engine.Evaluate(wrapped);
            var value = result.IsPromise() ? result.UnwrapIfPromise() : result;

            JsonElement materialized;
            try
            {
                materialized = JsonSerializer.SerializeToElement(
                    value.ToObject() ?? new Dictionary<string, object?>(), Session.JsonOptions);
            }
            catch (Exception)
            {
                return Task.FromResult(Settle(session, state, WorkflowStopReason.Error,
                    "RESULT_UNSERIALIZABLE", "Workflow return value is not JSON-serializable."));
            }

            EmitRunEnd(session, runId, "completed", state.AgentsStarted, null, null);
            return Task.FromResult(new WorkflowResult(
                materialized, WorkflowStopReason.Completed, null, null, state.AgentsStarted));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(Settle(session, state, WorkflowStopReason.Cancelled,
                "CANCELLED", "workflow run was cancelled"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Settle(session, state, WorkflowStopReason.Error,
                "RUN_FAILED", ex.Message));
        }
    }

    private object? RunAgent(RunState state, string prompt, JsValue? opts, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var label = ReadOpt(opts, "label") ?? ShortLabel(prompt);
        var phase = ReadOpt(opts, "phase");
        var hasSchema = ReadOpt(opts, "schema") is not null;

        if (state.AgentsStarted >= _maxAgentsPerRun)
            throw new InvalidOperationException($"Workflow exceeded the {_maxAgentsPerRun} agent limit.");

        var seq = Interlocked.Increment(ref state.AgentsStarted);
        var childId = $"wfagent_{seq}";
        state.Session.Append(SessionEventTypes.WorkflowAgentStart, new
        {
            runId = state.RunId, seq, label, phase, childId,
        });

        object? output = null;
        try
        {
            var activation = _subagents.Start(
                state.Session, "spawn", label, prompt, name: null, delivery: "caller");
            var result = activation.Result.Task.GetAwaiter().GetResult();
            output = hasSchema
                ? (object?)new Dictionary<string, object?> { ["text"] = result.Output }
                : result.Output;
        }
        catch (Exception ex)
        {
            state.Log?.Invoke($"agent {seq} failed: {ex.Message}");
            output = null; // a failed child resolves to null, as in agent()
        }

        state.Session.Append(SessionEventTypes.WorkflowAgentEnd, new
        {
            runId = state.RunId, seq, label, phase, childId,
            outcome = output is null ? "failed" : "completed",
        });
        return output;
    }

    private object?[] RunParallel(RunState state, JsValue thunksValue)
    {
        // parallel(thunks) is a barrier: await ALL; a throwing thunk resolves to null.
        var thunks = ArrayValues(thunksValue);
        var results = new object?[thunks.Count];
        var engine = state.Engine;
        // Jint Engine is single-threaded; run hooks sequentially (the JS VM interleaves
        // async agent() promises, which is how PTC achieves concurrency).
        for (var i = 0; i < thunks.Count; i++)
        {
            try
            {
                var value = engine.Call(thunks[i]);
                results[i] = Materialize(value);
            }
            catch (Exception ex)
            {
                state.Log?.Invoke($"parallel task {i} failed: {ex.Message}");
                results[i] = null;
            }
        }
        return results;
    }

    private object?[] RunPipeline(RunState state, JsValue items, JsValue stagesValue)
    {
        // pipeline(items, ...stages): a stage throw drops that ITEM to null, no barrier between stages.
        var stages = ArrayValues(stagesValue);
        var list = ArrayValues(items).Select(ToClr).ToList();
        var engine = state.Engine;
        var outputs = new object?[list.Count];
        // Jint Engine is single-threaded; keep stage calls on the script thread.
        for (var i = 0; i < list.Count; i++)
        {
            object? current = list[i];
            try
            {
                foreach (var stage in stages)
                {
                    var value = engine.Call(stage, JsValue.Undefined,
                    [
                        JsValue.FromObject(engine, current),
                        JsValue.FromObject(engine, list[i]),
                        JsValue.FromObject(engine, i),
                    ]);
                    current = Materialize(value);
                }
                outputs[i] = current;
            }
            catch (Exception ex)
            {
                state.Log?.Invoke($"pipeline item {i} failed: {ex.Message}");
                outputs[i] = null;
            }
        }
        return outputs;
    }

    private static List<JsValue> ArrayValues(JsValue value)
    {
        var list = new List<JsValue>();
        if (!value.IsArray()) return list;
        var arr = value.AsArray();
        for (var i = 0; i < arr.Length; i++) list.Add(arr.Get(i.ToString()));
        return list;
    }

    private static object? Materialize(JsValue value)
    {
        if (value.IsPromise()) value = value.UnwrapIfPromise();
        return value.IsUndefined() || value.IsNull() ? null : value.ToObject();
    }

    private static object? ToClr(JsValue value) =>
        value.IsUndefined() || value.IsNull() ? null : value.ToObject();

    private static string? ReadOpt(JsValue? opts, string key)
    {
        if (opts is null || opts.IsUndefined() || opts.IsNull()) return null;
        try
        {
            var v = opts.AsObject().Get(key);
            return v.IsUndefined() || v.IsNull() ? null : v.ToString();
        }
        catch { return null; }
    }

    private static string ShortLabel(string prompt) =>
        prompt.Length > 40 ? prompt[..40] + "…" : prompt;

    private static WorkflowResult Settle(Session session, RunState state, WorkflowStopReason reason,
        string code, string message)
    {
        var stop = reason switch
        {
            WorkflowStopReason.Completed => "completed",
            WorkflowStopReason.Cancelled => "cancelled",
            _ => "error",
        };
        EmitRunEnd(session, state.RunId, stop, state.AgentsStarted, code, message);
        return new WorkflowResult(JsonDocument.Parse("null").RootElement, reason, message, code, state.AgentsStarted);
    }

    private static void EmitRunStart(Session s, string runId, string name) =>
        s.Append(SessionEventTypes.WorkflowStart, new { runId, name });
    private static void EmitPhase(Session s, string runId, string title) =>
        s.Append(SessionEventTypes.WorkflowPhase, new { runId, title });
    private static void EmitLog(Session s, string runId, string message) =>
        s.Append(SessionEventTypes.WorkflowLog, new { runId, message });
    private static void EmitRunEnd(Session s, string runId, string stopReason, int agents,
        string? errorCode, string? error) =>
        s.Append(SessionEventTypes.WorkflowEnd, new { runId, stopReason, agentsStarted = agents, errorCode, error });

    private sealed class RunState(Session session, string runId, WorkflowMeta meta, Action<string>? log)
    {
        public Session Session { get; } = session;
        public string RunId { get; } = runId;
        public WorkflowMeta Meta { get; } = meta;
        public Action<string>? Log { get; } = log;
        public Engine Engine { get; set; } = null!;
        public int AgentsStarted;
    }
}
