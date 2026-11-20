using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyAgent.Core;

namespace MyAgent.Goal;

public enum GoalPhase { Active, Paused, Blocked, Complete }

public sealed record GoalBlockReason(string Code, string Message);

/// <summary>Full durable goal snapshot. Every non-clear mutation writes a new revision.</summary>
public sealed record GoalSnapshot(
    string Id,
    int Revision,
    string Objective,
    GoalPhase Phase,
    int MaxGoalRounds,
    GoalBlockReason? BlockedReason = null);

/// <summary>Process-local continuation state; never persisted.</summary>
public enum GoalActivation { Armed, Disarmed }

public sealed record GoalView(
    GoalSnapshot Goal,
    int RoundsStarted,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    GoalActivation Activation)
{
    public string Id => Goal.Id;
    public int Revision => Goal.Revision;
    public string Objective => Goal.Objective;
    public GoalPhase Phase => Goal.Phase;
    public int MaxGoalRounds => Goal.MaxGoalRounds;
    public GoalBlockReason? BlockedReason => Goal.BlockedReason;
}

internal sealed record GoalChange(
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("goal")] GoalSnapshot? Goal,
    [property: JsonPropertyName("roundsStarted")] int RoundsStarted,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

/// <summary>
/// One durable goal per session, folded from <c>goal/change</c> events with compare-and-set
/// revisions. Mirrors the dsh-goal domain (create/edit/pause/resume/complete/block/clear).
/// </summary>
public sealed class GoalService
{
    public const int DefaultMaxGoalRounds = 20;
    public const int DefaultBlockedAfterRounds = 3;

    private readonly int _blockedAfterRounds;
    private readonly ConcurrentDictionary<string, GoalActivation> _activation = new();
    private readonly ConcurrentDictionary<string, bool> _continuationRound = new();

    public GoalService(int blockedAfterRounds = DefaultBlockedAfterRounds)
        => _blockedAfterRounds = blockedAfterRounds;

    /// <summary>Fold the durable event stream into the current goal projection.</summary>
    public GoalView? Get(Session session)
    {
        GoalSnapshot? current = null;
        var rounds = 0;
        var created = DateTimeOffset.MinValue;
        var updated = DateTimeOffset.MinValue;

        foreach (var evt in session.Events)
        {
            if (evt.Type != SessionEventTypes.GoalChange) continue;
            GoalChange? change;
            try { change = evt.Data.Deserialize<GoalChange>(Session.JsonOptions); }
            catch (JsonException) { continue; }
            if (change is null) continue;

            if (change.Operation == "clear")
            {
                if (current is not null && change.Goal is null)
                {
                    current = null; rounds = 0;
                    created = DateTimeOffset.MinValue; updated = change.UpdatedAt;
                }
                continue;
            }
            if (change.Goal is null) continue;
            current = change.Goal;
            rounds = change.RoundsStarted;
            if (created == DateTimeOffset.MinValue) created = change.CreatedAt;
            updated = change.UpdatedAt;
        }

        if (current is null) return null;
        var activation = _activation.GetOrAdd(session.Id, _ => GoalActivation.Disarmed);
        return new GoalView(current, rounds, created, updated, activation);
    }

    public GoalView Create(Session session, string objective, int? maxGoalRounds)
    {
        objective = objective?.Trim() ?? "";
        if (objective.Length == 0)
            throw new HarnessError("GOAL_INVALID_OBJECTIVE", "Objective must be non-empty.");
        if (Get(session) is not null)
            throw new HarnessError("GOAL_ALREADY_EXISTS", "A current goal already exists; clear it first.");

        var cap = maxGoalRounds ?? DefaultMaxGoalRounds;
        if (cap < 1)
            throw new HarnessError("GOAL_INVALID_MAX_ROUNDS", "maxGoalRounds must be positive.");

        var now = DateTimeOffset.UtcNow;
        var snapshot = new GoalSnapshot($"g_{Guid.NewGuid():N}", 1, objective, GoalPhase.Active, cap);
        Commit(session, "create", snapshot, 0, now, now);
        _activation[session.Id] = GoalActivation.Armed;
        return Get(session)!;
    }

    public GoalView Edit(Session session, string id, int revision, string? objective = null, int? maxGoalRounds = null)
    {
        var current = Require(session, id, revision);
        var objectiveChanged = objective is { Length: > 0 };
        var capChanged = maxGoalRounds is > 0;
        if (!objectiveChanged && !capChanged)
            throw new HarnessError("GOAL_INVALID_EDIT", "At least one field must change.");

        var snapshot = current.Goal with
        {
            Revision = revision + 1,
            Objective = objectiveChanged ? objective!.Trim() : current.Objective,
            MaxGoalRounds = capChanged ? maxGoalRounds!.Value : current.MaxGoalRounds,
        };
        Commit(session, "edit", snapshot, current.RoundsStarted, current.CreatedAt, DateTimeOffset.UtcNow);
        return Get(session)!;
    }

    public GoalView Pause(Session session, string id, int revision)
    {
        var current = Require(session, id, revision);
        var snapshot = current.Goal with { Revision = revision + 1, Phase = GoalPhase.Paused };
        Commit(session, "pause", snapshot, current.RoundsStarted, current.CreatedAt, DateTimeOffset.UtcNow);
        _activation[session.Id] = GoalActivation.Disarmed;
        return Get(session)!;
    }

