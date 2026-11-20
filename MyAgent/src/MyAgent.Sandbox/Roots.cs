using MyAgent.Core;

namespace MyAgent.Sandbox;

/// <summary>
/// The writable-root derivation shared by every enforcement dialect (mirrors dsh-sandbox/roots):
/// workspace-write means "the workspace root, explicit extra directories, and platform temp areas".
/// The Windows ACL grants and the in-process filesystem fence both derive their allow-list here.
/// </summary>
public static class SandboxRoots
{
    /// <summary>
    /// Resolve a granted root to the path enforcement compares: canonical when it exists;
    /// the spelling as-is when resolution fails (a missing root matches nothing — conservative).
    /// </summary>
    public static string CanonicalPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            // Resolve reparse points/symlinks on the deepest existing ancestor, then re-append the tail.
            var existing = full;
            var tail = new List<string>();
            while (!Directory.Exists(existing) && !File.Exists(existing))
            {
                var parent = Path.GetDirectoryName(existing);
                if (parent is null || parent == existing) return full;
                tail.Insert(0, Path.GetFileName(existing));
                existing = parent;
            }
            var real = Path.GetFullPath(new DirectoryInfo(existing).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? existing);
            foreach (var part in tail) real = Path.Combine(real, part);
            return real;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return path;
        }
    }

    /// <summary>Resolve an extra grant, rejecting missing, non-directory, and symlinked entries.</summary>
    private static string AdditionalRoot(string path)
    {
        DirectoryInfo info;
        try
        {
            info = new DirectoryInfo(path);
            if (!info.Exists) throw SandboxErrorCodes.InvalidRootError(path, "expected an existing directory");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw SandboxErrorCodes.InvalidRootError(path, $"cannot read the directory: {e.Message}");
        }
        if (info.LinkTarget is not null)
            throw SandboxErrorCodes.InvalidRootError(path, $"use the physical directory path; symlinked grants are not supported");
        return CanonicalPath(path);
    }

    /// <summary>
    /// The workspace-owned writable roots: workspace root plus explicit extra directories,
    /// canonical and deduplicated. Empty under read-only and danger-full-access.
    /// </summary>
    public static IReadOnlyList<string> WorkspaceWritableRoots(SandboxExecutionPolicy policy)
    {
        if (policy.Mode != SandboxMode.WorkspaceWrite) return [];
        var roots = new List<string> { CanonicalPath(policy.WorkspaceRoot) };
        foreach (var extra in policy.AdditionalWritableRoots ?? [])
            roots.Add(AdditionalRoot(extra));
        return roots.Distinct(PathComparison).ToList();
    }

    /// <summary>
    /// The roots one confined execution may WRITE under: workspace-owned roots plus the
    /// per-user platform temp dir. Empty exactly under read-only.
    /// </summary>
    public static IReadOnlyList<string> WritableRoots(SandboxExecutionPolicy policy)
    {
        if (policy.Mode != SandboxMode.WorkspaceWrite) return [];
        var roots = WorkspaceWritableRoots(policy).ToList();
        roots.Add(CanonicalPath(Path.GetTempPath()));
        return roots.Distinct(PathComparison).ToList();
    }

    public static StringComparer PathComparison =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Whether a canonical target is a writable root or lies beneath it (mirrors fs-sandbox containment).
    /// Lexical fast path first; then walk existing ancestors comparing resolved identity, so Windows
    /// 8.3 aliases and symlinked ancestors cannot smuggle a write outside the fence.
    /// </summary>
    public static bool IsPathUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var fullTarget = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(fullTarget, fullRoot, comparison)) return true;
        if (fullTarget.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)) return true;

        // Identity fallback: canonicalize both sides through existing ancestors.
        var canonicalTarget = CanonicalPath(fullTarget);
        var canonicalRoot = CanonicalPath(fullRoot);
        if (string.Equals(canonicalTarget, canonicalRoot, comparison)) return true;
        return canonicalTarget.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, comparison);
    }
}

/// <summary>
/// The in-process filesystem fence (mirrors dsh-fs-sandbox): every mutating file tool checks
/// its target against the resolved policy BEFORE touching the filesystem. Denials carry
/// FS_SANDBOX_DENIED so the tool layer maps them to the shared [sandbox: …] marker.
/// </summary>
public static class FsFence
{
    public static void EnsureWritable(SandboxExecutionPolicy policy, string path)
    {
        if (policy.Mode == SandboxMode.DangerFullAccess) return;
        if (policy.Mode == SandboxMode.WorkspaceWrite)
        {
            foreach (var root in SandboxRoots.WritableRoots(policy))
                if (SandboxRoots.IsPathUnder(path, root)) return;
        }
        throw new HarnessError(SandboxErrorCodes.FsDenied,
            $"file write to \"{path}\" is denied under {policy.Mode.Wire()} mode");
    }
}
