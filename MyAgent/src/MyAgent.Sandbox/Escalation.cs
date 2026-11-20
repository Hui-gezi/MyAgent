using MyAgent.Core;

namespace MyAgent.Sandbox;

/// <summary>
/// The escalation vocabulary and choreography shared by every sandbox-enforcing tool family
/// (mirrors dsh-sandbox/escalation): the strictly-wider ladder, argument-pairing validation,
/// model-facing denial/hint markers, and ApproveEscalationAsync — the ordered fail-closed
/// sequence resolving a sandbox_permissions request through an approval channel BEFORE
/// anything executes.
/// </summary>
public static class Escalation
{
    public const string InvalidArgument = "SANDBOX_ESCALATION_INVALID_ARGUMENT";
    public const string Unavailable = "SANDBOX_ESCALATION_UNAVAILABLE";
    public const string Rejected = "SANDBOX_ESCALATION_REJECTED";
    public const string Cancelled = "SANDBOX_ESCALATION_CANCELLED";

    /// <summary>The strictly-wider table, checked at EXECUTION against the call's effective mode.</summary>
    public static IReadOnlyList<SandboxMode> WiderModes(SandboxMode effective) => effective switch
    {
        SandboxMode.ReadOnly => [SandboxMode.WorkspaceWrite, SandboxMode.DangerFullAccess],
        SandboxMode.WorkspaceWrite => [SandboxMode.DangerFullAccess],
        _ => [],
    };

    /// <summary>The closed escalation-target vocabulary advertised in tool schemas.</summary>
    public static readonly IReadOnlyList<SandboxMode> Targets = [SandboxMode.WorkspaceWrite, SandboxMode.DangerFullAccess];

    /// <summary>Validate the argument pairing a tool schema cannot express.</summary>
    public static void ValidateArgs(string? sandboxPermissions, string? justification)
    {
        if (sandboxPermissions is not null && justification is null)
            throw new HarnessError(InvalidArgument, "invalid escalation: sandbox_permissions requires a justification");
        if (justification is not null && sandboxPermissions is null)
            throw new HarnessError(InvalidArgument, "invalid escalation: justification is only valid together with sandbox_permissions");
        if (justification is not null && justification.Trim().Length == 0)
            throw new HarnessError(InvalidArgument, "invalid justification: expected a non-empty sentence");
    }

    /// <summary>The model-facing denial marker — one vocabulary both enforcing families report.</summary>
    public static string DenialMarker(SandboxMode mode) =>
        $"[sandbox: file access denied under {mode.Wire()} mode]";

    /// <summary>The same-turn escalation hint riding a denial (subject: command / operation).</summary>
    public static string HintMarker(string subject) =>
        $"[sandbox: escalation available — retry this exact {subject} once with sandbox_permissions (the narrowest wider mode that suffices) + justification; the approval prompt asks the user]";

    /// <summary>The model-facing sandbox_permissions parameter description.</summary>
    public static string PermissionsDescription(string subject) =>
        $"The narrowest wider sandbox mode for a one-shot retry of the exact {subject} the sandbox just denied; the retry asks the user for approval.";

    /// <summary>The justification parameter description shared by both families.</summary>
    public static string JustificationDescription(string subject) =>
        $"Required with sandbox_permissions: one sentence for the user explaining why this exact {subject} needs the wider access. Use the language of the user's current request.";

    /// <summary>
    /// Resolve a sandbox permission request before execution. Repeating the call's effective
    /// mode returns it without approval. A strictly wider mode requires approval and applies
    /// only to this call. Non-grant outcomes throw before execution (fail closed).
    /// </summary>
    public static async Task<SandboxMode> ApproveAsync(
        SandboxMode requestedMode,
        string justification,
        SandboxMode effectiveMode,
        string subject,
        IEscalationApprover? approver,
        Session? session,
        string toolName,
        string? callId,
        CancellationToken signal)
    {
        if (requestedMode == effectiveMode) return effectiveMode;
        if (!WiderModes(effectiveMode).Contains(requestedMode))
            throw new HarnessError(InvalidArgument,
                $"sandbox escalation to \"{requestedMode.Wire()}\" is not strictly wider than this call's current \"{effectiveMode.Wire()}\" mode");
        if (approver is null)
            throw new HarnessError(Unavailable,
                $"sandbox escalation to \"{requestedMode.Wire()}\" requires approval, but no approval service is composed");
        if (session is null)
            throw new HarnessError(Unavailable,
                $"sandbox escalation to \"{requestedMode.Wire()}\" requires approval, but the call has no agent to route it through");

        var outcome = await approver.RequestAsync(new ApprovalRequest(
            session, toolName, callId,
            $"escalate sandbox to {requestedMode.Wire()}: {justification}", signal));

        switch (outcome)
        {
            case ApprovalOutcome.AllowedOnce:
                return requestedMode;
            case ApprovalOutcome.Rejected:
                throw new HarnessError(Rejected,
                    $"the user rejected escalating this {subject} to \"{requestedMode.Wire()}\"; it stays denied, so stop and explain instead of working around it");
            case ApprovalOutcome.Cancelled:
                signal.ThrowIfCancellationRequested();
                throw new HarnessError(Cancelled, $"approval for escalating to \"{requestedMode.Wire()}\" was cancelled");
            default:
                throw new HarnessError(Unavailable,
                    $"sandbox escalation to \"{requestedMode.Wire()}\" requires approval, but no approval channel is available");
        }
    }
}

/// <summary>
/// The minimal structural approval-request shape ApproveAsync needs — structurally the
/// approval seam, so the sandbox package never depends on the approval service project.
/// </summary>
public interface IEscalationApprover
{
    Task<ApprovalOutcome> RequestAsync(ApprovalRequest request);
}
