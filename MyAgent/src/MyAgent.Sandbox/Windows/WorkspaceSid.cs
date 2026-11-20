using System.Security.Cryptography;
using System.Text;

namespace MyAgent.Sandbox.Windows;

/// <summary>
/// The per-workspace write identity (mirrors dsh-sandbox-windows-acl/workspace-sid):
/// a deterministic S-1-4-x-y SID derived from the canonical workspace path, whose ACEs
/// form that workspace's write allowlist. Every confined execution of the same workspace
/// carries the SAME write SID, so the workspace-root ACE materializes once per workspace
/// per machine. Temp directories use a separate per-directory identity so sibling sessions
/// sharing a workspace cannot write one another's temp trees.
/// </summary>
public static class WorkspaceSid
{
    private const uint Modulus = (1u << 30) - 1;

    /// <summary>Derive the workspace's write SID from its canonical path (SDDL string form).</summary>
    public static string WorkspaceWriteSid(string workspaceRoot)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(workspaceRoot));
        var first = BitConverter.ToUInt32(digest, 0) % Modulus + 1;
        var second = BitConverter.ToUInt32(digest, 4) % Modulus + 1;
        return $"S-1-4-{first}-{second}";
    }

    /// <summary>Derive one private temp directory's write SID (domain-separated third subauthority).</summary>
    public static string TempWriteSid(string tempDir)
    {
        var bytes = Encoding.UTF8.GetBytes("temp\0").Concat(Encoding.UTF8.GetBytes(tempDir)).ToArray();
        var digest = SHA256.HashData(bytes);
        var first = BitConverter.ToUInt32(digest, 0) % Modulus + 1;
        var second = BitConverter.ToUInt32(digest, 4) % Modulus + 1;
        return $"S-1-4-{first}-{second}-1";
    }
}
