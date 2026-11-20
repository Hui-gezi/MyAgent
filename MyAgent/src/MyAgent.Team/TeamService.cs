using System.Text.Json;
using System.Text.Json.Serialization;
using MyAgent.Core;

namespace MyAgent.Team;

public enum TeamTaskStatus { Pending, InProgress, Completed, Deleted }

public sealed record TeamTask(
    string Id,
    int Revision,
    string Subject,
    string Description,
    TeamTaskStatus Status,
    string? OwnerId,
    string? OwnerName,
    List<string> BlockedBy,
    List<string> WriteScopes);

public sealed record TeamTaskView(
    TeamTask Task,
    bool Ready,
    List<string> WriteScopeWarnings);

/// <summary>
/// Shared task board rooted at a session (the Team Lead). Tasks are durable whole-snapshot
/// records carried by <c>team/task</c> events with compare-and-set revisions, and readiness is
/// derived from the blockedBy DAG. Mirrors dsh-agent-team's task-board + task-graph.
/// </summary>
public sealed class TeamService
{
    public const int DefaultMaxTasks = 200;
    private const int EventVersion = 2;

    private readonly int _maxTasks;

    /// <summary>Optional owner-name resolver backed by the subagent roster (set by App).</summary>
    public Func<Session, string, string?>? ResolveOwnerName { get; set; }

    /// <summary>Optional predicate for "is this the root/lead agent" (set by App).</summary>
    public Func<Session, bool>? IsLead { get; set; }

    public TeamService(int maxTasks = DefaultMaxTasks) => _maxTasks = maxTasks;

    public const string PolicySection = """
        Agent Teams is available in this session. The Team Lead and teammates share the working directory and filesystem.
        Split write work into disjoint scopes and record expected write scopes on shared tasks; use task dependencies when
        work must be ordered. Write-scope overlap is advisory, not a lock. Shared-task workflow is:
        list, get, claim with the current revision, perform the work, then complete. Task readiness never starts an owner.
        The root agent must wait for required teammates before giving the final answer.
        """;

    /// <summary>All non-deleted tasks, folded from the durable log.</summary>
    public List<TeamTask> Tasks(Session session)
    {
        var byId = new Dictionary<string, TeamTask>(StringComparer.Ordinal);
        foreach (var evt in session.Events)
        {
            if (evt.Type != SessionEventTypes.TeamTask) continue;
            if (!evt.Data.TryGetProperty("task", out var taskEl)) continue;
            TeamTask? task;
            try { task = taskEl.Deserialize<TeamTask>(Session.JsonOptions); }
            catch (JsonException) { continue; }
            if (task?.Id is null) continue;
            byId[task.Id] = task;
        }
        return [.. byId.Values.Where(t => t.Status != TeamTaskStatus.Deleted)];
    }

    public TeamTaskView? Get(Session session, string id)
    {
        var task = Tasks(session).FirstOrDefault(t => t.Id == id);
        return task is null ? null : View(task, Tasks(session));
    }

