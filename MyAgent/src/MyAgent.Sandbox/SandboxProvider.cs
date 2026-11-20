using MyAgent.Core;
using MyAgent.Sandbox.Windows;

namespace MyAgent.Sandbox;

/// <summary>Enforcement completeness for this host (mirrors SandboxEnforcement).</summary>
public enum SandboxEnforcement
{
    Full,
    Partial,
}

/// <summary>
/// One settled confined execution plus the backend's dialect (mirrors ConfinedArgv facts
/// carried per call): the consumer classifies runner failure FIRST, then denial signatures.
/// </summary>
public sealed record ConfinedRunResult(
    string Stdout,
    string Stderr,
    int ExitCode,
    SandboxEnforcement Enforcement,
    IReadOnlyList<string> DenialSignatures,
    IReadOnlyList<RunnerFailureRule> RunnerFailureRules);

/// <summary>
/// The process-confinement capability seam (mirrors SandboxProvider). The TypeScript seam
/// returns wrapped argv because its backends are launcher executables; the Windows ACL
/// backend confines through a restricted primary token, which cannot be expressed as argv —
/// so the C# seam runs the exact argv under the policy itself. Fail-closed contract is
/// identical: enforce or throw SANDBOX_UNAVAILABLE; silent unconfined passthrough is forbidden.
/// </summary>
public interface ISandboxProvider
{
    Task<ConfinedRunResult> RunConfinedAsync(
        string fileName, IReadOnlyList<string> args, string cwd,
        SandboxExecutionPolicy policy, CancellationToken cancellationToken);
}

/// <summary>
/// Windows ACL write-restriction provider (mirrors LocalSandboxProvider's windows-acl rung):
/// caches the STANDING workspace grant per canonical workspace root and a REVOCABLE random
/// private-temp capability per live session/workspace pair; tokens are minted per capability
/// and reused across calls. Dispose revokes temp grants (workspace ACEs stand by design —
/// the cross-session reuse cache).
/// </summary>
public sealed class WindowsAclSandboxProvider : ISandboxProvider, IDisposable
{
    /// <summary>
    /// The windows-acl denial dialect (pwsh/.NET, cmd, Node EACCES/EPERM texts) plus the
    /// localized cmd/PowerShell texts — captured stdio is decoded through the console code
    /// page, so zh-CN "拒绝访问。" / "访问被拒绝" must match too.
    /// </summary>
    public static readonly IReadOnlyList<string> DenialDialect =
        ["access is denied", "access to the path", "permission denied", "operation not permitted",
         "拒绝访问", "访问被拒绝"];

    /// <summary>
    /// Runner-failure evidence: our runner reports failures by THROWING before spawn, so a
    /// settled child is never a runner failure; the rule stays for dialect parity.
    /// </summary>
    public static readonly IReadOnlyList<RunnerFailureRule> FailureRules =
        [new(["myagent-acl-run: "], [127])];

    private sealed class Capability(AclSandbox sandbox) : IDisposable
    {
        public AclSandbox Sandbox { get; } = sandbox;
        public void Dispose() => Sandbox.Dispose();
    }

    private readonly object _gate = new();
    // Standing workspace capability per canonical workspace root (its ACE outlives us).
    private readonly Dictionary<string, Capability> _workspaceCapabilities = new(SandboxRoots.PathComparison);
    // Revocable private-temp capability per live session/workspace pair.
    private readonly Dictionary<string, Capability> _tempCapabilities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tempDirs = new(StringComparer.Ordinal);
    // Read-only tokens carry no grants; one per workspace keeps the logon-SID scan amortized.
    private readonly Dictionary<string, Capability> _readOnlyCapabilities = new(SandboxRoots.PathComparison);

    public Task<ConfinedRunResult> RunConfinedAsync(
        string fileName, IReadOnlyList<string> args, string cwd,
        SandboxExecutionPolicy policy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw SandboxErrorCodes.UnavailableError(policy.Mode, "the ACL backend requires Windows");
        if (policy.Mode == SandboxMode.DangerFullAccess)
            throw new HarnessError(SandboxErrorCodes.PreparationFailed,
                "danger-full-access bypasses confinement; the caller must run unconfined explicitly");

        var workspaceRoot = SandboxRoots.CanonicalPath(policy.WorkspaceRoot);
        try
        {
            var capability = policy.Mode == SandboxMode.ReadOnly
                ? ReadOnlyCapability(workspaceRoot)
                : WorkspaceCapability(workspaceRoot, policy.SessionId);
            return RunLockedAsync(capability, fileName, args, cwd, cancellationToken);
        }
        catch (HarnessError) { throw; }
        catch (Exception ex)
        {
            // Fail closed: any preparation/spawn failure refuses to run unconfined.
            throw SandboxErrorCodes.UnavailableError(policy.Mode, ex.Message);
        }
    }

    private static async Task<ConfinedRunResult> RunLockedAsync(
        Capability capability, string fileName, IReadOnlyList<string> args, string cwd, CancellationToken ct)
    {
        // One restricted token spawns serially: stdio drains and grant lifetimes stay simple.
        var result = await capability.Sandbox.RunAsync(fileName, args, cwd, ct);
        return new ConfinedRunResult(result.Stdout, result.Stderr, result.ExitCode,
            SandboxEnforcement.Partial, DenialDialect, FailureRules);
    }

    private Capability ReadOnlyCapability(string workspaceRoot)
    {
        lock (_gate)
        {
            if (_readOnlyCapabilities.TryGetValue(workspaceRoot, out var existing)) return existing;
            var sandbox = new AclSandbox(new AclSandboxOptions([], SandboxMode.ReadOnly));
            sandbox.Init();
            return _readOnlyCapabilities[workspaceRoot] = new Capability(sandbox);
        }
    }

    private Capability WorkspaceCapability(string workspaceRoot, string? sessionId)
    {
        var key = $"{sessionId ?? "<agentless>"}|{workspaceRoot}";
        lock (_gate)
        {
            if (_tempCapabilities.TryGetValue(key, out var existing)) return existing;

            // The private temp directory must live OUTSIDE the workspace, or every child
            // would inherit the standing workspace capability.
            var tempRoot = Path.GetTempPath();
            PathBoundary.AssertTempRootOutsideWorkspace(workspaceRoot, tempRoot);
            var tempDir = Path.Combine(tempRoot, "myagent-sandbox", $"s_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            var writeSid = WorkspaceSid.WorkspaceWriteSid(workspaceRoot);
            var tempSid = WorkspaceSid.TempWriteSid(tempDir);
            var sandbox = new AclSandbox(new AclSandboxOptions(
                [workspaceRoot], SandboxMode.WorkspaceWrite,
                WriteSid: writeSid, TempDir: tempDir, TempWriteSid: tempSid));
            try
            {
                sandbox.Init();
            }
            catch
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
                throw;
            }
            _tempDirs[key] = tempDir;
            return _tempCapabilities[key] = new Capability(sandbox);
        }
    }

    /// <summary>
    /// Revoke the revocable temp grants and delete the private temp trees; standing workspace
    /// ACEs stay (the reuse cache — an unclean shutdown leaves them for the exact-ACE skip).
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var capability in _tempCapabilities.Values) capability.Dispose();
            foreach (var capability in _readOnlyCapabilities.Values) capability.Dispose();
            _tempCapabilities.Clear();
            _readOnlyCapabilities.Clear();
            _workspaceCapabilities.Clear();
            foreach (var dir in _tempDirs.Values)
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
            _tempDirs.Clear();
        }
    }
}
