using MyAgent.Approval;
using MyAgent.Core;
using MyAgent.Sandbox;

namespace MyAgent.Permissions;

/// <summary>One preset's sandbox/approval bundle and optional client presentation (mirrors PresetSpec).</summary>
public sealed record PresetSpec(
    SandboxMode Sandbox,
    ApprovalPolicy Approval,
    string? Name = null,
    string? Description = null);

/// <summary>Presentation for an available preset or the derived 'custom' current value.</summary>
public sealed record PresetOption(string Value, string Name, string? Description = null);

/// <summary>
/// User-facing permission presets over the independent sandbox-mode and approval-policy knobs
/// (mirrors dsh-permission-presets PermissionPresetService). A switch records the selected
/// preset as durable user intent, then writes changed knobs through their canonical setters.
/// Execution, prompt narration, and replay keep reading their knob folds. The preset event
/// preserves user intent when two presets share a bundle; unmatched knob values derive 'custom'.
/// </summary>
public sealed class PermissionPresetService(SandboxPolicyService sandboxPolicy, ApprovalService approval)
{
    /// <summary>Returned when effective knob values match no available preset — never a switch target.</summary>
    public const string CustomPreset = "custom";

    /// <summary>The default preset table (mirrors the harness Config default).</summary>
    public static IReadOnlyDictionary<string, PresetSpec> DefaultPresets { get; } =
        new Dictionary<string, PresetSpec>(StringComparer.Ordinal)
        {
            // 展示名与描述面向中文 UI；预设键（wire 值）保持英文，事件与折叠不受影响。
            ["workspace-write"] = new(SandboxMode.WorkspaceWrite, ApprovalPolicy.Ask,
                "工作区可写",
                "可写会话工作区与临时目录；越界操作需经审批。"),
            ["danger-full-access"] = new(SandboxMode.DangerFullAccess, ApprovalPolicy.Never,
                "完全访问",
                "不限制文件读写，不弹审批提示。"),
        };

    private readonly IReadOnlyDictionary<string, PresetSpec> _presets = DefaultPresets;

    /// <summary>The advertised preset names in declaration order.</summary>
    public IReadOnlyList<string> Names => [.. _presets.Keys];

    /// <summary>The preset selected as the default for future sessions (settings override or inferred).</summary>
    public string DefaultPreset { get; set; } = "workspace-write";

    /// <summary>The knob state folded from the session log: last seen value of each knob event.</summary>
    private sealed record KnobState(string? Preset, SandboxMode? Sandbox, ApprovalPolicy? Approval, bool Seeded);

    private static KnobState Fold(Session session)
    {
        string? preset = null;
        SandboxMode? sandbox = null;
        ApprovalPolicy? approvalPolicy = null;
        var seeded = false;
        foreach (var evt in session.Events)
        {
            switch (evt.Type)
            {
                case SessionEventTypes.PermissionPreset:
                    if (evt.Data.TryGetProperty("preset", out var p)) preset = p.GetString();
                    break;
                case SessionEventTypes.SandboxMode:
                    if (evt.Data.TryGetProperty("mode", out var m) && SandboxModes.TryParse(m.GetString(), out var mode))
                        sandbox = mode;
                    break;
                case SessionEventTypes.ApprovalPolicy:
                    if (evt.Data.TryGetProperty("policy", out var a) && ApprovalPolicies.TryParse(a.GetString(), out var policy))
                        approvalPolicy = policy;
                    break;
                case SessionEventTypes.SessionEndSeed:
                    seeded = true;
                    break;
            }
        }
        return new KnobState(preset, sandbox, approvalPolicy, seeded);
    }

    /// <summary>
    /// Resolve the preset matching the effective knob values. A still-matching last selection
    /// wins shared-bundle ties; otherwise the first configured match wins; 'custom' when none.
    /// </summary>
    public string Current(Session session) => Derive(Fold(session));

    private string Derive(KnobState state)
    {
        var sandbox = state.Sandbox ?? sandboxPolicy.DefaultMode;
        var approvalValue = state.Approval ?? approval.DefaultPolicy;
        bool Matches(PresetSpec spec) => spec.Sandbox == sandbox && spec.Approval == approvalValue;
        if (state.Preset is not null && _presets.TryGetValue(state.Preset, out var selected) && Matches(selected))
            return state.Preset;
        foreach (var (name, spec) in _presets)
            if (Matches(spec)) return name;
        return CustomPreset;
    }

