using System.Text.Json;
using MyAgent.Core;

namespace MyAgent.Sandbox;

/// <summary>
/// The sandbox POLICY home (mirrors dsh-sandbox-policy): the deployment's sandbox fallbacks
/// plus per-session resolution — the file-effect mode, the workspace-write root, and the
/// explicit extra writable directories. The session log is the store: a runtime switch is
/// one log-only sandbox/mode event; the LAST such event is the session's override, so it
/// survives restart by replay and two sessions never see each other's state.
/// </summary>
public sealed class SandboxPolicyService
{
    /// <summary>The deployment default mode — the fallback beneath a session override (fail-safe: read-only).</summary>
    public SandboxMode DefaultMode { get; }

    /// <summary>Absolute fallback root for agentless calls and sessions without a cwd.</summary>
    public string WorkspaceRoot { get; }

    /// <summary>Frozen extra writable directories shared by every resolved policy, or null for none.</summary>
    public IReadOnlyList<string>? AdditionalWritableRoots { get; }

    /// <summary>Runtime-only grants per live session; never restored from the log.</summary>
    private readonly Dictionary<string, Dictionary<object, string>> _writableRootRegistrations = [];
    private readonly object _gate = new();

    public SandboxPolicyService(
        SandboxMode defaultMode = SandboxMode.ReadOnly,
        string? workspaceRoot = null,
        IReadOnlyList<string>? additionalWritableRoots = null)
    {
        DefaultMode = defaultMode;
        var root = workspaceRoot ?? Directory.GetCurrentDirectory();
        if (!Path.IsPathRooted(root) || root.Contains('\0'))
            throw SandboxErrorCodes.InvalidRootError(root,
                "workspace root must be an absolute execution-world path without NUL bytes", "sandbox workspace root");
        WorkspaceRoot = Path.GetFullPath(root);
        AdditionalWritableRoots = ValidateAdditional(additionalWritableRoots);
    }

    private static IReadOnlyList<string>? ValidateAdditional(IReadOnlyList<string>? roots)
    {
        if (roots is null || roots.Count == 0) return null;
        foreach (var root in roots)
            if (!Path.IsPathRooted(root) || root.Contains('\0'))
                throw SandboxErrorCodes.InvalidRootError(root,
                    "writable directories must be absolute execution-world paths without NUL bytes");
        return roots.Distinct(SandboxRoots.PathComparison).ToList();
    }

    /// <summary>
    /// THE write path for a session's sandbox-mode override: appends exactly one log-only
    /// sandbox/mode event — the switch IS its event. Takes effect on the session's next confined call.
    /// </summary>
    public static void SetMode(Session session, SandboxMode mode, string? source = null) =>
        session.Append(SessionEventTypes.SandboxMode,
            source is null ? new { mode = mode.Wire() } : new { mode = mode.Wire(), source });

    /// <summary>Read the session override without applying the deployment default.</summary>
    public SandboxMode? OverrideOf(Session session)
    {
        var events = session.Events; // one snapshot; indexing the property copies per access
        for (var i = events.Count - 1; i >= 0; i--)
        {
            var evt = events[i];
            if (evt.Type != SessionEventTypes.SandboxMode) continue;
            if (evt.Data.TryGetProperty("mode", out var m) && SandboxModes.TryParse(m.GetString(), out var mode))
                return mode;
        }
        return null;
    }

    /// <summary>
    /// Resolve the complete policy for one capability call. An approved explicit mode outranks
    /// the session's last sandbox/mode event, which outranks the deployment default. The session
    /// cwd is its workspace-write boundary; the configured root is the fallback.
    /// </summary>
    public SandboxExecutionPolicy Resolve(Session? session = null, SandboxMode? approvedMode = null)
    {
        IReadOnlyList<string>? additional = AdditionalWritableRoots;
        if (session is not null)
        {
            lock (_gate)
            {
                if (_writableRootRegistrations.TryGetValue(session.Id, out var registrations) && registrations.Count > 0)
                    additional = ValidateAdditional([.. (AdditionalWritableRoots ?? []), .. registrations.Values]);
            }
        }
        var root = session?.Cwd is { Length: > 0 } cwd ? cwd : WorkspaceRoot;
        if (!Path.IsPathRooted(root) || root.Contains('\0'))
            throw SandboxErrorCodes.InvalidRootError(root,
                "the Session working directory must be an absolute execution-world path without NUL bytes",
                "sandbox workspace root");
        return new SandboxExecutionPolicy(
            approvedMode ?? (session is null ? null : OverrideOf(session)) ?? DefaultMode,
            Path.GetFullPath(root),
            additional,
            session?.Id);
    }

    /// <summary>
    /// Permit writes under one directory in this session's future workspace-write operations.
    /// Runtime-only: dispose removes only this registration; grants must be re-registered after resume.
    /// </summary>
    public IDisposable RegisterWritableRoot(Session session, string directory)
    {
        ValidateAdditional([directory]);
        var key = new object();
        lock (_gate)
        {
            if (!_writableRootRegistrations.TryGetValue(session.Id, out var registrations))
                _writableRootRegistrations[session.Id] = registrations = [];
            registrations[key] = directory;
        }
        return new Registration(this, session.Id, key);
    }

    private sealed class Registration(SandboxPolicyService owner, string sessionId, object key) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (owner._writableRootRegistrations.TryGetValue(sessionId, out var registrations))
                {
                    registrations.Remove(key);
                    if (registrations.Count == 0) owner._writableRootRegistrations.Remove(sessionId);
                }
            }
        }
    }

    /// <summary>
    /// Render the policy for the model's runtime-context snapshot (mirrors renderPolicyContext):
    /// describes the policy without inventorying capabilities; each tool owns its denial guidance.
    /// </summary>
    public string RenderPolicyContext(Session session)
    {
        var policy = Resolve(session);
        switch (policy.Mode)
        {
            case SandboxMode.ReadOnly:
                return "Current file policy: read-only. Any available operation enforced by the file sandbox cannot modify files in the standing mode. Do not refuse a required modification from this policy alone: try an available tool normally and follow any denial and escalation guidance it returns.";
            case SandboxMode.WorkspaceWrite:
                var extra = policy.AdditionalWritableRoots is null
                    ? ""
                    : $" Additional writable directories: {JsonSerializer.Serialize(policy.AdditionalWritableRoots)}. On Windows, these additional grants apply to file tools only, not sandboxed processes.";
                return $"Current file policy: workspace-write. Any available operation enforced by the file sandbox may modify files under the session workspace: {JsonSerializer.Serialize(policy.WorkspaceRoot)}.{extra} Some platform temporary areas may also be writable.";
            default:
                return "Current file policy: danger-full-access. The file sandbox does not restrict file modifications by available operations.";
        }
    }
}
