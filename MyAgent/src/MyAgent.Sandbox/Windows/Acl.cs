using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MyAgent.Core;

namespace MyAgent.Sandbox.Windows;

/// <summary>
/// ACL editing helpers (mirrors dsh-sandbox-windows-acl/acl): grant/revoke a capability SID
/// on a directory via SetEntriesInAclW + SetNamedSecurityInfoW. Each grant applies three
/// edits in ONE call: the capability-SID allow ACE, a container-inherited Deny removing the
/// ambient FILE_DELETE_CHILD right from Everyone, and a Low no-write-up mandatory label.
/// The whole get-merge-set sequence runs under a per-path exclusive LockFileEx lock.
/// Every API call is checked; failures throw with the API name and exact Win32 code.
/// </summary>
internal static class Acl
{
    /// <summary>One lock file per protected path: %TEMP%\myagent-acl-locks\&lt;sha256(lowercased path)[..16]&gt;.lock.</summary>
    public static string LockFilePath(string path)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..16].ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), "myagent-acl-locks", $"{digest}.lock");
    }

    /// <summary>Run action holding the per-path exclusive byte-range lock (fail closed).</summary>
    public static T WithPathLock<T>(string path, Func<T> action)
    {
        var lockPath = LockFilePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var handle = Win32.CreateFileW(lockPath,
            Win32.GENERIC_READ | Win32.GENERIC_WRITE,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
            IntPtr.Zero, Win32.OPEN_ALWAYS, 0, IntPtr.Zero);
        if (handle == Win32.InvalidHandle || handle == IntPtr.Zero)
            throw Win32.Fail("CreateFileW", Marshal.GetLastWin32Error(), lockPath);
        var overlapped = new Win32.OVERLAPPED();
        if (!Win32.LockFileEx(handle, Win32.LOCKFILE_EXCLUSIVE_LOCK, 0, 1, 0, ref overlapped))
        {
            var code = Marshal.GetLastWin32Error();
            Win32.CloseHandle(handle);
            throw Win32.Fail("LockFileEx", code, lockPath);
        }
        try
        {
            return action();
        }
        finally
        {
            // Best-effort release: cleanup failures must not mask the action's error.
            Win32.UnlockFileEx(handle, 0, 1, 0, ref overlapped);
            Win32.CloseHandle(handle);
        }
    }

    private static Win32.EXPLICIT_ACCESS_W Entry(IntPtr sid, uint mode, uint permissions,
        uint inheritance = Win32.SUB_CONTAINERS_AND_OBJECTS_INHERIT) => new()
    {
        grfAccessPermissions = permissions,
        grfAccessMode = mode,
        grfInheritance = inheritance,
        Trustee = new Win32.TRUSTEE_W
        {
            MultipleTrusteeOperation = 0, // NO_MULTIPLE_TRUSTEE
            TrusteeForm = 0,              // TRUSTEE_IS_SID
            TrusteeType = 0,              // TRUSTEE_IS_UNKNOWN
            ptstrName = sid,
        },
    };

    /// <summary>
    /// Read the directory's current explicit DACL and mandatory label. The returned ACL
    /// pointers sit INSIDE the security descriptor allocation — only the descriptor may be
    /// LocalFree'd, and not before SetEntriesInAclW has consumed the ACL.
    /// </summary>
    private static (IntPtr OldAcl, IntPtr LabelAcl, IntPtr Descriptor) ReadCurrentSecurity(string path)
    {
        var result = Win32.GetNamedSecurityInfoW(path, Win32.SE_FILE_OBJECT,
            Win32.DACL_SECURITY_INFORMATION | Win32.LABEL_SECURITY_INFORMATION,
            out _, out _, out var dacl, out var sacl, out var descriptor);
        if (result != Win32.ERROR_SUCCESS) throw Win32.Fail("GetNamedSecurityInfoW", (int)result, path);
        return (dacl, sacl, descriptor);
    }

    /// <summary>Build the inheritable Low no-write-up mandatory label ACL (caller LocalFree's it).</summary>
    private static IntPtr BuildLowLabelAcl(IntPtr lowLabelSid)
    {
        var sidLength = Win32.GetLengthSid(lowLabelSid);
        if (sidLength == 0) throw Win32.Fail("GetLengthSid", Marshal.GetLastWin32Error(), "Low mandatory label SID");
        var aclLength = 8u + 8u + sidLength; // ACL header + SYSTEM_MANDATORY_LABEL_ACE overhead + SID
        var acl = Win32.LocalAlloc(Win32.LPTR, aclLength);
        if (acl == IntPtr.Zero) throw Win32.Fail("LocalAlloc", Marshal.GetLastWin32Error(), "Low mandatory label ACL");
        if (!Win32.InitializeAcl(acl, aclLength, Win32.ACL_REVISION))
        {
            var code = Marshal.GetLastWin32Error();
            Win32.LocalFree(acl);
            throw Win32.Fail("InitializeAcl", code, "Low mandatory label ACL");
        }
        if (!Win32.AddMandatoryAce(acl, Win32.ACL_REVISION,
                Win32.SUB_CONTAINERS_AND_OBJECTS_INHERIT, Win32.SYSTEM_MANDATORY_LABEL_NO_WRITE_UP, lowLabelSid))
        {
            var code = Marshal.GetLastWin32Error();
            Win32.LocalFree(acl);
            throw Win32.Fail("AddMandatoryAce", code, "Low mandatory label ACL");
        }
        return acl;
    }

    /// <summary>
    /// True when the ACL already carries the EXACT entry (aceType, inheritance, mask, trustee SID).
    /// ACE_HEADER: AceType@0, AceFlags@1, AceSize@2; Mask@4; the SID is INLINE at @8 — compared
    /// byte-wise against the trustee SID's canonical form. Malformed headers read as "no exact
    /// entry" so the caller falls back to the robust merge-apply path.
    /// </summary>
    private static bool HasExactEntry(IntPtr acl, byte aceType, uint inheritance, uint mask, IntPtr sid)
    {
        if (acl == IntPtr.Zero) return false;
        int aclSize = (ushort)Marshal.ReadInt16(acl, 2);
        var aceCount = (ushort)Marshal.ReadInt16(acl, 4);
        if (aclSize < 8 || aclSize > 1_048_576) return false;
        var sidLength = (int)Win32.GetLengthSid(sid);
        if (sidLength <= 0) return false;
        var sidBytes = new byte[sidLength];
        Marshal.Copy(sid, sidBytes, 0, sidLength);
        var offset = 8;
        for (var i = 0; i < aceCount; i++)
        {
            var aceSize = (ushort)Marshal.ReadInt16(acl, offset + 2);
            if (aceSize < 8 || offset + aceSize > aclSize) return false;
            var exact = Marshal.ReadByte(acl, offset) == aceType
                && Marshal.ReadByte(acl, offset + 1) == inheritance
                && (uint)Marshal.ReadInt32(acl, offset + 4) == mask
                && aceSize >= 8 + sidLength;
            if (exact)
            {
                var inline = new byte[sidLength];
                Marshal.Copy(acl + offset + 8, inline, 0, sidLength);
                if (inline.AsSpan().SequenceEqual(sidBytes)) return true;
            }
            offset += aceSize;
        }
        return false;
    }

    private static bool HasExactGrant(IntPtr oldAcl, IntPtr sid) =>
        HasExactEntry(oldAcl, Win32.ACCESS_ALLOWED_ACE_TYPE, Win32.SUB_CONTAINERS_AND_OBJECTS_INHERIT, Win32.GRANT_MASK, sid);

    private static bool HasExactDeny(IntPtr oldAcl, IntPtr worldSid) =>
        HasExactEntry(oldAcl, Win32.ACCESS_DENIED_ACE_TYPE, Win32.CONTAINER_INHERIT_ACE, Win32.FILE_DELETE_CHILD, worldSid);

    private static bool HasExactLabel(IntPtr labelAcl, IntPtr lowLabelSid) =>
        HasExactEntry(labelAcl, Win32.SYSTEM_MANDATORY_LABEL_ACE_TYPE, Win32.SUB_CONTAINERS_AND_OBJECTS_INHERIT,
            Win32.SYSTEM_MANDATORY_LABEL_NO_WRITE_UP, lowLabelSid);

    /// <summary>Whether a capability grant for a DIFFERENT SID stands on this DACL (revoke must keep the shared label).</summary>
    private static bool HasForeignGrant(IntPtr oldAcl, IntPtr sid)
    {
        if (oldAcl == IntPtr.Zero) return false;
        int aclSize = (ushort)Marshal.ReadInt16(oldAcl, 2);
        var aceCount = (ushort)Marshal.ReadInt16(oldAcl, 4);
        if (aclSize < 8 || aclSize > 1_048_576) return false;
        var sidLength = (int)Win32.GetLengthSid(sid);
        if (sidLength <= 0) return false;
        var sidBytes = new byte[sidLength];
        Marshal.Copy(sid, sidBytes, 0, sidLength);
        var offset = 8;
        for (var i = 0; i < aceCount; i++)
        {
            var aceSize = (ushort)Marshal.ReadInt16(oldAcl, offset + 2);
            if (aceSize < 8 || offset + aceSize > aclSize) return false;
            var isGrant = Marshal.ReadByte(oldAcl, offset) == Win32.ACCESS_ALLOWED_ACE_TYPE
                && (uint)Marshal.ReadInt32(oldAcl, offset + 4) == Win32.GRANT_MASK;
            if (isGrant && aceSize >= 8 + sidLength)
            {
                var inline = new byte[sidLength];
                Marshal.Copy(oldAcl + offset + 8, inline, 0, sidLength);
                if (!inline.AsSpan().SequenceEqual(sidBytes)) return true;
            }
            offset += aceSize;
        }
        return false;
    }

    /// <summary>
    /// Shared tail of GrantWrite/RevokeWrite: merge entries into oldAcl, free the descriptor
    /// before applying, apply the merged DACL together with the label edit in one
    /// SetNamedSecurityInfoW call, then free every ACL this call owns — checking each call.
    /// labelEdit: apply (set label), clear (remove label), keep (leave untouched).
    /// </summary>
    private static void MergeAndApply(string path, Win32.EXPLICIT_ACCESS_W[] entries, IntPtr oldAcl,
        (string Kind, IntPtr Acl) labelEdit, IntPtr descriptor, string label)
    {
        var mergeResult = Win32.SetEntriesInAclW((uint)entries.Length, entries, oldAcl, out var newAcl);
        if (mergeResult != Win32.ERROR_SUCCESS)
        {
            if (descriptor != IntPtr.Zero) Win32.LocalFree(descriptor);
            if (labelEdit.Kind == "apply") Win32.LocalFree(labelEdit.Acl);
            throw Win32.Fail("SetEntriesInAclW", (int)mergeResult, $"{label}({path})");
        }
        if (newAcl == IntPtr.Zero)
        {
            if (descriptor != IntPtr.Zero) Win32.LocalFree(descriptor);
            if (labelEdit.Kind == "apply") Win32.LocalFree(labelEdit.Acl);
            throw Win32.Fail("SetEntriesInAclW", Marshal.GetLastWin32Error(), $"{label}({path}): null new ACL");
        }

        // The descriptor block (oldAcl included) is dead after the merge — free before applying.
        if (descriptor != IntPtr.Zero) Win32.LocalFree(descriptor);
        var securityInfo = labelEdit.Kind == "keep"
            ? Win32.DACL_SECURITY_INFORMATION
            : Win32.DACL_SECURITY_INFORMATION | Win32.LABEL_SECURITY_INFORMATION;
        var applyResult = Win32.SetNamedSecurityInfoW(path, Win32.SE_FILE_OBJECT, securityInfo,
            IntPtr.Zero, IntPtr.Zero, newAcl, labelEdit.Kind == "apply" ? labelEdit.Acl : IntPtr.Zero);
        Win32.LocalFree(newAcl);
        if (labelEdit.Kind == "apply") Win32.LocalFree(labelEdit.Acl);
        if (applyResult != Win32.ERROR_SUCCESS)
            throw Win32.Fail("SetNamedSecurityInfoW", (int)applyResult, $"{label}({path})");
    }

    /// <summary>
    /// Grant GRANT_MASK (Write+Delete, "Modify") to the capability SID on path, deny Everyone
    /// the ambient FILE_DELETE_CHILD right (containers only), and apply the Low mandatory
    /// label — one merge. Idempotent: exact ACE + deny + label together SKIP the apply
    /// (which would re-propagate the descriptor across the whole tree).
    /// </summary>
    public static void GrantWrite(string path, IntPtr sid, IntPtr lowLabelSid, IntPtr worldSid)
    {
        WithPathLock(path, () =>
        {
            var (oldAcl, labelAcl, descriptor) = ReadCurrentSecurity(path);
            if (oldAcl != IntPtr.Zero && labelAcl != IntPtr.Zero
                && HasExactGrant(oldAcl, sid) && HasExactDeny(oldAcl, worldSid) && HasExactLabel(labelAcl, lowLabelSid))
            {
                if (descriptor != IntPtr.Zero) Win32.LocalFree(descriptor);
                return true;
            }
            IntPtr label;
            try
            {
                label = BuildLowLabelAcl(lowLabelSid);
            }
            catch
            {
                if (descriptor != IntPtr.Zero) Win32.LocalFree(descriptor);
                throw;
            }
            MergeAndApply(path,
                [
                    Entry(worldSid, Win32.DENY_ACCESS, Win32.FILE_DELETE_CHILD, Win32.CONTAINER_INHERIT_ACE),
                    Entry(sid, Win32.GRANT_ACCESS, Win32.GRANT_MASK),
                ],
                oldAcl, ("apply", label), descriptor, "grantWrite");
            return true;
        });
    }

    /// <summary>
    /// Remove every ACE for the capability SID (REVOKE_ACCESS merge — other entries survive).
    /// The shared Low label is cleared only when no other capability grant remains.
    /// Returns whether an ACE removal was attempted.
    /// </summary>
    public static bool RevokeWrite(string path, IntPtr sid)
    {
        return WithPathLock(path, () =>
        {
            var (oldAcl, _, descriptor) = ReadCurrentSecurity(path);
            if (oldAcl == IntPtr.Zero)
            {
                if (descriptor != IntPtr.Zero) Win32.LocalFree(descriptor);
                return false;
            }
            MergeAndApply(path, [Entry(sid, Win32.REVOKE_ACCESS, 0)], oldAcl,
                HasForeignGrant(oldAcl, sid) ? ("keep", IntPtr.Zero) : ("clear", IntPtr.Zero),
                descriptor, "revokeWrite");
            return true;
        });
    }
}

