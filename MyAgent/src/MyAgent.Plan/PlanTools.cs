using System.Text.Json;
using MyAgent.Core;

namespace MyAgent.Plan;

/// <summary>
/// Reviewed exit for plan mode, mirroring dsh-plan-mode: the tool submits the plan through the
/// host's userQuestions service and awaits a human decision. Approve leaves plan mode; dismiss
/// or feedback keeps planning. The tool call blocks until the review resolves.
/// </summary>
public sealed class ExitPlanModeTool(PlanService plans, IQuestionService questions) : ITool
{
    public const string ApproveLabel = "Approve";
    public const string KeepPlanningLabel = "Keep planning";
    public const string ReviewId = "plan-review";

    public string Name => "exit_plan_mode";
    public string Description =>
        "Present the complete plan for user review and leave plan mode on approval. " +
        "The plan must be Markdown starting with a '# ' heading that names it. " +
        "The call waits for the user's decision.";
    public JsonElement InputSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "plan": { "type": "string", "description": "The complete plan as Markdown, starting with a # heading." }
          },
          "required": ["plan"],
          "additionalProperties": false
        }
        """).RootElement;

    /// <summary>Raised when a plan is submitted so the UI can preview it before the decision.</summary>
    public event Action<string, string>? PlanSubmitted;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        var plan = args.TryGetProperty("plan", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? "" : "";
        if (plan.Length == 0 || !plan.TrimStart().StartsWith('#'))
            return ToolResult.Error("plan must be non-empty Markdown starting with a '# ' heading.", "invalid_args");
        if (!plans.IsActive(context.Session))
            return ToolResult.Error("Plan mode is not active.", "PLAN_NOT_ACTIVE");

        PlanSubmitted?.Invoke(context.Session.Id, plan);
        context.Session.Append(SessionEventTypes.PlanSubmitted, new { plan, status = "pending" });

        var request = new QuestionRequest(
            Id: ReviewId,
            Header: "Plan review",
            Question: "Approve this plan and leave plan mode?",
            Detail: plan,
            Options:
            [
                new QuestionOption(ApproveLabel, "Leave plan mode; the plan is carried out from the next step."),
                new QuestionOption(KeepPlanningLabel, "Stay in plan mode; feedback goes back to the model."),
            ]);

        string? decision;
        try { decision = await questions.AskAsync(request, cancellationToken); }
        catch (OperationCanceledException) { decision = null; }

        if (decision == ApproveLabel)
        {
            plans.Set(context.Session, false);
            context.Session.Append(SessionEventTypes.PlanSubmitted, new { plan, status = "approved" });
            return new ToolResult("{\"approved\":true} — Plan approved; plan mode exited.");
        }

        context.Session.Append(SessionEventTypes.PlanSubmitted, new { plan, status = "revision" });
        return new ToolResult("{\"approved\":false} — The user dismissed the review to reply in their own words; plan mode remains active.");
    }
}