    public List<TeamTaskView> List(
        Session session,
        TeamTaskStatus? status = null,
        string? owner = null,
        bool? unowned = null,
        bool? ready = null,
        int cursor = 0,
        int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 100);
        cursor = Math.Max(0, cursor);
        var all = Tasks(session);
        var views = all.Select(t => View(t, all)).AsEnumerable();
        if (status is not null) views = views.Where(v => v.Task.Status == status);
        if (owner is not null) views = views.Where(v => v.Task.OwnerName == owner);
        if (unowned is not null) views = views.Where(v => (v.Task.OwnerId is null) == unowned.Value);
        if (ready is not null) views = views.Where(v => v.Ready == ready.Value);
        return [.. views.Skip(cursor).Take(limit)];
    }

    public TeamTask Create(
        Session session, string subject, string description,
        IReadOnlyList<string>? blockedBy = null, IReadOnlyList<string>? writeScopes = null)
    {
        subject = subject?.Trim() ?? "";
        if (subject.Length == 0)
            throw new HarnessError("team_invalid_task", "subject must be non-empty.");
        if (Tasks(session).Count >= _maxTasks)
            throw new HarnessError("team_task_limit", $"Team task limit ({_maxTasks}) reached.");

        var task = new TeamTask(
            $"t_{Guid.NewGuid():N}", 1, subject, description ?? "", TeamTaskStatus.Pending,
            null, null, [.. blockedBy ?? []], [.. writeScopes ?? []]);
        Commit(session, task);
        return task;
    }

    /// <summary>Compare-and-set mutation; mirrors the 8 TeamTaskAction verbs.</summary>
    public TeamTask Update(
        Session session, string taskId, int expectedRevision, string action,
        string? subject = null, string? description = null,
        IReadOnlyList<string>? blockedBy = null, IReadOnlyList<string>? writeScopes = null,
        string? owner = null)
    {
        var current = Tasks(session).FirstOrDefault(t => t.Id == taskId)
            ?? throw new HarnessError("team_task_not_found", $"Task '{taskId}' not found.");
        if (current.Revision != expectedRevision)
            throw new HarnessError("team_stale_revision",
                $"Expected revision {expectedRevision}, current is {current.Revision}.");

        var next = current with { Revision = expectedRevision + 1 };
        switch (action)
        {
            case "claim":
                next = next with { Status = TeamTaskStatus.InProgress, OwnerId = owner ?? "lead", OwnerName = owner ?? "lead" };
                break;
            case "release":
                next = next with { Status = TeamTaskStatus.Pending, OwnerId = null, OwnerName = null };
                break;
            case "edit":
                if (subject is not null) next = next with { Subject = subject };
                if (description is not null) next = next with { Description = description };
                if (writeScopes is not null) next = next with { WriteScopes = [.. writeScopes] };
                break;
            case "set_dependencies":
                if (blockedBy is not null) next = next with { BlockedBy = [.. blockedBy] };
                break;
            case "complete":
                next = next with { Status = TeamTaskStatus.Completed };
                break;
            case "reopen":
                next = next with { Status = TeamTaskStatus.Pending };
                break;
            case "reassign":
                next = next with { OwnerId = owner, OwnerName = owner };
                break;
            case "delete":
                next = next with { Status = TeamTaskStatus.Deleted };
                break;
            default:
                throw new HarnessError("team_invalid_action", $"Unknown action '{action}'.");
        }

        if (action is "set_dependencies" && blockedBy is not null && HasCycle(session, taskId, blockedBy))
            throw new HarnessError("team_dependency_cycle", "Dependencies would create a cycle.");

        Commit(session, next);
        return next;
    }

    public void SetMode(Session session, bool enabled) =>
        session.Append(SessionEventTypes.TeamMode, new { enabled });

    public bool IsEnabled(Session session)
    {
        var enabled = false;
        foreach (var evt in session.Events)
            if (evt.Type == SessionEventTypes.TeamMode && evt.Data.TryGetProperty("enabled", out var e))
                enabled = e.ValueKind == JsonValueKind.True;
        return enabled;
    }

    private static TeamTaskView View(TeamTask task, IReadOnlyList<TeamTask> all)
    {
        var ready = task.Status == TeamTaskStatus.Pending &&
                    task.BlockedBy.All(id => all.Any(t => t.Id == id && t.Status == TeamTaskStatus.Completed));
        var warnings = new List<string>();
        if (task.OwnerId is not null && task.WriteScopes.Count > 0)
        {
            foreach (var other in all.Where(t => t.Id != task.Id && t.OwnerId == task.OwnerId && t.Status == TeamTaskStatus.InProgress))
            {
                var overlap = task.WriteScopes.Intersect(other.WriteScopes, StringComparer.Ordinal).ToList();
                if (overlap.Count > 0)
                    warnings.Add($"Write scope overlaps task '{other.Id}': {string.Join(", ", overlap)}");
            }
        }
        return new TeamTaskView(task, ready, warnings);
    }

    private bool HasCycle(Session session, string taskId, IReadOnlyList<string> blockedBy)
    {
        var all = Tasks(session).ToDictionary(t => t.Id, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(blockedBy);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (id == taskId) return true;
            if (!visited.Add(id)) continue;
            if (all.TryGetValue(id, out var t))
                foreach (var dep in t.BlockedBy) stack.Push(dep);
        }
        return false;
    }

    private static void Commit(Session session, TeamTask task) =>
        session.Append(SessionEventTypes.TeamTask, new { version = EventVersion, teamId = session.Id, task });
}
