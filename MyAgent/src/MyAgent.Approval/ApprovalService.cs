using MyAgent.Core;
using MyAgent.Sandbox;

namespace MyAgent.Approval;

/// <summary>
/// The composed answerer seam (the C# equivalent of the approval/request waterfall):
/// the host UI answers questions for the agents it owns. Returning a non-vocabulary value
/// is impossible with the closed enum; a throwing answerer fails the QUESTION closed.
/// </summary>
public interface IApprovalAnswerer
{
    Task<ApprovalOutcome> AnswerAsync(ApprovalRequest request);
}

/// <summary>
/// Approval service (mirrors dsh-user-approval ApprovalService): applies session policy
/// BEFORE answerers and logs every ask/outcome pair to the requesting session. The session
/// log is the policy store — the LAST approval/policy event is the session's override, so it
/// survives restart by replay. Missing answerers fail closed; grants apply only to the
/// requested action (allowed-once).
/// </summary>
public sealed class ApprovalService(ApprovalPolicy defaultPolicy = ApprovalPolicy.Ask) : IEscalationApprover
{
    /// <summary>Model-facing statement for the deterministic 'never' policy.</summary>
    public const string NeverSentence =
        "Approval prompts are disabled in this session: actions that require approval are rejected automatically — do not request sandbox escalation (do not set `sandbox_permissions`).";

    /// <summary>Model-facing statement for an interactive policy that may still fail closed.</summary>
    public const string AskSentence =
        "Approval prompts are enabled in this session: operations that require approval may ask through the configured answerers; without an available answerer, the request fails closed.";

    private static readonly IReadOnlyDictionary<ApprovalPolicy, string> PolicyNoticeNames =
        new Dictionary<ApprovalPolicy, string>
        {
            [ApprovalPolicy.Ask] = "ask the user for approval",
            [ApprovalPolicy.Never] = "never ask the user for approval",
        };

    /// <summary>The deployment default policy — the fallback beneath a session override.</summary>
    public ApprovalPolicy DefaultPolicy { get; } = defaultPolicy;

    private IApprovalAnswerer? _answerer;

    /// <summary>Compose (or remove) the single answerer. With none, every ask fails closed.</summary>
    public void SetAnswerer(IApprovalAnswerer? answerer) => _answerer = answerer;

    /// <summary>
    /// THE write path for a session's approval-policy override: appends exactly one log-only
    /// approval/policy event. Invalid values cannot be expressed (closed enum).
    /// </summary>
    public static void SetPolicy(Session session, ApprovalPolicy policy, string? source = null) =>
        session.Append(SessionEventTypes.ApprovalPolicy,
            source is null ? new { policy = policy.Wire() } : new { policy = policy.Wire(), source });

    /// <summary>Read the session override without applying the configured default.</summary>
    public ApprovalPolicy? OverrideOf(Session session)
    {
        var events = session.Events; // one snapshot; indexing the property copies per access
        for (var i = events.Count - 1; i >= 0; i--)
        {
            var evt = events[i];
            if (evt.Type != SessionEventTypes.ApprovalPolicy) continue;
            if (evt.Data.TryGetProperty("policy", out var p) && ApprovalPolicies.TryParse(p.GetString(), out var policy))
                return policy;
        }
        return null;
    }

    /// <summary>The policy every ask for this session resolves under right now.</summary>
    public ApprovalPolicy EffectivePolicy(Session session) => OverrideOf(session) ?? DefaultPolicy;

    /// <summary>
    /// Switch one live agent's policy and queue the transition notice for its next model step
    /// (mirrors ApprovalService.setPolicy). Session initialization uses SetPolicy directly
    /// because there is no previously visible policy to change.
    /// </summary>
    public void SwitchPolicy(Agent agent, ApprovalPolicy policy)
    {
        var previous = EffectivePolicy(agent.Session);
        if (previous == policy) return;
        SetPolicy(agent.Session, policy);
        agent.Inject(
            $"The user changed the approval policy from \"{PolicyNoticeNames[previous]}\" to \"{PolicyNoticeNames[policy]}\".",
            "user-approval");
    }

    /// <summary>The policy sentence for the model's runtime-context snapshot.</summary>
    public string RenderPolicyContext(Session session) =>
        EffectivePolicy(session) == ApprovalPolicy.Never ? NeverSentence : AskSentence;

    /// <summary>
    /// Ask the composed answerer to decide one request. Requires an open turn: the
    /// approval/asked + approval/decided audit pair must be turn-enclosed (a bare event
    /// between turns is crash-tail garbage on reload). The answerer phase ALWAYS produces
    /// an outcome: an aborted signal yields cancelled, a missing or throwing answerer yields
    /// unavailable (fail closed). Exactly one decided event follows every asked event.
    /// </summary>
    public async Task<ApprovalOutcome> RequestAsync(ApprovalRequest request)
    {
        var session = request.Session;
        if (!TurnOpen(session))
            throw new HarnessError("approval_outside_turn",
                "approval.RequestAsync() outside an open turn: the approval/asked + approval/decided audit pair "
                + "must be turn-enclosed. Ask from inside the turn that needs the decision.");

        var id = Guid.NewGuid().ToString("N");
        session.Append(SessionEventTypes.ApprovalAsked, new
        {
            id,
            toolName = request.ToolName,
            callId = request.CallId,
            reason = request.Reason,
        });
        var outcome = await DecideAsync(request, session);
        session.Append(SessionEventTypes.ApprovalDecided, new { id, outcome = outcome.Wire() });
        return outcome;
    }

    /// <summary>Fold turn enclosure from the durable log (last turn/start vs turn/end).</summary>
    private static bool TurnOpen(Session session)
    {
        var events = session.Events; // one snapshot; indexing the property copies per access
        for (var i = events.Count - 1; i >= 0; i--)
        {
            var type = events[i].Type;
            if (type == SessionEventTypes.TurnStart) return true;
            if (type == SessionEventTypes.TurnEnd) return false;
        }
        return false;
    }

    /// <summary>
    /// Dispatch to the answerer, contained and raced against the request signal. The 'never'
    /// policy is decided HERE, before any dispatch, so it rejects deterministically regardless
    /// of answerer registration order. A late answer after an abort won the race is discarded.
    /// </summary>
    private async Task<ApprovalOutcome> DecideAsync(ApprovalRequest request, Session session)
    {
        var signal = request.Signal;
        if (signal.IsCancellationRequested) return ApprovalOutcome.Cancelled;
        if (EffectivePolicy(session) == ApprovalPolicy.Never) return ApprovalOutcome.Rejected;
        var answerer = _answerer;
        if (answerer is null) return ApprovalOutcome.Unavailable;

        var settled = new TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = signal.Register(() => settled.TrySetResult(ApprovalOutcome.Cancelled));
        _ = AnswerContainedAsync(answerer, request, settled);
        return await settled.Task;
    }

    private static async Task AnswerContainedAsync(
        IApprovalAnswerer answerer, ApprovalRequest request, TaskCompletionSource<ApprovalOutcome> settled)
    {
        try
        {
            // A throwing answerer must fail the QUESTION closed, not the caller's tool call open.
            settled.TrySetResult(await answerer.AnswerAsync(request));
        }
        catch
        {
            settled.TrySetResult(ApprovalOutcome.Unavailable);
        }
    }
}
