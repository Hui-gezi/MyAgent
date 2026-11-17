namespace MyAgent.Core;

public interface ISystemPromptAssembler
{
    string Assemble(Session session, IReadOnlyList<ITool> tools);
}

public sealed class AgentOptions
{
    public string Model { get; set; } = "deepseek-flash";
    public int MaxTokens { get; set; } = 256_000;
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
    public Func<Session, CancellationToken, Task>? PreStepAsync { get; set; }

    public event Action<SessionEvent>? EventAppended
    {
        add => session.EventAppended += value;
        remove => session.EventAppended -= value;
    }

    public event Action<StreamChunk>? AssistantStream;
    public event Action<bool>? RunningChanged;
    public event Action<Exception>? Error;

    public void Followup(string text)
    {
        session.Append(SessionEventTypes.UserMessage, new { text });
        lock (_driverLock)
        {
            _pendingTurns++;
            if (_driver is null || _driver.IsCompleted)
                _driver = Task.Run(KickAsync);
        }
    }

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
        var stepIndex = 0;
        var stopReason = "stop";

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (PreStepAsync is not null)
                    await PreStepAsync(session, ct);

                var system = systemPrompt.Assemble(session, tools.Tools);
                if (system != _lastCommittedSystem)
                {
                    session.Append(SessionEventTypes.SystemMessage, new { text = system });
                    _lastCommittedSystem = system;
                }
                session.Append(SessionEventTypes.RequestContext, new { text = BuildRuntimeContext() });
                session.Append(SessionEventTypes.RequestHeader, new
                {
                    model = options.Model,
                    maxTokens = options.MaxTokens,
                    thinking = options.ThinkingEnabled,
                });
                session.Append(SessionEventTypes.StepStart, new { turnId, stepIndex });

                var (derivedSystem, messages) = session.DeriveMessages();
                var request = new LlmRequest
                {
                    Model = options.Model,
                    System = derivedSystem ?? system,
                    Messages = messages,
                    Tools = [.. tools.Tools.Select(t => new ToolSpec(t.Name, t.Description, t.InputSchema))],
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
                    throw;
                }

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
                    new AssistantMessageData(outcome.Blocks, outcome.StopReason, outcome.Usage));

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
            CloseInterrupted(turnStartSeq, turnId, stepIndex, "cancelled");
        }
        catch (Exception ex)
        {
            CloseInterrupted(turnStartSeq, turnId, stepIndex, "error");
            Error?.Invoke(ex);
        }
        finally
        {
            _turnCts = null;
            SetRunning(false);
        }

        lock (_driverLock) return _pendingTurns > 0;
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

    private string BuildRuntimeContext() =>
        $"<runtime-context>\ncwd: {session.Cwd}\nplatform: {Environment.OSVersion.Platform}\nlocal time: {DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}\n</runtime-context>";

    private void SetRunning(bool running)
    {
        if (IsRunning == running) return;
        IsRunning = running;
        RunningChanged?.Invoke(running);
    }
}