using System.Collections.Concurrent;
using System.Text.Json;
using MyAgent.Core;

namespace MyAgent.Plan;

/// <summary>
/// Wire view of the plan projection, folded from the session log. <c>Pending</c> is true while a
/// selection targets a state other than the logged one. Mirrors dsh-plan-mode's PlanProjection.
/// </summary>
public sealed record PlanProjection(bool Active, bool Pending);

/// <summary>Outcome of a user selection (mirrors dsh-plan-mode PlanModeController.set).</summary>
public enum PlanSelectionOutcome
{
    /// <summary>No open turn: the change was appended to the log immediately.</summary>
    Committed,
    /// <summary>Open turn: the selection awaits the next accepted in-turn pre-step.</summary>
    Queued,
    /// <summary>An opposite pending selection was cleared; the logged state already matches.</summary>
    Cancelled,
    /// <summary>Already in (or already headed to) that state.</summary>
    Noop,
}

/// <summary>
/// Plan-mode collaboration state. The last <c>plan/mode</c> event wins; a log with none folds to
/// inactive. Between turns a selection commits immediately; during an open turn it stays pending
/// until an accepted in-turn pre-step commits it (mirrors dsh-plan-mode).
/// </summary>
public sealed class PlanService
{
    private readonly ConcurrentDictionary<string, bool> _pendingSelection = new();

    /// <summary>Fold the durable event stream into the current plan projection.</summary>
    public PlanProjection Get(Session session)
    {
        var active = false;
        foreach (var evt in session.Events)
        {
            if (evt.Type != SessionEventTypes.PlanMode) continue;
            if (evt.Data.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True)
                active = true;
            else if (evt.Data.TryGetProperty("active", out var a2) && a2.ValueKind == JsonValueKind.False)
                active = false;
        }

        var pending = _pendingSelection.TryGetValue(session.Id, out var wanted) && wanted != active;
        return new PlanProjection(active, pending);
    }

    public bool IsActive(Session session) => Get(session).Active;

    /// <summary>Stage a toggle; it is committed as a plan/mode event on the next in-turn pre-step.</summary>
    public void Select(Session session, bool wanted) => _pendingSelection[session.Id] = wanted;

    /// <summary>
    /// User selection with harness set() semantics: between turns (no open turn) the change
    /// commits IMMEDIATELY — waiting for a pre-step that will never run is the bug where the
    /// UI toggle appeared dead. During an open turn the selection stays pending until the
    /// next accepted in-turn pre-step. Selecting the current or already-pending target is a
    /// no-op; an opposite pending selection is cancelled.
    /// </summary>
    public PlanSelectionOutcome SetSelected(Session session, bool wanted, bool hasOpenTurn)
    {
        var projection = Get(session);
        var target = projection.Pending
            ? _pendingSelection[session.Id]
            : projection.Active;
        if (wanted == target) return PlanSelectionOutcome.Noop;
        if (hasOpenTurn)
        {
            _pendingSelection[session.Id] = wanted;
            return projection.Active == wanted ? PlanSelectionOutcome.Cancelled : PlanSelectionOutcome.Queued;
        }
        // No open turn: commit now. Append BEFORE deleting so a failed durable write
        // leaves the selection retryable, not dropped.
        if (wanted == projection.Active)
        {
            _pendingSelection.TryRemove(session.Id, out _);
            return PlanSelectionOutcome.Cancelled;
        }
        session.Append(SessionEventTypes.PlanMode, new { active = wanted });
        _pendingSelection.TryRemove(session.Id, out _);
        return PlanSelectionOutcome.Committed;
    }

    /// <summary>Commit any pending selection. Called from the Agent pre-step hook.</summary>
    public void CommitPending(Session session)
    {
        if (!_pendingSelection.TryRemove(session.Id, out var wanted)) return;
        var current = Get(session);
        if (current.Active == wanted) return;
        session.Append(SessionEventTypes.PlanMode, new { active = wanted });
    }

    /// <summary>Enter or leave plan mode immediately (used by human commands and the exit tool).</summary>
    public void Set(Session session, bool active)
    {
        _pendingSelection.TryRemove(session.Id, out _);
        if (Get(session).Active == active) return;
        session.Append(SessionEventTypes.PlanMode, new { active });
    }

    public string PolicySection => """
        Plan mode is active. Explore and design before executing; do not modify files or run mutating commands yet.
        When the plan is ready, call exit_plan_mode with the complete plan as Markdown starting with a "#" heading.
        The user will approve it or ask you to keep planning. Plan mode guides rather than restricts: tools remain available.
        """;
}
