using System.Runtime.InteropServices;
using MyAgent.Core;

namespace MyAgent.Sandbox.Windows;

/// <summary>
/// Restricted-token construction (mirrors dsh-sandbox-windows-acl/token): open the current
/// process token, extract its logon SID, build the well-known SIDs, and call
/// CreateRestrictedToken with the POC's restricting-SID allowlist. Every API call is
/// checked; any failure throws with the API name and the exact Win32 code — never spawn
/// unrestricted.
/// </summary>
internal static class RestrictedToken
{
    /// <summary>Open the current process's access token with the rights CreateRestrictedToken requires.</summary>
    public static IntPtr OpenCurrentProcessToken()
    {
        var pid = (uint)Environment.ProcessId;
        var process = Win32.OpenProcess(Win32.PROCESS_QUERY_INFORMATION, false, pid);
        if (process == IntPtr.Zero) throw Win32.Fail("OpenProcess", Marshal.GetLastWin32Error(), $"pid {pid}");
        if (!Win32.OpenProcessToken(process,
                Win32.TOKEN_QUERY | Win32.TOKEN_DUPLICATE | Win32.TOKEN_ADJUST_DEFAULT | Win32.TOKEN_ASSIGN_PRIMARY,
                out var token))
        {
            var code = Marshal.GetLastWin32Error();
            Win32.CloseHandle(process);
            throw Win32.Fail("OpenProcessToken", code, $"pid {pid}");
        }
        if (!Win32.CloseHandle(process)) throw Win32.Fail("CloseHandle", Marshal.GetLastWin32Error(), "OpenProcess process handle");
        return token;
    }

    /// <summary>
    /// Find and copy the token's logon session SID (S-1-5-5-x-y, SE_GROUP_LOGON_ID). The
    /// restricted token needs it for WinSta0/desktop and other per-logon objects.
    /// </summary>
    public static IntPtr FindLogonSid(IntPtr token)
    {
        Win32.GetTokenInformation(token, Win32.TokenGroups, IntPtr.Zero, 0, out var needed);
        if (needed == 0) throw Win32.Fail("GetTokenInformation", Marshal.GetLastWin32Error(), "TokenGroups size query");
        if (needed < Win32.TOKEN_GROUPS_OFFSET)
            throw Win32.Fail("GetTokenInformation", Marshal.GetLastWin32Error(), $"implausible TokenGroups size {needed}");
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!Win32.GetTokenInformation(token, Win32.TokenGroups, buffer, needed, out _))
                throw Win32.Fail("GetTokenInformation", Marshal.GetLastWin32Error(), "TokenGroups");
            var groupCount = Marshal.ReadInt32(buffer);
            for (var i = 0; i < groupCount; i++)
            {
                var entry = buffer + Win32.TOKEN_GROUPS_OFFSET + i * Win32.SID_AND_ATTRIBUTES_SIZE;
                var sidPtr = Marshal.ReadIntPtr(entry);
                var attributes = (uint)Marshal.ReadInt32(entry, 8);
                if (sidPtr == IntPtr.Zero || (attributes & Win32.SE_GROUP_LOGON_ID) != Win32.SE_GROUP_LOGON_ID) continue;
                var sidLength = Win32.GetLengthSid(sidPtr);
                if (sidLength == 0) throw Win32.Fail("GetLengthSid", Marshal.GetLastWin32Error(), $"logon SID group {i}");
                var copy = Marshal.AllocHGlobal((int)sidLength);
                if (!Win32.CopySid(sidLength, copy, sidPtr))
                {
                    var code = Marshal.GetLastWin32Error();
                    Marshal.FreeHGlobal(copy);
                    throw Win32.Fail("CopySid", code, $"logon SID group {i}");
                }
                return copy;
            }
            throw new HarnessError(SandboxErrorCodes.PreparationFailed,
                $"CreateRestrictedToken prerequisite failed: no logon SID found among {groupCount} token groups");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Create one well-known SID (SECURITY_MAX_SID_SIZE buffer) and assert validity.</summary>
    public static IntPtr MakeWellKnownSid(int type)
    {
        var sid = Marshal.AllocHGlobal(Win32.SECURITY_MAX_SID_SIZE);
        var size = (uint)Win32.SECURITY_MAX_SID_SIZE;
        if (!Win32.CreateWellKnownSid(type, IntPtr.Zero, sid, ref size))
        {
            var code = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(sid);
            throw Win32.Fail("CreateWellKnownSid", code, $"type {type}");
        }
        if (!Win32.IsValidSid(sid))
        {
            var code = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(sid);
            throw Win32.Fail("IsValidSid", code, $"CreateWellKnownSid type {type}");
        }
        return sid;
    }

    /// <summary>Parse an SDDL SID string into a LocalAlloc'd SID (ConvertStringSidToSidW owns the allocation).</summary>
    public static IntPtr ParseSid(string sid)
    {
        if (!Win32.ConvertStringSidToSidW(sid, out var parsed) || parsed == IntPtr.Zero)
            throw Win32.Fail("ConvertStringSidToSidW", Marshal.GetLastWin32Error(), sid);
        return parsed;
    }

    /// <summary>Free a SID allocated by ConvertStringSidToSidW (LocalFree).</summary>
    public static void FreeParsedSid(IntPtr sid)
    {
        if (sid != IntPtr.Zero) Win32.LocalFree(sid);
    }

