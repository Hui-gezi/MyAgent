using System.Text.Json;
using MyAgent.Core;
using MyAgent.Jobs;

namespace MyAgent.Workflow;

/// <summary>
/// Model-facing workflow tool mirroring dsh-tool-workflow: script + meta + args + run_in_background.
/// Foreground returns {kind:'foreground', runId, agentsStarted, result}; background registers a
/// job and returns {kind:'background', jobId, runId} immediately.
/// </summary>
public sealed class WorkflowTool(WorkflowService workflows, JobRegistry jobs) : ITool
{
    public string Name => "workflow";
    public string Description => """
        Run a JavaScript workflow script that orchestrates subagents at scale. Use for work that fans out
        across many independent pieces — an audit over many files, a migration, multi-angle research,
        adversarial verification — where you write the orchestration as a script instead of delegating turn by turn.

        Script hooks (plain JS body, top-level await allowed, `return <value>`):
        - agent(prompt, opts?): Promise — run one subagent; resolves to its final text, or null on failure.
          opts: label, phase, schema, provider, model.
        - pipeline(items, ...stages): Promise<any[]> — each item through stages independently, NO barrier;
          each stage receives (prev, item, index). A stage throw drops that ITEM to null.
        - parallel(thunks): Promise<any[]> — run zero-arg functions concurrently and await ALL (a barrier).
          A throwing thunk resolves to null.
        - phase(title), log(message), args — progress, narration, and the call's args input.

        The script has no filesystem, network, timer, or Node.js APIs; the subagents do the work.
        """;
    public JsonElement InputSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "script": { "type": "string", "description": "Plain JavaScript body, no export const meta. Top-level await allowed." },
            "meta": {
              "type": "object",
              "properties": {
                "name": { "type": "string", "description": "Short kebab-case workflow name." },
                "description": { "type": "string", "description": "One-line description." },
                "whenToUse": { "type": "string" },
                "phases": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "title": { "type": "string" },
                      "detail": { "type": "string" },
                      "provider": { "type": "string" },
                      "model": { "type": "string" }
                    },
                    "required": ["title"]
                  }
                }
              },
              "required": ["name", "description"]
            },
            "args": { "type": "object", "description": "Optional JSON input exposed to the script as args." },
            "run_in_background": { "type": "boolean", "description": "Return a job id immediately instead of waiting." }
          },
          "required": ["script", "meta"],
          "additionalProperties": false
        }
        """).RootElement;
    public int PromptOrder => 1500;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var meta = ParseMeta(args);
            var script = Str(args, "script") ?? "";
            if (script.Length == 0) return ToolResult.Error("script is required.", "INVALID_ARGUMENT");
            var input = args.TryGetProperty("args", out var a) ? a : JsonDocument.Parse("{}").RootElement;
            var background = args.TryGetProperty("run_in_background", out var b) && b.ValueKind == JsonValueKind.True;

            if (background)
            {
                var runId = $"wf_{Guid.NewGuid():N}";
                var entry = jobs.Start("workflow", meta.Name, context.Session.Id, async (handle, ct) =>
                {
                    var result = await workflows.RunAsync(context.Session, meta, script, input,
                        log: msg => handle.Append(msg + "\n", "log"), cancellationToken: ct);
                    return new JobOutcome(
                        result.StopReason == WorkflowStopReason.Completed ? "completed" : "failed",
                        result.Error,
                        new { runId, agentsStarted = result.AgentsStarted, result = result.Value });
                });
                return new ToolResult(JsonSerializer.Serialize(new
                {
                    kind = "background", jobId = entry.Id, runId,
                }, Session.JsonOptions));
            }

            var outcome = await workflows.RunAsync(context.Session, meta, script, input,
                log: msg => context.Session.Append(SessionEventTypes.WorkflowLog,
                    new { name = meta.Name, message = msg }),
                cancellationToken: cancellationToken);

            if (outcome.StopReason != WorkflowStopReason.Completed)
                return ToolResult.Error(
                    $"workflow {outcome.StopReason.ToString().ToLowerInvariant()}: {outcome.Error ?? "unknown error"}",
                    outcome.ErrorCode ?? "RUN_FAILED");

            return new ToolResult(JsonSerializer.Serialize(new
            {
                kind = "foreground",
                runId = "",
                agentsStarted = outcome.AgentsStarted,
                result = outcome.Value,
            }, Session.JsonOptions));
        }
        catch (JsonException ex) { return ToolResult.Error($"Invalid workflow arguments: {ex.Message}", "INVALID_ARGUMENT"); }
        catch (HarnessError ex) { return ToolResult.Error(ex.Message, ex.Code); }
    }

    private static WorkflowMeta ParseMeta(JsonElement args)
    {
        if (!args.TryGetProperty("meta", out var m) || m.ValueKind != JsonValueKind.Object)
            throw new HarnessError("INVALID_ARGUMENT", "meta is required.");
        var name = Str(m, "name") ?? "";
        var description = Str(m, "description") ?? "";
        if (name.Length == 0 || description.Length == 0)
            throw new HarnessError("META_INVALID", "meta.name and meta.description are required.");
        var phases = new List<WorkflowPhase>();
        if (m.TryGetProperty("phases", out var ps) && ps.ValueKind == JsonValueKind.Array)
            foreach (var p in ps.EnumerateArray())
                phases.Add(new WorkflowPhase(Str(p, "title") ?? "", Str(p, "detail")));
        return new WorkflowMeta(name, description, Str(m, "whenToUse"), phases);
    }

    private static string? Str(JsonElement o, string n) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
