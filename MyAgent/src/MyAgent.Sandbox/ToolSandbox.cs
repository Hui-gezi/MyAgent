using System.Text.Json;
using System.Text.Json.Nodes;
using MyAgent.Core;

namespace MyAgent.Sandbox;

/// <summary>
/// The sandbox API shared by every enforcing tool family (mirrors dsh-tool-fs/sandbox
/// FsSandboxController and tool-bash's escalation glue): advertisement gating, per-call
/// policy resolution, the one-approved wider retry, and denial-marker mapping. Built ONCE
/// per composition and shared by bash / write / edit so they escalate identically.
/// </summary>
public sealed class ToolSandbox(
    SandboxPolicyService policy,
    ISandboxProvider? provider,
    IEscalationApprover? approver)
{
    /// <summary>The escalation targets this composition advertises ([] when no confining backend is mounted).</summary>
    public IReadOnlyList<SandboxMode> EscalationModes { get; } = provider is null ? [] : Escalation.Targets;

    /// <summary>Whether a confining process backend is mounted (bash confinement).</summary>
    public bool ConfinesProcesses => provider is not null;

    /// <summary>
    /// Spread the two escalation parameters into a tool schema when a confining backend is
    /// mounted. The enum pins the closed target vocabulary; the strict-wider check happens
    /// per call at execution (schemas are registry-global, the effective mode is per-call truth).
    /// </summary>
    public JsonElement AddEscalationFields(JsonElement baseSchema, string subject)
    {
        if (EscalationModes.Count == 0) return baseSchema;
        var node = JsonNode.Parse(baseSchema.GetRawText())!.AsObject();
        var properties = node["properties"]!.AsObject();
        properties["sandbox_permissions"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(EscalationModes.Select(m => (JsonNode)m.Wire()).ToArray()),
            ["description"] = Escalation.PermissionsDescription(subject),
        };
        properties["justification"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = Escalation.JustificationDescription(subject),
        };
        return JsonDocument.Parse(node.ToJsonString()).RootElement;
    }

    /// <summary>
    /// The policy to stamp onto this call: an approved escalation grant (a strictly wider
    /// retry resolved through the approval channel BEFORE anything executes), else the
    /// session's standing mode. Repeating the effective mode needs no approval.
    /// </summary>
    public async Task<SandboxExecutionPolicy> ResolvePolicyAsync(
        string toolName, JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        var requested = args.TryGetProperty("sandbox_permissions", out var sp) && sp.ValueKind == JsonValueKind.String
            ? sp.GetString()
            : null;
        var justification = args.TryGetProperty("justification", out var j) && j.ValueKind == JsonValueKind.String
            ? j.GetString()
            : null;

        // The schema omits both fields without a confining backend; reject them before pairing checks.
        if (EscalationModes.Count == 0 && (requested is not null || (justification ?? "").Trim().Length > 0))
            throw new HarnessError("invalid_args",
                "sandbox_permissions and justification are not available in this composition (no confining sandbox backend)");

        var standing = policy.Resolve(context.Session);
        if (requested is not null && SandboxModes.TryParse(requested, out var requestedMode) && requestedMode == standing.Mode)
            return standing;
        if (requested is null && justification is not null && justification.Trim().Length == 0)
            justification = null;
        Escalation.ValidateArgs(requested, justification);
        if (requested is null || justification is null) return standing;
        if (!SandboxModes.TryParse(requested, out var target))
            throw new HarnessError(Escalation.InvalidArgument, $"unknown sandbox mode \"{requested}\"");

        var approved = await Escalation.ApproveAsync(
            target, justification, standing.Mode,
            toolName == "bash" ? "command" : "operation",
            approver, context.Session, toolName, context.CallId, cancellationToken);
        return standing with { Mode = approved };
    }

    /// <summary>
    /// Map a filesystem-fence denial for the model: the shared [sandbox: …] marker plus the
    /// same-turn escalation hint, keeping the structured FS_SANDBOX_DENIED code.
    /// </summary>
    public ToolResult MapFsDenial(SandboxExecutionPolicy standing) =>
        ToolResult.Error(
            $"{Escalation.DenialMarker(standing.Mode)}\n{Escalation.HintMarker("operation")}",
            SandboxErrorCodes.FsDenied);

    /// <summary>
    /// Run one command under the resolved policy: danger-full-access returns null (the caller
    /// runs its own unconfined path explicitly); a confined mode without a backend fails closed.
    /// </summary>
    public Task<ConfinedRunResult?> RunConfinedAsync(
        string fileName, IReadOnlyList<string> args, string cwd,
        SandboxExecutionPolicy resolved, CancellationToken cancellationToken)
    {
        if (resolved.Mode == SandboxMode.DangerFullAccess) return Task.FromResult<ConfinedRunResult?>(null);
        if (provider is null) throw SandboxErrorCodes.UnavailableError(resolved.Mode);
        return provider.RunConfinedAsync(fileName, args, cwd, resolved, cancellationToken)!;
    }
}
