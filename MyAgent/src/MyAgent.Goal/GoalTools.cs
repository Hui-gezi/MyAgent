using System.Text.Json;
using MyAgent.Core;

namespace MyAgent.Goal;

internal static class GoalJson
{
    public static object? Value(GoalView? goal) => goal is null
        ? new { goal = (object?)null }
        : new
        {
            goal = new
            {
                id = goal.Id,
                revision = goal.Revision,
                objective = goal.Objective,
                phase = PhaseName(goal.Phase),
                roundsStarted = goal.RoundsStarted,
                maxGoalRounds = goal.MaxGoalRounds,
                blockedReason = goal.BlockedReason is null ? null : new
                {
                    code = goal.BlockedReason.Code,
                    message = goal.BlockedReason.Message,
                },
            },
            activation = goal.Activation == GoalActivation.Armed ? "armed" : "disarmed",
        };

    public static string PhaseName(GoalPhase phase) => phase switch
    {
        GoalPhase.Active => "active",
        GoalPhase.Paused => "paused",
        GoalPhase.Blocked => "blocked",
        GoalPhase.Complete => "complete",
        _ => "active",
    };

    public static string Serialize(GoalView? goal) =>
        JsonSerializer.Serialize(Value(goal), Session.JsonOptions);
}

public sealed class GetGoalTool(GoalService goals) : ITool
{
    public string Name => "get_goal";
    public string Description =>
        "Read the current session goal, including the id and revision that update_goal requires.";
    public JsonElement InputSchema => JsonDocument.Parse("""
        { "type": "object", "properties": {}, "additionalProperties": false }
        """).RootElement;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new ToolResult(GoalJson.Serialize(goals.Get(context.Session))));
}

