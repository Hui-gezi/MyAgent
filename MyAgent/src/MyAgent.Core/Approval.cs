namespace MyAgent.Core;

/// <summary>
/// Closed approval outcomes (mirrors dsh-user-approval ApprovalOutcome): a one-shot grant,
/// explicit rejection, withdrawn request, or unavailable answerer. Callers fail closed on Unavailable.
/// Wire-safe vocabulary shared by the approval service and the sandbox escalation seam.
/// </summary>
public enum ApprovalOutcome
{
    AllowedOnce,
    Rejected,
    Cancelled,
    Unavailable,
}

public static class ApprovalOutcomes
{
    public static string Wire(this ApprovalOutcome outcome) => outcome switch
    {
        ApprovalOutcome.AllowedOnce => "allowed-once",
        ApprovalOutcome.Rejected => "rejected",
        ApprovalOutcome.Cancelled => "cancelled",
        ApprovalOutcome.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    public static bool TryParse(string? value, out ApprovalOutcome outcome)
    {
        switch (value)
        {
            case "allowed-once": outcome = ApprovalOutcome.AllowedOnce; return true;
            case "rejected": outcome = ApprovalOutcome.Rejected; return true;
            case "cancelled": outcome = ApprovalOutcome.Cancelled; return true;
            case "unavailable": outcome = ApprovalOutcome.Unavailable; return true;
            default: outcome = ApprovalOutcome.Unavailable; return false;
        }
    }
}

/// <summary>
/// A session's approval policy (mirrors dsh-user-approval ApprovalPolicy):
/// Ask delegates to the composed answerers (fail-closed with none);
/// Never auto-rejects every ask deterministically (the headless/CI stance).
/// </summary>
public enum ApprovalPolicy
{
    Ask,
    Never,
}

public static class ApprovalPolicies
{
    public static string Wire(this ApprovalPolicy policy) => policy switch
    {
        ApprovalPolicy.Ask => "ask",
        ApprovalPolicy.Never => "never",
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };

    public static bool TryParse(string? value, out ApprovalPolicy policy)
    {
        switch (value)
        {
            case "ask": policy = ApprovalPolicy.Ask; return true;
            case "never": policy = ApprovalPolicy.Never; return true;
            default: policy = ApprovalPolicy.Ask; return false;
        }
    }
}

/// <summary>
/// One readonly same-process approval question. CallId links to an already presented
/// tool call so arguments are not duplicated. The session routes the question and
/// receives the approval/asked + approval/decided audit pair on its log.
/// </summary>
public sealed record ApprovalRequest(
    Session Session,
    string ToolName,
    string? CallId = null,
    string? Reason = null,
    CancellationToken Signal = default);
