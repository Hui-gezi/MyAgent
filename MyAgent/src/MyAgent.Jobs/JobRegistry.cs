using System.Collections.Concurrent;
using System.Text;
using MyAgent.Core;

namespace MyAgent.Jobs;

public enum JobStatus { Running, Stopping, Completed, Killed, Failed }

public sealed record JobChunk(long At, string Text, string Channel = "stdout", bool GapBefore = false);

/// <summary>Bounded output ring with absolute byte offsets and lossy trimming (mirrors dsh jobs-local/ring).</summary>
public sealed class OutputRing(int capacityBytes = 262144)
{
    private readonly object _gate = new();
    private readonly List<JobChunk> _chunks = [];
    private long _total;
    private long _earliest;

    public long Total { get { lock (_gate) return _total; } }
    public long Earliest { get { lock (_gate) return _earliest; } }

    public void Append(string text, string channel = "stdout")
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        lock (_gate)
        {
            _chunks.Add(new JobChunk(_total, text, channel));
            _total += bytes;
            Trim(capacityBytes);
        }
    }

    public (List<JobChunk> Chunks, long Next, bool Lossy) ReadFrom(long from)
    {
        lock (_gate)
        {
            var chunks = _chunks.Where(c => c.At >= from).ToList();
            return (chunks, _total, from < _earliest);
        }
    }

    public void Trim(int cap)
    {
        lock (_gate)
        {
            var excess = _total - cap;
            while (excess > 0 && _chunks.Count > 1)
            {
                var first = _chunks[0];
                var size = Encoding.UTF8.GetByteCount(first.Text);
                _chunks.RemoveAt(0);
                _earliest = first.At + size;
                excess -= size;
            }
        }
    }
}

public sealed record JobView(
    string Id, string Kind, string Label, string Status,
    string? Progress, string? Detail, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt, long OutputTotal, long OutputEarliest);

public sealed record JobOutcome(string Status, string? Detail = null, object? Result = null);

public sealed class JobHandle(string id, OutputRing ring)
{
    public string Id { get; } = id;
    public OutputRing Ring { get; } = ring;
    public string? Progress { get; set; }
    public void Append(string text, string channel = "stdout") => Ring.Append(text, channel);
    public void UpdateProgress(string line) => Progress = line;
}

public sealed class JobEntry
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Label { get; init; }
    public required string Owner { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public JobStatus Status { get; set; } = JobStatus.Running;
    public OutputRing Ring { get; } = new();
    public TaskCompletionSource<JobOutcome> Done { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationTokenSource Cts { get; } = new();
    public string? Progress { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    public string StatusName => Status switch
    {
        JobStatus.Running => "running",
        JobStatus.Stopping => "stopping",
        JobStatus.Completed => "completed",
        JobStatus.Killed => "killed",
        JobStatus.Failed => "failed",
        _ => "running",
    };

    public JobView View() => new(Id, Kind, Label, StatusName, Progress, Detail,
        StartedAt, FinishedAt, Ring.Total, Ring.Earliest);
}

/// <summary>
/// Background job registry: producers own execution resources, the registry owns ids, access,
/// lifecycle and output rings. Mirrors dsh-jobs (JobRegistry + jobs-local pump).
/// </summary>
public sealed class JobRegistry
{
    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new(StringComparer.Ordinal);
    private readonly Func<string, Session?> _resolveSession;

    public JobRegistry(Func<string, Session?> resolveSession) => _resolveSession = resolveSession;

    public JobEntry Start(string kind, string label, string owner, Func<JobHandle, CancellationToken, Task<JobOutcome>> run)
    {
        var id = $"{kind}-{Guid.NewGuid():N}"[..Math.Min(64, kind.Length + 17)];
        var entry = new JobEntry
        {
            Id = id, Kind = kind, Label = label, Owner = owner, StartedAt = DateTimeOffset.UtcNow,
        };
        _jobs[id] = entry;

        var session = _resolveSession(owner);
        session?.Append(SessionEventTypes.JobRegistered, new
        {
            jobId = id, owner, kind, label, startedAt = entry.StartedAt,
        });

        var handle = new JobHandle(id, entry.Ring);
        _ = Task.Run(async () =>
        {
            JobOutcome outcome;
            try
            {
                outcome = await run(handle, entry.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                outcome = new JobOutcome("killed", "cancelled");
            }
            catch (Exception ex)
            {
                outcome = new JobOutcome("failed", ex.Message);
            }
            Settle(entry, outcome);
        });
        return entry;
    }

    private void Settle(JobEntry entry, JobOutcome outcome)
    {
        entry.Status = outcome.Status switch
        {
            "completed" => JobStatus.Completed,
            "killed" => JobStatus.Killed,
            _ => JobStatus.Failed,
        };
        entry.Detail = outcome.Detail;
        entry.FinishedAt = DateTimeOffset.UtcNow;
        entry.Ring.Trim(16384);

        var session = _resolveSession(entry.Owner);
        session?.Append(SessionEventTypes.JobSettled, new
        {
            jobId = entry.Id, owner = entry.Owner, kind = entry.Kind, label = entry.Label,
            startedAt = entry.StartedAt, finishedAt = entry.FinishedAt,
            status = entry.StatusName, cause = "producer", detail = outcome.Detail,
        });
        entry.Done.TrySetResult(outcome);
    }

    public IReadOnlyList<JobView> List(string? owner = null) =>
        [.. _jobs.Values.Where(j => owner is null || j.Owner == owner).Select(j => j.View())];

    public JobEntry? Get(string id, string? owner = null)
    {
        if (!_jobs.TryGetValue(id, out var job)) return null;
        if (owner is not null && job.Owner != owner) return null;
        return job;
    }

    public (List<JobChunk> Chunks, long Next, bool Lossy) ReadAt(string id, long from, string? owner = null)
    {
        var job = Get(id, owner) ?? throw new HarnessError("job_not_found", $"Job '{id}' not found.");
        return job.Ring.ReadFrom(from);
    }

    public string Kill(string id, string reason = "user", string? owner = null)
    {
        var job = Get(id, owner);
        if (job is null) return "not_found";
        if (job.Status is JobStatus.Completed or JobStatus.Killed or JobStatus.Failed) return "already_finished";
        job.Status = JobStatus.Stopping;
        job.Cts.Cancel();
        return "requested";
    }
}