public sealed class CreateGoalTool(GoalService goals) : ITool
{
    public string Name => "create_goal";
    public string Description =>
        "Create a persisted goal that keeps this session working across automatic continuation rounds. " +
        "Use it when the direct human request is a long-running objective, even if the user did not say 'goal'; " +
        "not for single-turn work.";
    public JsonElement InputSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "objective": { "type": "string", "description": "The concrete completion objective." },
            "max_goal_rounds": { "type": "integer", "description": "Optional positive limit on continuation rounds." }
          },
          "required": ["objective"],
          "additionalProperties": false
        }
        """).RootElement;
    public string PromptSection =>
        "create_goal may infer goal intent from a direct human request in any language. " +
        "Use it for long-running objectives; do not use it for single-turn work.";
    public int PromptOrder => 1300;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        var objective = args.TryGetProperty("objective", out var o) ? o.GetString() : null;
        int? cap = null;
        if (args.TryGetProperty("max_goal_rounds", out var m) && m.ValueKind == JsonValueKind.Number)
            cap = m.GetInt32();
        try
        {
            var goal = goals.Create(context.Session, objective ?? "", cap);
            return Task.FromResult(new ToolResult(GoalJson.Serialize(goal)));
        }
        catch (HarnessError ex)
        {
            return Task.FromResult(ToolResult.Error(ex.Message, ex.Code));
        }
    }
}

public sealed class UpdateGoalTool(GoalService goals) : ITool
{
    private static readonly HashSet<string> Actions =
        ["edit", "pause", "resume", "complete", "blocked"];

    public string Name => "update_goal";
    public string Description =>
        "Update the current goal. edit/pause/resume require a direct top-level human request. " +
        "complete and blocked are also allowed during automatic continuation of this goal; " +
        $"blocked is rejected before {GoalService.DefaultBlockedAfterRounds} consecutive goal rounds.";
    public JsonElement InputSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "goal_id": { "type": "string", "description": "Exact id returned by get_goal." },
            "revision": { "type": "integer", "description": "Exact positive revision returned by get_goal." },
            "action": { "type": "string", "enum": ["edit", "pause", "resume", "complete", "blocked"] },
            "objective": { "type": "string", "description": "Replacement objective; valid only with edit." },
            "max_goal_rounds": { "type": "integer", "description": "Replacement cap; valid only with edit." },
            "blocked_reason": { "type": "string", "description": "Required only with blocked." }
          },
          "required": ["goal_id", "revision", "action"],
          "additionalProperties": false
        }
        """).RootElement;
    public int PromptOrder => 1310;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var id = GetString(args, "goal_id") ?? "";
            var revision = GetInt(args, "revision") ?? 0;
            var action = GetString(args, "action") ?? "";
            if (!Actions.Contains(action))
                return Task.FromResult(ToolResult.Error($"Unknown action '{action}'.", "GOAL_TOOL_INVALID_UPDATE"));
            if (id.Length == 0 || revision < 1)
                return Task.FromResult(ToolResult.Error(
                    "goal_id must be non-empty and revision must be positive.", "GOAL_TOOL_INVALID_UPDATE"));

            var isContinuation = goals.IsContinuationRound(context.Session);
            var directHuman = GoalService.IsDirectHuman(context.Session);
            var objective = GetString(args, "objective");
            int? cap = GetInt(args, "max_goal_rounds");
            var reason = GetString(args, "blocked_reason");

            // edit/pause/resume require a direct top-level human request.
            if (action is "edit" or "pause" or "resume" && !directHuman)
                return Task.FromResult(ToolResult.Error(
                    $"action '{action}' requires a direct human request.", "GOAL_TOOL_AUTHORITY_REQUIRED"));

            // The model cannot resume a paused goal; a human must do it.
            if (action == "resume")
            {
                var current = goals.Get(context.Session);
                if (current?.Phase == GoalPhase.Paused)
                    return Task.FromResult(ToolResult.Error(
                        "the model cannot resume a paused goal; the user must resume it.",
                        "GOAL_TOOL_RESUME_PAUSED"));
            }

            // complete/blocked require direct human OR this exact goal-continuation round.
            if (action is "complete" or "blocked" && !directHuman && !isContinuation)
                return Task.FromResult(ToolResult.Error(
                    $"action '{action}' requires direct human or goal-round authority.",
                    "GOAL_TOOL_AUTHORITY_REQUIRED"));

            GoalView goal = action switch
            {
                "edit" => goals.Edit(context.Session, id, revision, objective, cap),
                "pause" => goals.Pause(context.Session, id, revision),
                "resume" => goals.Resume(context.Session, id, revision),
                "complete" => goals.Complete(context.Session, id, revision),
                "blocked" => goals.Block(context.Session, id, revision, reason ?? "", isContinuation),
                _ => throw new HarnessError("GOAL_TOOL_INVALID_UPDATE", "Unsupported action."),
            };

            // During an automatic continuation, inject the wrap-up notice (deferContext equivalent).
            if (isContinuation && action is "complete" or "blocked")
                context.Session.Append(SessionEventTypes.UserMessage, new
                {
                    text = GoalService.RenderWrapup(goal.Objective, action == "blocked" ? reason : null),
                    source = "tool-goal",
                });

            return Task.FromResult(new ToolResult(GoalJson.Serialize(goal)));
        }
        catch (HarnessError ex)
        {
            return Task.FromResult(ToolResult.Error(ex.Message, ex.Code));
        }
    }

    private static string? GetString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}

/// <summary>
/// Hooks the Agent's after-turn stage so an active, armed goal admits the next continuation round.
/// Mirrors dsh-goal-round-driver's renderGoalRoundPrompt / automatic Round admission.
/// </summary>
public sealed class GoalRoundDriver(GoalService goals)
{
    public const string Source = "goal-round";

    public Task<AfterTurnAction?> OnAfterTurnAsync(Session session, string stopReason, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // A continuation round just finished: clear the marker after it produced its turn.
        if (goals.IsContinuationRound(session))
        {
            goals.EndContinuationRound(session);
            return Task.FromResult<AfterTurnAction?>(null);
        }

        var round = goals.AdmitRound(session);
        if (round is null) return Task.FromResult<AfterTurnAction?>(null);

        var goal = goals.Get(session)!;
        var prompt = "<goal_round>\n"
            + $"Objective: {JsonSerializer.Serialize(goal.Objective)}\n"
            + $"Round: {round}/{goal.MaxGoalRounds}\n\n"
            + "Continue working toward the objective in this same session. Treat the workspace, tool results, "
            + "and durable session state as authoritative. Make concrete progress and verify the result. "
            + "Before claiming completion, gather evidence that the whole objective is achieved, read the current "
            + "goal, and mark it complete. If work remains, leave the goal active for the next round.\n"
            + "</goal_round>";
        return Task.FromResult<AfterTurnAction?>(new AfterTurnAction(prompt, Source));
    }
}
