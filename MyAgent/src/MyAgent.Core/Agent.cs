using System.Text.Json;

namespace MyAgent.Core;

public interface ISystemPromptAssembler
{
    string Assemble(Session session, IReadOnlyList<ITool> tools);
}

public sealed class AgentOptions
{
    public string Model { get; set; } = "deepseek-flash";
    public int MaxTokens { get; set; } = 256_000;

    /// <summary>Provider context window used by compaction/occupancy; must match the real model.</summary>
    public int ContextWindow { get; set; } = 1_048_576;
    public bool ThinkingEnabled { get; set; } = true;
    public int MaxParallelToolCalls { get; set; } = ToolBatch.DefaultMaxParallelToolCalls;
}

public sealed class Agent(
    Session session,
    ILlmProvider provider,
    ToolRegistry tools,
    ISystemPromptAssembler systemPrompt,
    AgentOptions options)
{
    private readonly object _driverLock = new();
    private Task? _driver;
    private int _pendingTurns;
    private CancellationTokenSource? _turnCts;
    private string? _lastCommittedSystem;

    public Session Session => session;
    public bool IsRunning { get; private set; }
    public AgentInbox Inbox { get; } = new();
    public Func<Session, CancellationToken, Task>? PreStepAsync { get; set; }

    /// <summary>
    /// Overflow recovery hook: invoked when the provider rejects a request as exceeding the
    /// context window. Returns true when history was compacted and the step should be retried.
    /// Without it (or when it returns false) the turn fails like any other LLM error.
    /// </summary>
    public Func<Session, CancellationToken, Task<bool>>? ContextOverflowRecoveryAsync { get; set; }

    /// <summary>
    /// Hook invoked after each turn settles (including interrupted turns). Features such as
    /// goal auto-continuation return an <see cref="AfterTurnAction"/> to admit another turn.
    /// </summary>
    public Func<Session, string, CancellationToken, Task<AfterTurnAction?>>? AfterTurnAsync { get; set; }

    public event Action<SessionEvent>? EventAppended
    {
        add => session.EventAppended += value;
        remove => session.EventAppended -= value;
    }

    public event Action<StreamChunk>? AssistantStream;
    public event Action<bool>? RunningChanged;
    public event Action<Exception>? Error;

    public void Followup(string text, string source = "user")
    {
        session.Append(SessionEventTypes.UserMessage, new { text, source });
        Inbox.QueueTurn(new InboxMessage(text, source, DateTimeOffset.UtcNow));
        lock (_driverLock)
        {
            _pendingTurns++;
            if (_driver is null || _driver.IsCompleted)
                _driver = Task.Run(KickAsync);
        }
    }

    /// <summary>Steer a running agent: message reaches the next step without opening a new turn.</summary>
    public void Steer(string text, string source = "agent-message")
    {
        session.Append(SessionEventTypes.UserMessage, new { text, source });
        Inbox.QueueStep(new InboxMessage(text, source, DateTimeOffset.UtcNow));
    }

    /// <summary>Inject context for the next step without starting or steering a turn.</summary>
    public void Inject(string text, string source = "notice")
    {
        session.Append(SessionEventTypes.UserMessage, new { text, source });
    }

    public Task WhenIdleAsync(CancellationToken ct = default) => Inbox.WhenIdleAsync(ct);

    public void Cancel() => _turnCts?.Cancel();

    private async Task KickAsync()
    {
        while (await TurnAsync()) { }
    }

    private async Task<bool> TurnAsync()
    {
        lock (_driverLock)
        {
            if (_pendingTurns <= 0) return false;
            _pendingTurns--;
        }

        var turnId = $"turn_{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource();
        _turnCts = cts;
        var ct = cts.Token;
        var turnStartSeq = session.Append(SessionEventTypes.TurnStart, new { turnId }).Seq;
        SetRunning(true);
        Inbox.EnterTurn();
        var stepIndex = 0;
        var stopReason = "stop";
        var overflowRecoveries = 0;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // Consume any steered message already appended to the log (no-op: visible via DeriveMessages).
                Inbox.DrainStep();
                if (PreStepAsync is not null)
                    await PreStepAsync(session, ct);

                var system = systemPrompt.Assemble(session, tools.Tools);
                if (system != _lastCommittedSystem)
                {
                    session.Append(SessionEventTypes.SystemMessage, new { text = system });
                    _lastCommittedSystem = system;
                }
                session.Append(SessionEventTypes.RequestContext, new { text = BuildRuntimeContext() });

                var (derivedSystem, messages) = session.DeriveMessages();
                var toolSpecs = tools.Tools.Select(t => new ToolSpec(t.Name, t.Description, t.InputSchema)).ToList();
                // Heuristic context breakdown for the occupancy meter (chars/4, mirrors
                // token-meter estimate): system prompt, tool schemas, conversation messages.
                var systemTokens = (derivedSystem ?? system).Length / 4;
                var toolsTokens = toolSpecs.Sum(t => t.Name.Length + t.Description.Length + t.InputSchema.GetRawText().Length) / 4;
                var messageTokens = messages.Sum(m => m.Blocks.Sum(b => b switch
                {
                    TextBlock tb => tb.Text.Length / 4,
                    ThinkingBlock th => th.Text.Length / 4,
                    ToolUseBlock tu => tu.Input.GetRawText().Length / 4 + tu.Name.Length,
                    ToolResultBlock tr => tr.Content.Length / 4,
                    _ => 0,
                }));
                session.Append(SessionEventTypes.RequestHeader, new
                {
                    model = options.Model,
                    maxTokens = options.MaxTokens,
                    thinking = options.ThinkingEnabled,
                    contextBreakdown = new { systemTokens, toolsTokens, messageTokens },
                });
                session.Append(SessionEventTypes.StepStart, new { turnId, stepIndex });

                var request = new LlmRequest
                {
                    Model = options.Model,
                    System = derivedSystem ?? system,
                    Messages = messages,
                    Tools = [.. toolSpecs],
                    MaxTokens = options.MaxTokens,
                    ThinkingEnabled = options.ThinkingEnabled,
                };

                AssistantOutcome outcome;
                try
                {
                    outcome = await LlmAccumulator.CollectAsync(
                        provider.StreamAsync(request, ct),
                        chunk => AssistantStream?.Invoke(chunk),
                        ct);
                }
                catch (LlmException ex)
                {
                    session.Append(SessionEventTypes.AssistantAttempt, new { code = ex.Code, message = ex.Message });
                    // The provider says the request is too long: compact and retry THIS step
                    // instead of failing the turn (local estimates can undercount CJK text).
                    if (ex.Code == LlmErrorCodes.ContextWindowExceeded &&
                        ContextOverflowRecoveryAsync is not null &&
                        overflowRecoveries < 2 &&
                        await ContextOverflowRecoveryAsync(session, ct))
                    {
                        overflowRecoveries++;
                        continue;
                    }
                    throw;
                }
                overflowRecoveries = 0;

                if (outcome.Blocks.Count == 0)
                {
                    session.Append(SessionEventTypes.AssistantAttempt, new
                    {
                        code = LlmErrorCodes.EmptyResponse,
                        message = "Provider returned an empty response.",
                    });
                    throw new LlmException(LlmErrorCodes.EmptyResponse, "Provider returned an empty response.");
                }

                session.Append(SessionEventTypes.AssistantMessage,
                    new AssistantMessageData(outcome.Blocks, outcome.StopReason, outcome.Usage, outcome.Timing));

                var calls = outcome.Blocks.OfType<ToolUseBlock>().ToList();
                session.Append(SessionEventTypes.StepEnd, new { turnId, stepIndex });
                if (calls.Count == 0)
                {
                    stopReason = outcome.StopReason;
                    break;
                }

                foreach (var call in calls)
                    session.Append(SessionEventTypes.ToolCall, new
                    {
                        toolCallId = call.Id,
                        name = call.Name,
                        args = call.Input,
                    });

                var context = new ToolContext(session, session.Cwd);
                await ToolBatch.ExecuteAsync(calls, tools, context, options.MaxParallelToolCalls,
                    (call, result) => session.Append(SessionEventTypes.ToolResult,
                        new ToolResultData(call.Id, result.Content, result.IsError, result.ErrorCode)),
                    ct);
                stepIndex++;
            }

            session.Append(SessionEventTypes.TurnEnd, new { turnId, stopReason });
        }
        catch (OperationCanceledException)
        {
            stopReason = "cancelled";
            CloseInterrupted(turnStartSeq, turnId, stepIndex, stopReason);
        }
        catch (Exception ex)
        {
            stopReason = "error";
            CloseInterrupted(turnStartSeq, turnId, stepIndex, stopReason);
            Error?.Invoke(ex);
        }
        finally
        {
            _turnCts = null;
            SetRunning(false);
            Inbox.ExitTurn();
        }

        // Features may admit a follow-up turn after this one settles (e.g. goal auto-continuation).
        // Run after the closed turn so a hook failure never corrupts it.
        if (AfterTurnAsync is not null)
        {
            try
            {
                var action = await AfterTurnAsync(session, stopReason, CancellationToken.None);
                if (action?.Prompt is { Length: > 0 } prompt)
                {
                    session.Append(SessionEventTypes.UserMessage,
                        new { text = prompt, source = action.Source ?? "feature" });
                    Interlocked.Increment(ref _pendingTurns);
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
            }
        }

        lock (_driverLock)
        {
            if (_pendingTurns > 0) return true;
        }

        // The run is about to idle: settle any stale in_progress todos so the task list
        // never keeps showing "working" after the conversation has finished. The model is
        // asked to close its own list, but it may forget — this is the durable safety net.
        FinalizeTodos(stopReason);
        return false;
    }

    /// <summary>
    /// When the agent goes idle, close out leftover in_progress todos: a normally finished
    /// turn means the work is done (completed); a cancelled/failed turn means it is not
    /// running anymore (pending). Appended as a regular todo/update event so live UI,
    /// snapshot reload and DeriveMessages all stay consistent.
    /// </summary>
    private void FinalizeTodos(string stopReason)
    {
        var last = session.Events.LastOrDefault(e => e.Type == SessionEventTypes.TodoUpdate);
        if (last is null) return;
        if (!last.Data.TryGetProperty("todos", out var todosEl) || todosEl.ValueKind != JsonValueKind.Array) return;
        var todos = todosEl.Deserialize<List<TodoItem>>(Session.JsonOptions);
        if (todos is null || !todos.Any(t => t.Status == "in_progress")) return;

        var settled = stopReason is "cancelled" or "error" ? "pending" : "completed";
        var updated = todos.Select(t => t.Status == "in_progress" ? t with { Status = settled } : t).ToList();
        session.Append(SessionEventTypes.TodoUpdate, new { todos = updated });
    }

    private void CloseInterrupted(long turnStartSeq, string turnId, int stepIndex, string stopReason)
    {
        var answered = session.Events
            .Where(e => e.Seq > turnStartSeq && e.Type == SessionEventTypes.ToolResult)
            .Select(e => e.Data.GetProperty("toolCallId").GetString())
            .ToHashSet();
        foreach (var evt in session.Events.Where(e => e.Seq > turnStartSeq && e.Type == SessionEventTypes.ToolCall))
        {
            var toolCallId = evt.Data.GetProperty("toolCallId").GetString()!;
            if (answered.Contains(toolCallId)) continue;
            session.Append(SessionEventTypes.ToolResult,
                new ToolResultData(toolCallId, "Error: Tool call interrupted before completion.", true, stopReason));
        }
        session.Append(SessionEventTypes.StepEnd, new { turnId, stepIndex });
        session.Append(SessionEventTypes.TurnEnd, new { turnId, stopReason });
    }

    /// <summary>
    /// Extra runtime-context lines contributed per request (sandbox policy, approval policy).
    /// They travel AFTER retained history as part of the snapshot, so switching a policy never
    /// rewrites the stable system-prompt cache prefix (mirrors dsh systemPrompt.context).
    /// </summary>
    public Func<Session, IEnumerable<string>>? RuntimeContextSections { get; set; }

    private string BuildRuntimeContext()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<runtime-context>\n");
        sb.Append($"cwd: {session.Cwd}\n");
        sb.Append($"platform: {Environment.OSVersion.Platform}\n");
        sb.Append($"local time: {DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}\n");
        if (RuntimeContextSections is not null)
        {
            foreach (var section in RuntimeContextSections(session))
                if (!string.IsNullOrWhiteSpace(section))
                    sb.Append(section).Append('\n');
        }
        sb.Append("</runtime-context>");
        return sb.ToString();
    }

    private void SetRunning(bool running)
    {
        if (IsRunning == running) return;
        IsRunning = running;
        RunningChanged?.Invoke(running);
    }
}