/// <summary>
/// Canonical directory-boundary checks (mirrors path-boundary.ts): a private temp inside
/// the workspace would inherit the standing workspace capability; overlapping writable and
/// temp trees would merge the two capabilities.
/// </summary>
public static class PathBoundary
{
    private static bool ContainsDirectory(string root, string candidate)
    {
        var relation = Path.GetRelativePath(SandboxRoots.CanonicalPath(root), SandboxRoots.CanonicalPath(candidate));
        return relation == "."
            || (!Path.IsPathRooted(relation) && relation != ".." && !relation.StartsWith(".." + Path.DirectorySeparatorChar));
    }

    public static bool IsTempRootInsideWorkspace(string workspaceRoot, string tempRoot) =>
        ContainsDirectory(workspaceRoot, tempRoot);

    public static void AssertTempRootOutsideWorkspace(string workspaceRoot, string tempRoot)
    {
        if (IsTempRootInsideWorkspace(workspaceRoot, tempRoot))
            throw new HarnessError(SandboxErrorCodes.InvalidTempRoot,
                $"Windows ACL temp root must be outside the workspace: workspace={workspaceRoot}; temp={tempRoot}");
    }

    public static void AssertPrivateTempDisjoint(IEnumerable<string> writableDirs, string tempDir)
    {
        foreach (var writableDir in writableDirs)
            if (ContainsDirectory(writableDir, tempDir) || ContainsDirectory(tempDir, writableDir))
                throw new HarnessError(SandboxErrorCodes.PreparationFailed,
                    $"AclSandbox private temp directory must be disjoint from writable directories: writable={writableDir}; temp={tempDir}");
    }
}