    /// <summary>
    /// Create the write-restricted token with the mode-selected restricting list:
    /// read-only → [logon SID, EVERYONE]; workspace-write → [logon SID, EVERYONE, write SIDs…].
    /// The logon+Everyone keep-alive pair is shared (early DLL init and CNG fail without them).
    /// Write SIDs join ONLY workspace-write, so a standing grant ACE stays INERT under
    /// read-only. FAILS CLOSED: any failure throws — never spawn unrestricted.
    /// </summary>
    public static IntPtr Create(IntPtr currentToken, IntPtr logonSid, IReadOnlyList<IntPtr> writeSids, IntPtr worldSid, SandboxMode mode)
    {
        var restricting = new List<Win32.SID_AND_ATTRIBUTES>
        {
            new() { Sid = logonSid },
            new() { Sid = worldSid },
        };
        if (mode == SandboxMode.WorkspaceWrite)
        {
            if (writeSids.Count == 0)
                throw new HarnessError(SandboxErrorCodes.PreparationFailed,
                    "createRestrictedToken: workspace-write restricting list requires at least one write SID");
            restricting.AddRange(writeSids.Select(sid => new Win32.SID_AND_ATTRIBUTES { Sid = sid }));
        }
        if (!Win32.CreateRestrictedToken(currentToken,
                Win32.DISABLE_MAX_PRIVILEGE | Win32.WRITE_RESTRICTED,
                0, IntPtr.Zero, 0, IntPtr.Zero,
                (uint)restricting.Count, restricting.ToArray(), out var token) || token == IntPtr.Zero)
            throw Win32.Fail("CreateRestrictedToken", Marshal.GetLastWin32Error(), $"restricting SIDs: {restricting.Count}");
        return token;
    }

    /// <summary>
    /// Lower the restricted token's integrity to Low (S-1-16-4096) — the level the mandatory
    /// labels GrantWrite applies are matched against; a Medium token would ignore them.
    /// </summary>
    public static void RestrictIntegrity(IntPtr token, IntPtr lowLabelSid)
    {
        var label = new Win32.TOKEN_MANDATORY_LABEL
        {
            Label = new Win32.SID_AND_ATTRIBUTES { Sid = lowLabelSid, Attributes = Win32.SE_GROUP_INTEGRITY },
        };
        var size = Marshal.SizeOf<Win32.TOKEN_MANDATORY_LABEL>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(label, buffer, false);
            if (!Win32.SetTokenInformation(token, Win32.TokenIntegrityLevel, buffer, (uint)size))
                throw Win32.Fail("SetTokenInformation", Marshal.GetLastWin32Error(), "TokenIntegrityLevel (Low)");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Merge one full-access allow ACE for a restricting SID into the token's DEFAULT DACL.
    /// The inherited default DACL names no restricting SID, so every NEW object the confined
    /// process creates (anonymous stdio pipes, sync objects) would fail the write pass-2
    /// check at creation (EPERM), breaking piped-stdio spawns. The merged ACE lets new
    /// objects pass their own check while creation stays gated by the parent's DACL.
    /// </summary>
    public static void SetDefaultDaclGrant(IntPtr token, IntPtr sid)
    {
        Win32.GetTokenInformation(token, Win32.TokenDefaultDacl, IntPtr.Zero, 0, out var needed);
        if (needed == 0) throw Win32.Fail("GetTokenInformation", Marshal.GetLastWin32Error(), "TokenDefaultDacl size query");
        var buffer = Marshal.AllocHGlobal((int)needed);
        IntPtr newAcl = IntPtr.Zero;
        try
        {
            if (!Win32.GetTokenInformation(token, Win32.TokenDefaultDacl, buffer, needed, out _))
                throw Win32.Fail("GetTokenInformation", Marshal.GetLastWin32Error(), "TokenDefaultDacl");
            var currentDacl = Marshal.ReadIntPtr(buffer);
            if (currentDacl == IntPtr.Zero)
                throw new HarnessError(SandboxErrorCodes.PreparationFailed,
                    "setTokenDefaultDaclGrant: the token carries no default DACL to extend");
            var entry = new Win32.EXPLICIT_ACCESS_W
            {
                grfAccessPermissions = Win32.FILE_ALL_ACCESS,
                grfAccessMode = Win32.GRANT_ACCESS,
                grfInheritance = Win32.SUB_CONTAINERS_AND_OBJECTS_INHERIT,
                Trustee = new Win32.TRUSTEE_W { TrusteeForm = 0, TrusteeType = 0, ptstrName = sid },
            };
            var mergeResult = Win32.SetEntriesInAclW(1, [entry], currentDacl, out newAcl);
            if (mergeResult != Win32.ERROR_SUCCESS)
                throw Win32.Fail("SetEntriesInAclW", (int)mergeResult, "default DACL merge");
            if (newAcl == IntPtr.Zero)
                throw Win32.Fail("SetEntriesInAclW", Marshal.GetLastWin32Error(), "null merged default DACL");
            var info = new Win32.TOKEN_DEFAULT_DACL { DefaultDacl = newAcl };
            var infoSize = Marshal.SizeOf<Win32.TOKEN_DEFAULT_DACL>();
            var infoBuffer = Marshal.AllocHGlobal(infoSize);
            try
            {
                Marshal.StructureToPtr(info, infoBuffer, false);
                if (!Win32.SetTokenInformation(token, Win32.TokenDefaultDacl, infoBuffer, (uint)infoSize))
                    throw Win32.Fail("SetTokenInformation", Marshal.GetLastWin32Error(), "TokenDefaultDacl");
            }
            finally
            {
                Marshal.FreeHGlobal(infoBuffer);
            }
        }
        finally
        {
            if (newAcl != IntPtr.Zero) Win32.LocalFree(newAcl);
            Marshal.FreeHGlobal(buffer);
        }
    }
}
