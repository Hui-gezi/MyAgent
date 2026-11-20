using System.Collections.Concurrent;
using MyAgent.Core;

namespace MyAgent.App;

/// <summary>
/// Host-side question service (mirrors dsh userQuestions). A tool calls AskAsync and blocks;
/// the UI resolves it with Answer/Dismiss. Pending requests are surfaced to the view model.
/// </summary>
public sealed class QuestionService : IQuestionService
{
    private sealed class Pending(string id, QuestionRequest request)
    {
        public string Id { get; } = id;
        public QuestionRequest Request { get; } = request;
        public TaskCompletionSource<string?> Done { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    public event Action<string, QuestionRequest>? QuestionRaised;
    public event Action<string>? QuestionCleared;

    public IReadOnlyList<(string Id, QuestionRequest Request)> PendingRequests =>
        [.. _pending.Values.Select(p => (p.Id, p.Request))];

    public async Task<string?> AskAsync(QuestionRequest request, CancellationToken cancellationToken = default)
    {
        var id = $"q_{Guid.NewGuid():N}";
        var pending = new Pending(id, request);
        _pending[id] = pending;
        QuestionRaised?.Invoke(id, request);
        try
        {
            using var reg = cancellationToken.Register(() => pending.Done.TrySetResult(null));
            return await pending.Done.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
            QuestionCleared?.Invoke(id);
        }
    }

    /// <summary>Resolve with a selected option label (e.g. "Approve").</summary>
    public bool Answer(string id, string label)
    {
        if (!_pending.TryGetValue(id, out var pending)) return false;
        return pending.Done.TrySetResult(label);
    }

    /// <summary>Dismiss without an answer (keeps planning / cancels).</summary>
    public bool Dismiss(string id)
    {
        if (!_pending.TryGetValue(id, out var pending)) return false;
        return pending.Done.TrySetResult(null);
    }
}
