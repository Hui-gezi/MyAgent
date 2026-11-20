using System.Runtime.InteropServices;

namespace MyAgent.Sandbox.Windows;

/// <summary>
/// P/Invoke surface for the Windows ACL write-restriction backend (mirrors
/// dsh-sandbox-windows-acl ffi/win32-abi). Every call is checked by the caller;
/// any failure throws with the API name and the exact Win32 code — a child is
/// NEVER spawned unrestricted.
/// </summary>
internal static class Win32
{
    // ---- constants (mirrors win32-abi.ts) ----
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    public const uint TOKEN_DUPLICATE = 0x0002;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    public const uint SE_GROUP_LOGON_ID = 0xC0000000;
    public const uint STANDARD_RIGHTS_WRITE = 0x00020000;
    public const uint FILE_GENERIC_WRITE = 0x00120116;
    public const uint DELETE = 0x00010000;
    public const uint FILE_DELETE_CHILD = 0x0040;
    public const uint GRANT_MASK = (FILE_GENERIC_WRITE | DELETE | FILE_DELETE_CHILD) & ~STANDARD_RIGHTS_WRITE;
    public const uint FILE_ALL_ACCESS = 0x1F01FF;
    public const uint DISABLE_MAX_PRIVILEGE = 0x1;
    public const uint WRITE_RESTRICTED = 0x8;
    public const int WinWorldSid = 1;
    public const int WinLowLabelSid = 66;
    public const int TokenGroups = 2;
    public const int TokenDefaultDacl = 6;
    public const int TokenIntegrityLevel = 25;
    public const uint DACL_SECURITY_INFORMATION = 0x00000004;
    public const uint LABEL_SECURITY_INFORMATION = 0x00000010;
    public const byte SYSTEM_MANDATORY_LABEL_ACE_TYPE = 0x11;
    public const uint SYSTEM_MANDATORY_LABEL_NO_WRITE_UP = 0x00000001;
    public const uint SE_GROUP_INTEGRITY = 0x00000020;
    public const int ACL_REVISION = 2;
    public const uint LPTR = 0x0040;
    public const int SE_FILE_OBJECT = 1;
    public const uint GRANT_ACCESS = 1;
    public const uint DENY_ACCESS = 3;
    public const uint REVOKE_ACCESS = 4;
    public const uint SUB_CONTAINERS_AND_OBJECTS_INHERIT = 0x3;
    public const uint CONTAINER_INHERIT_ACE = 0x2;
    public const uint ERROR_SUCCESS = 0;
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_ALWAYS = 4;
    public const uint LOCKFILE_EXCLUSIVE_LOCK = 0x2;
    public const byte ACCESS_ALLOWED_ACE_TYPE = 0;
    public const byte ACCESS_DENIED_ACE_TYPE = 1;
    public const int SECURITY_MAX_SID_SIZE = 68;
    public const int SID_AND_ATTRIBUTES_SIZE = 16;
    public const int TOKEN_GROUPS_OFFSET = 8;
    public const uint LOGON_WITH_PROFILE = 0x00000001;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint CREATE_NO_WINDOW = 0x08000000;
    public const uint STARTF_USESTDHANDLES = 0x00000100;
    public const uint HANDLE_FLAG_INHERIT = 0x00000001;
    public const uint WAIT_TIMEOUT = 0x00000102;
    public const uint INFINITE = 0xFFFFFFFF;

    public static readonly IntPtr InvalidHandle = new(-1);

    // ---- structs ----
    [StructLayout(LayoutKind.Sequential)]
    public struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TRUSTEE_W
    {
        public IntPtr pMultipleTrustee;
        public uint MultipleTrusteeOperation;
        public uint TrusteeForm;   // TRUSTEE_IS_SID = 0
        public uint TrusteeType;   // TRUSTEE_IS_UNKNOWN = 0
        public IntPtr ptstrName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EXPLICIT_ACCESS_W
    {
        public uint grfAccessPermissions;
        public uint grfAccessMode;
        public uint grfInheritance;
        public TRUSTEE_W Trustee;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_MANDATORY_LABEL
    {
        public SID_AND_ATTRIBUTES Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_DEFAULT_DACL
    {
        public IntPtr DefaultDacl;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public uint nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFOW
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

    // ---- kernel32 ----
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr LocalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, ref SECURITY_ATTRIBUTES attrs, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadFile(IntPtr file, IntPtr buffer, uint toRead, out uint read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(string fileName, uint access, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LockFileEx(IntPtr file, uint flags, uint reserved, uint countLow, uint countHigh, ref OVERLAPPED overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnlockFileEx(IntPtr file, uint reserved, uint countLow, uint countHigh, ref OVERLAPPED overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetTempPathW(uint bufferLength, [Out] char[] buffer);

    // ---- advapi32: token ----
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, uint infoLength, out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetTokenInformation(IntPtr token, int infoClass, IntPtr info, uint infoLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateRestrictedToken(IntPtr existingToken, uint flags,
        uint disableSidCount, IntPtr sidsToDisable,
        uint deletePrivilegeCount, IntPtr privilegesToDelete,
        uint restrictingSidCount, SID_AND_ATTRIBUTES[] sidsToRestrict,
        out IntPtr newToken);

    // ---- advapi32: SID ----
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ConvertStringSidToSidW(string stringSid, out IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateWellKnownSid(int wellKnownSidType, IntPtr domainSid, IntPtr sid, ref uint sidSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsValidSid(IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern uint GetLengthSid(IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CopySid(uint destLength, IntPtr dest, IntPtr source);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EqualSid(IntPtr sid1, IntPtr sid2);

    // ---- advapi32: ACL / security ----
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetNamedSecurityInfoW(string objectName, int objectType, uint securityInfo,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint SetNamedSecurityInfoW(string objectName, int objectType, uint securityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint SetEntriesInAclW(uint countOfEntries, EXPLICIT_ACCESS_W[] entries, IntPtr oldAcl, out IntPtr newAcl);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InitializeAcl(IntPtr acl, uint aclLength, uint aclRevision);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddMandatoryAce(IntPtr acl, uint aclRevision, uint aceFlags, uint mandatoryPolicy, IntPtr labelSid);

    // ---- console: one hidden host console shared by every confined child ----
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public const int SW_HIDE = 0;

    // ---- job objects: kill-on-close + whole-tree termination ----
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    public const int JobObjectExtendedLimitInformation = 9;
    public const uint CREATE_SUSPENDED = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint ResumeThread(IntPtr thread);

    // ---- advapi32 / userenv: spawn under a primary token ----
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcessAsUserW(IntPtr token, string? applicationName, System.Text.StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFOW startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyEnvironmentBlock(IntPtr environment);

    /// <summary>Throw with the API name and exact Win32 code (the POC ignored these; we never do).</summary>
    public static Exception Fail(string api, int code, string? detail = null) =>
        new SandboxWin32Exception(api, code, detail);
}

/// <summary>A checked Win32 failure carrying the API name and exact error code.</summary>
public sealed class SandboxWin32Exception(string api, int code, string? detail = null)
    : Exception($"{api} failed with Win32 error {code}{(detail is null ? "" : $" ({detail})")}")
{
    public string Api { get; } = api;
    public int Code { get; } = code;
}