    public GoalView Resume(Session session, string id, int revision)
    {
        var current = Require(session, id, revision);
        var snapshot = current.Goal with
        {
            Revision = revision + 1,
            Phase = GoalPhase.Active,
            BlockedReason = null,
        };
        Commit(session, "resume", snapshot, current.RoundsStarted, current.CreatedAt, DateTimeOffset.UtcNow);
        _activation[session.Id] = GoalActivation.Armed;
        return Get(session)!;
    }

    public GoalView Complete(Session session, string id, int revision)
    {
        var current = Require(session, id, revision);
        var snapshot = current.Goal with { Revision = revision + 1, Phase = GoalPhase.Complete };
        Commit(session, "complete", snapshot, current.RoundsStarted, current.CreatedAt, DateTimeOffset.UtcNow);
        _activation[session.Id] = GoalActivation.Disarmed;
        return Get(session)!;
    }

    public GoalView Block(Session session, string id, int revision, string reason, bool isContinuationRound)
    {
        var current = Require(session, id, revision);
        reason = reason?.Trim() ?? "";
        if (reason.Length == 0)
            throw new HarnessError("GOAL_INVALID_BLOCK_REASON", "blocked_reason is required.");
        if (isContinuationRound && current.RoundsStarted < _blockedAfterRounds)
            throw new HarnessError(
                "GOAL_TOOL_BLOCK_THRESHOLD",
                $"blocked requires at least {_blockedAfterRounds} consecutive goal rounds; current round is {current.RoundsStarted}.");

        var snapshot = current.Goal with
        {
            Revision = revision + 1,
            Phase = GoalPhase.Blocked,
            BlockedReason = new GoalBlockReason("model-reported", reason),
        };
        Commit(session, "block", snapshot, current.RoundsStarted, current.CreatedAt, DateTimeOffset.UtcNow);
        _activation[session.Id] = GoalActivation.Disarmed;
        return Get(session)!;
    }

    public void Clear(Session session)
    {
        var current = Get(session);
        if (current is null) return;
        session.Append(SessionEventTypes.GoalChange, new
        {
            operation = "clear",
            goal = (GoalSnapshot?)null,
            roundsStarted = 0,
            createdAt = current.CreatedAt,
            updatedAt = DateTimeOffset.UtcNow,
        });
        _activation[session.Id] = GoalActivation.Disarmed;
    }

    /// <summary>Admit one automatic continuation round; returns the round number, or null when not allowed.</summary>
    public int? AdmitRound(Session session)
    {
        var current = Get(session);
        if (current is null || current.Phase != GoalPhase.Active) return null;
        if (_activation.GetValueOrDefault(session.Id) != GoalActivation.Armed) return null;
        if (current.RoundsStarted >= current.MaxGoalRounds) return null;

        var next = current.RoundsStarted + 1;
        Commit(session, "round", current.Goal, next, current.CreatedAt, DateTimeOffset.UtcNow);
        _continuationRound[session.Id] = true;
        return next;
    }

    public bool IsContinuationRound(Session session) =>
        _continuationRound.TryGetValue(session.Id, out var value) && value;

    public void EndContinuationRound(Session session) => _continuationRound[session.Id] = false;

    /// <summary>
    /// Mirrors requireDirectHuman: there must be a real user message inside the current open turn,
    /// and the session must not be a subagent tree origin.
    /// </summary>
    public static bool IsDirectHuman(Session session)
    {
        if (session.Origin == "subagent") return false;
        var lastTurnStart = session.Events.LastOrDefault(e => e.Type == SessionEventTypes.TurnStart);
        if (lastTurnStart is null) return false;
        return session.Events.Any(e =>
            e.Seq > lastTurnStart.Seq &&
            e.Type == SessionEventTypes.UserMessage &&
            (!e.Data.TryGetProperty("source", out var src) || src.GetString() == "user"));
    }

    /// <summary>Wrap-up notice injected after complete/blocked during a goal round.</summary>
    public static string RenderWrapup(string objective, string? blockedReason = null) =>
        blockedReason is null
            ? $"<goal_complete>\nObjective: {System.Text.Json.JsonSerializer.Serialize(objective)}\n" +
              "Write your closing message to the user now; do not call further tools this turn.\n</goal_complete>"
            : $"<goal_blocked>\nObjective: {System.Text.Json.JsonSerializer.Serialize(objective)}\n" +
              $"Blocked: {System.Text.Json.JsonSerializer.Serialize(blockedReason)}\n" +
              "Write your closing message to the user now; do not call further tools this turn.\n</goal_blocked>";

    /// <summary>Mark a goal disarmed after a host restart (resume/fork semantics).</summary>
    public void Disarm(Session session) => _activation[session.Id] = GoalActivation.Disarmed;

    private GoalView Require(Session session, string id, int revision)
    {
        var current = Get(session) ?? throw new HarnessError("GOAL_NOT_FOUND", "No current goal.");
        if (current.Id != id)
            throw new HarnessError("GOAL_NOT_FOUND", $"Goal '{id}' is not the current goal.");
        if (current.Revision != revision)
            throw new HarnessError("GOAL_STALE_REVISION",
                $"Expected revision {revision}, current is {current.Revision}.");
        return current;
    }

    private static void Commit(Session session, string operation, GoalSnapshot goal, int rounds, DateTimeOffset createdAt, DateTimeOffset now) =>
        session.Append(SessionEventTypes.GoalChange, new
        {
            operation,
            goal,
            roundsStarted = rounds,
            createdAt = createdAt,
            updatedAt = now,
        });
}
