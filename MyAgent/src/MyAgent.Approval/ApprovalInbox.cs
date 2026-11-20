using System.Collections.Concurrent;
using MyAgent.Core;

namespace MyAgent.Approval;

/// <summary>
/// Host-side approval answerer (the composed UI answerer of the approval/request waterfall).
/// A tool's escalation ask blocks here; the UI resolves it with Answer/Dismiss. Pending
/// requests are surfaced to the view model through Raised/Cleared. Cancellation of the
/// calling tool withdraws the question (the service settles 'cancelled').
/// </summary>
public sealed class ApprovalInbox : IApprovalAnswerer
{
    private sealed class Pending(string id, ApprovalRequest request)
    {
        public string Id { get; } = id;
        public ApprovalRequest Request { get; } = request;
        public TaskCompletionSource<ApprovalOutcome> Done { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    public event Action<string, ApprovalRequest>? Raised;
    public event Action<string>? Cleared;

    public IReadOnlyList<(string Id, ApprovalRequest Request)> PendingRequests =>
        [.. _pending.Values.Select(p => (p.Id, p.Request))];

    public async Task<ApprovalOutcome> AnswerAsync(ApprovalRequest request)
    {
        var id = $"ap_{Guid.NewGuid():N}";
        var pending = new Pending(id, request);
        _pending[id] = pending;
        Raised?.Invoke(id, request);
        try
        {
            using var reg = request.Signal.Register(() => pending.Done.TrySetResult(ApprovalOutcome.Cancelled));
            return await pending.Done.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
            Cleared?.Invoke(id);
        }
    }

    /// <summary>Resolve with a decision (allowed-once is the only grant).</summary>
    public bool Answer(string id, ApprovalOutcome outcome)
    {
        if (!_pending.TryGetValue(id, out var pending)) return false;
        return pending.Done.TrySetResult(outcome);
    }

    /// <summary>Dismiss without a decision — the ask settles 'cancelled'.</summary>
    public bool Dismiss(string id) => Answer(id, ApprovalOutcome.Cancelled);
}