    /// <summary>Resolve an available preset's knob bundle; unknown names throw.</summary>
    public PresetSpec Resolve(string name) =>
        _presets.TryGetValue(name, out var spec)
            ? spec
            : throw new HarnessError("unknown_preset", $"permission: unknown preset \"{name}\" (known: {string.Join(", ", Names)})");

    /// <summary>Build the client option for an available preset or 'custom'.</summary>
    public PresetOption OptionOf(string name)
    {
        if (name == CustomPreset)
            return new PresetOption(CustomPreset, "Custom", "Current sandbox and approval settings do not match a preset.");
        var spec = Resolve(name);
        return new PresetOption(name, spec.Name ?? name, spec.Description);
    }

    /// <summary>Every currently selectable preset, in contribution order.</summary>
    public IReadOnlyList<PresetOption> Catalog() => [.. Names.Select(OptionOf)];

    /// <summary>
    /// Record a changed preset, then update each changed knob through its own canonical setter.
    /// Selecting the effective preset again appends nothing.
    /// </summary>
    public void Set(Session session, string name)
    {
        var spec = Resolve(name);
        var current = Current(session);
        var knobs = Fold(session);
        if (current != name)
            session.Append(SessionEventTypes.PermissionPreset, new { preset = name });
        if (spec.Sandbox != (knobs.Sandbox ?? sandboxPolicy.DefaultMode))
            SandboxPolicyService.SetMode(session, spec.Sandbox);
        if (spec.Approval != (knobs.Approval ?? approval.DefaultPolicy))
            ApprovalService.SetPolicy(session, spec.Approval);
    }

    /// <summary>
    /// Switch one live agent's preset: the approval knob goes through the live service so the
    /// model receives the policy-change notice (mirrors the /permission command handler).
    /// </summary>
    public void SetForAgent(Agent agent, string name)
    {
        var spec = Resolve(name);
        var current = Current(agent.Session);
        var knobs = Fold(agent.Session);
        if (current != name)
            agent.Session.Append(SessionEventTypes.PermissionPreset, new { preset = name });
        if (spec.Sandbox != (knobs.Sandbox ?? sandboxPolicy.DefaultMode))
            SandboxPolicyService.SetMode(agent.Session, spec.Sandbox);
        if (spec.Approval != (knobs.Approval ?? approval.DefaultPolicy))
            approval.SwitchPolicy(agent, spec.Approval);
    }

    /// <summary>
    /// Fill every missing permission fact during agent activation (mirrors pinInitialPermission):
    /// a genuinely fresh session pins the user default; seeded or partially initialized sessions
    /// preserve their effective knob values and only gain the missing durable facts.
    /// </summary>
    public void PinInitialPermission(Session session)
    {
        var state = Fold(session);
        if (state.Preset is null && state.Sandbox is null && state.Approval is null && !state.Seeded)
        {
            var name = DefaultPreset;
            var spec = Resolve(name);
            session.Append(SessionEventTypes.PermissionPreset, new { preset = name });
            SandboxPolicyService.SetMode(session, spec.Sandbox);
            ApprovalService.SetPolicy(session, spec.Approval);
            return;
        }
        var effective = Derive(state);
        if (state.Preset is null && effective != CustomPreset)
            session.Append(SessionEventTypes.PermissionPreset, new { preset = effective });
        if (state.Sandbox is null)
            SandboxPolicyService.SetMode(session, sandboxPolicy.DefaultMode);
        if (state.Approval is null)
            ApprovalService.SetPolicy(session, approval.DefaultPolicy);
    }

    /// <summary>
    /// Seed a delegated child's knobs from the parent's effective values (mirrors the
    /// delegation-seeded sandbox/mode + approval/policy overrides; source='delegation').
    /// </summary>
    public void SeedDelegation(Session parent, Session child)
    {
        var sandbox = sandboxPolicy.OverrideOf(parent) ?? sandboxPolicy.DefaultMode;
        var approvalValue = approval.OverrideOf(parent) ?? approval.DefaultPolicy;
        SandboxPolicyService.SetMode(child, sandbox, "delegation");
        ApprovalService.SetPolicy(child, approvalValue, "delegation");
        child.Append(SessionEventTypes.SessionEndSeed, new { });
    }

    /// <summary>Model-facing permission narration for the runtime-context snapshot.</summary>
    public IEnumerable<string> RenderContext(Session session)
    {
        yield return sandboxPolicy.RenderPolicyContext(session);
        yield return approval.RenderPolicyContext(session);
    }
}
