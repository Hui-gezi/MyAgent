using System.Collections.Concurrent;

namespace MyAgent.Core;

/// <summary>A message admitted into an agent's inbox.</summary>
public sealed record InboxMessage(string Text, string Source, DateTimeOffset At);

/// <summary>
/// Durable inbox with two ordered queues, mirroring dsh-agent-loop's next-turn / next-step
/// projections. `followup` opens a new turn; `steer` reaches the next step of a running turn;
/// `inject` only appends context without starting work.
/// </summary>
public sealed class AgentInbox
{
    private readonly ConcurrentQueue<InboxMessage> _nextTurn = new();
    private readonly ConcurrentQueue<InboxMessage> _nextStep = new();
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _stepWaiter;
    private TaskCompletionSource<bool>? _idleWaiter;
    private int _activeTurns;

    public event Action? Inserted;
    public event Action? IdleChanged;

    public bool HasPendingStep => !_nextStep.IsEmpty;
    public bool HasPendingTurn => !_nextTurn.IsEmpty;
    // Idle means no active turn. Queues are signal-only (the driver consumes them via pendingTurns),
    // so a lingering queued message must not make a finished agent look busy forever.
    public bool IsIdle => Volatile.Read(ref _activeTurns) == 0;

    public void QueueTurn(InboxMessage message)
    {
        _nextTurn.Enqueue(message);
        SignalStep();
        Inserted?.Invoke();
    }

    public void QueueStep(InboxMessage message)
    {
        _nextStep.Enqueue(message);
        SignalStep();
        Inserted?.Invoke();
    }

    /// <summary>Drain messages that should be delivered at the next step boundary.</summary>
    public List<InboxMessage> DrainStep()
    {
        var items = new List<InboxMessage>();
        while (_nextStep.TryDequeue(out var m)) items.Add(m);
        return items;
    }

    public bool TryTakeTurn(out InboxMessage? message) => _nextTurn.TryDequeue(out message);

    public void EnterTurn()
    {
        Interlocked.Increment(ref _activeTurns);
        IdleChanged?.Invoke();
    }

    public void ExitTurn()
    {
        Interlocked.Decrement(ref _activeTurns);
        SignalIdle();
        IdleChanged?.Invoke();
    }

    public Task WaitForStepAsync(CancellationToken ct)
    {
        if (HasPendingStep) return Task.CompletedTask;
        lock (_gate)
        {
            if (HasPendingStep) return Task.CompletedTask;
            _stepWaiter ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = _stepWaiter.Task;
            ct.Register(() => { lock (_gate) _stepWaiter?.TrySetResult(false); });
            return task;
        }
    }

    public Task WhenIdleAsync(CancellationToken ct)
    {
        if (IsIdle) return Task.CompletedTask;
        lock (_gate)
        {
            if (IsIdle) return Task.CompletedTask;
            _idleWaiter ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = _idleWaiter.Task;
            ct.Register(() => { lock (_gate) _idleWaiter?.TrySetResult(false); });
            return task;
        }
    }

    private void SignalStep()
    {
        lock (_gate)
        {
            _stepWaiter?.TrySetResult(true);
            _stepWaiter = null;
        }
    }

    private void SignalIdle()
    {
        if (!IsIdle) return;
        lock (_gate)
        {
            _idleWaiter?.TrySetResult(true);
            _idleWaiter = null;
        }
    }
}

/// <summary>A human-facing question raised by a tool (plan review, approval, ask_user_question).</summary>
public sealed record QuestionOption(string Label, string? Description = null);

public sealed record QuestionRequest(
    string Id,
    string Header,
    string Question,
    string? Detail,
    IReadOnlyList<QuestionOption> Options);

/// <summary>Host service that resolves a question by showing UI; mirrors userQuestions.ask.</summary>
public interface IQuestionService
{
    Task<string?> AskAsync(QuestionRequest request, CancellationToken cancellationToken = default);
}

public sealed class NullQuestionService : IQuestionService
{
    public Task<string?> AskAsync(QuestionRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
