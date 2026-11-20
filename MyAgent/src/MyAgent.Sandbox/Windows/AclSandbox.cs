using System.Runtime.InteropServices;
using System.Text;
using MyAgent.Core;

namespace MyAgent.Sandbox.Windows;

/// <summary>Construction options: the workspace/temp allowlists and their distinct SID identities.</summary>
public sealed record AclSandboxOptions(
    IReadOnlyList<string> WritableDirs,
    SandboxMode Mode,
    string? WriteSid = null,
    string? TempDir = null,
    string? TempWriteSid = null,
    bool ManageDacls = true);

/// <summary>A settled confined child: captured stdio and the exit code.</summary>
public sealed record AclChildResult(string Stdout, string Stderr, int ExitCode);

/// <summary>
/// One write-restricted sandbox instance (mirrors dsh-sandbox-windows-acl AclSandbox):
/// token + write-SID grants + spawn. Init() is fail-closed — any Win32 failure revokes the
/// revocable (temp) grants and throws; Dispose() revokes temp grants, leaves the standing
/// workspace ACEs (the cross-session reuse cache), and frees every allocation.
/// Known boundaries (inherent to restricted tokens): writes are restricted; reads, network,
/// and process visibility are NOT; granted directories must be caller-owned.
/// </summary>
public sealed class AclSandbox : IDisposable
{
    public IReadOnlyList<string> WritableDirs { get; }
    public string? WriteSid { get; }
    public string? TempWriteSid { get; }
    public SandboxMode Mode { get; }
    public string? TempDir { get; }

    private readonly bool _manageDacls;
    private IntPtr _token;
    private IntPtr _writeSidPtr;
    private IntPtr _tempWriteSidPtr;
    private readonly List<IntPtr> _sidAllocations = [];
    private readonly List<(string Path, IntPtr Sid)> _grantedPaths = [];
    private bool _initialized;

    public AclSandbox(AclSandboxOptions options)
    {
        if (!OperatingSystem.IsWindows())
            throw SandboxErrorCodes.UnavailableError(options.Mode, "the ACL backend requires Windows");
        Mode = options.Mode;
        if (Mode is not (SandboxMode.ReadOnly or SandboxMode.WorkspaceWrite))
            throw new HarnessError(SandboxErrorCodes.PreparationFailed, "AclSandbox confines only read-only or workspace-write");
        _manageDacls = options.ManageDacls;
        WritableDirs = options.WritableDirs.Select(d =>
        {
            var absolute = Path.GetFullPath(d);
            if (!Directory.Exists(absolute))
                throw new HarnessError(SandboxErrorCodes.PreparationFailed,
                    $"AclSandbox writable dir does not exist or is not a directory: {absolute}");
            return absolute;
        }).ToList();
        WriteSid = options.WriteSid;
        TempWriteSid = options.TempWriteSid;
        TempDir = options.TempDir;

        if (Mode == SandboxMode.WorkspaceWrite && WriteSid is null)
            throw new HarnessError(SandboxErrorCodes.PreparationFailed,
                "AclSandbox workspace-write requires a write SID — derive it from the workspace via WorkspaceSid.WorkspaceWriteSid()");
        if (Mode == SandboxMode.WorkspaceWrite && options.TempDir is null && options.WritableDirs.Count == 0)
            throw new HarnessError(SandboxErrorCodes.PreparationFailed,
                "AclSandbox workspace-write requires an explicit private temp directory or writable dirs");
        if (Mode == SandboxMode.ReadOnly && (TempDir is not null || WriteSid is not null || TempWriteSid is not null))
            throw new HarnessError(SandboxErrorCodes.PreparationFailed, "AclSandbox read-only does not accept temp dirs or write SIDs");
        if (Mode == SandboxMode.WorkspaceWrite && TempDir is not null && TempWriteSid is null)
            throw new HarnessError(SandboxErrorCodes.PreparationFailed,
                "AclSandbox workspace-write with temp requires a temp write SID");
        if (TempDir is null && TempWriteSid is not null)
            throw new HarnessError(SandboxErrorCodes.PreparationFailed, "AclSandbox temp write SID requires a temp directory");
        if (WriteSid is not null && TempWriteSid == WriteSid)
            throw new HarnessError(SandboxErrorCodes.PreparationFailed, "AclSandbox workspace and temp write SIDs must be distinct");
        if (TempDir is not null)
        {
            if (!Directory.Exists(TempDir))
                throw new HarnessError(SandboxErrorCodes.PreparationFailed, $"AclSandbox temp dir does not exist: {TempDir}");
            PathBoundary.AssertPrivateTempDisjoint(WritableDirs, TempDir);
        }
    }

    /// <summary>Create the restricted token and apply the capability-SID grants. Once per instance.</summary>
    public void Init()
    {
        if (_initialized) throw new HarnessError(SandboxErrorCodes.PreparationFailed, "AclSandbox is already initialized");
        IntPtr currentToken = IntPtr.Zero;
        IntPtr restrictedToken = IntPtr.Zero;
        var currentTokenOpen = false;
        try
        {
            if (WriteSid is not null) _writeSidPtr = RestrictedToken.ParseSid(WriteSid);
            if (TempWriteSid is not null) _tempWriteSidPtr = RestrictedToken.ParseSid(TempWriteSid);

            var lowLabelSid = RestrictedToken.MakeWellKnownSid(Win32.WinLowLabelSid);
            var worldSid = RestrictedToken.MakeWellKnownSid(Win32.WinWorldSid);
            _sidAllocations.Add(lowLabelSid);
            _sidAllocations.Add(worldSid);

            if (_manageDacls && _writeSidPtr != IntPtr.Zero)
            {
                foreach (var path in WritableDirs)
                    Acl.GrantWrite(path, _writeSidPtr, lowLabelSid, worldSid);
                if (TempDir is not null && _tempWriteSidPtr != IntPtr.Zero)
                {
                    // Record BEFORE granting: a throw after a successful apply must still revoke.
                    _grantedPaths.Add((TempDir, _tempWriteSidPtr));
                    Acl.GrantWrite(TempDir, _tempWriteSidPtr, lowLabelSid, worldSid);
                }
            }

            currentToken = RestrictedToken.OpenCurrentProcessToken();
            currentTokenOpen = true;
            var logonSid = RestrictedToken.FindLogonSid(currentToken);
            _sidAllocations.Add(logonSid);
            var writeSids = new List<IntPtr>();
            if (_writeSidPtr != IntPtr.Zero) writeSids.Add(_writeSidPtr);
            if (_tempWriteSidPtr != IntPtr.Zero) writeSids.Add(_tempWriteSidPtr);
            restrictedToken = RestrictedToken.Create(currentToken, logonSid, writeSids, worldSid, Mode);
            RestrictedToken.RestrictIntegrity(restrictedToken, lowLabelSid);
            // New objects the confined process creates take the token's default DACL; merge a
            // restricting SID's full-access ACE so anonymous pipes etc. pass the write check.
            RestrictedToken.SetDefaultDaclGrant(restrictedToken,
                _tempWriteSidPtr != IntPtr.Zero ? _tempWriteSidPtr : _writeSidPtr != IntPtr.Zero ? _writeSidPtr : worldSid);
            Win32.CloseHandle(currentToken);
            currentTokenOpen = false;
            _token = restrictedToken;
            restrictedToken = IntPtr.Zero;
            _initialized = true;
        }
        catch
        {
            // Fail-closed cleanup: never leave a revocable grant or allocation behind a failed init.
            if (currentTokenOpen && currentToken != IntPtr.Zero) Win32.CloseHandle(currentToken);
            if (restrictedToken != IntPtr.Zero) Win32.CloseHandle(restrictedToken);
            foreach (var (path, sid) in _grantedPaths)
            {
                try { Acl.RevokeWrite(path, sid); } catch { /* best effort; original error wins */ }
            }
            _grantedPaths.Clear();
            RestrictedToken.FreeParsedSid(_writeSidPtr);
            RestrictedToken.FreeParsedSid(_tempWriteSidPtr);
            _writeSidPtr = _tempWriteSidPtr = IntPtr.Zero;
            foreach (var sid in _sidAllocations) Marshal.FreeHGlobal(sid);
            _sidAllocations.Clear();
            throw;
        }
    }

    private static readonly object ConsoleGate = new();
    private static bool _consoleEnsured;

    /// <summary>
    /// Give a GUI host (WPF, no console) ONE hidden console shared by every confined child.
    /// Without it, each console child gets a BRAND-NEW console + conhost.exe (hundreds of
    /// "控制台主机" processes over a long session). Restricted children cannot use
    /// CREATE_NO_WINDOW (STATUS_DLL_INIT_FAILED), so sharing the host console is the only
    /// option — exactly the harness's documented boundary. A host that already owns a
    /// console (terminal/debugger) keeps it untouched.
    /// </summary>
    private static void EnsureHostConsole()
    {
        lock (ConsoleGate)
        {
            if (_consoleEnsured) return;
            _consoleEnsured = true;
            if (Win32.GetConsoleWindow() != IntPtr.Zero) return; // already attached — never hide the user's terminal
            if (Win32.AllocConsole())
                Win32.ShowWindow(Win32.GetConsoleWindow(), Win32.SW_HIDE);
        }
    }

    /// <summary>
    /// Spawn a process under the restricted token with piped stdio. Fails closed: throws on
    /// every Win32 failure; the child is never created unrestricted. TEMP/TMP point at the
    /// private temp directory when one is granted. The child runs inside a kill-on-close Job:
    /// timeout/cancel terminates the WHOLE tree (no orphaned dotnet/MSBuild builds), and
    /// closing the job after a normal exit reaps any lingering grandchildren (build nodes).
    /// </summary>
    public async Task<AclChildResult> RunAsync(string command, IReadOnlyList<string> args, string cwd, CancellationToken ct)
    {
        if (!_initialized || _token == IntPtr.Zero)
            throw new HarnessError(SandboxErrorCodes.PreparationFailed, "AclSandbox is not initialized: call Init() first");

        EnsureHostConsole();
        var commandLine = BuildCommandLine(command, args);
        var env = BuildEnvironmentBlock();
        var job = Win32.CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw Win32.Fail("CreateJobObjectW", Marshal.GetLastWin32Error(), command);
        var limits = new Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new Win32.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = Win32.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };
        var limitsSize = Marshal.SizeOf<Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var limitsBuffer = Marshal.AllocHGlobal(limitsSize);
        try
        {
            Marshal.StructureToPtr(limits, limitsBuffer, false);
            if (!Win32.SetInformationJobObject(job, Win32.JobObjectExtendedLimitInformation, limitsBuffer, (uint)limitsSize))
                throw Win32.Fail("SetInformationJobObject", Marshal.GetLastWin32Error(), command);
        }
        finally
        {
            Marshal.FreeHGlobal(limitsBuffer);
        }

        var sa = new Win32.SECURITY_ATTRIBUTES
        {
            nLength = (uint)Marshal.SizeOf<Win32.SECURITY_ATTRIBUTES>(),
            bInheritHandle = true,
        };
        IntPtr outRead = IntPtr.Zero, outWrite = IntPtr.Zero, errRead = IntPtr.Zero, errWrite = IntPtr.Zero;
        IntPtr inRead = IntPtr.Zero, inWrite = IntPtr.Zero;
        var spawned = false;
        Win32.PROCESS_INFORMATION pi = default;
        try
        {
            if (!Win32.CreatePipe(out outRead, out outWrite, ref sa, 0))
                throw Win32.Fail("CreatePipe", Marshal.GetLastWin32Error(), "stdout");
            if (!Win32.CreatePipe(out errRead, out errWrite, ref sa, 0))
                throw Win32.Fail("CreatePipe", Marshal.GetLastWin32Error(), "stderr");
            if (!Win32.CreatePipe(out inRead, out inWrite, ref sa, 0))
                throw Win32.Fail("CreatePipe", Marshal.GetLastWin32Error(), "stdin");
            // Parent-side ends must not be inheritable.
            Win32.SetHandleInformation(outRead, Win32.HANDLE_FLAG_INHERIT, 0);
            Win32.SetHandleInformation(errRead, Win32.HANDLE_FLAG_INHERIT, 0);
            Win32.SetHandleInformation(inWrite, Win32.HANDLE_FLAG_INHERIT, 0);

            var si = new Win32.STARTUPINFOW
            {
                cb = (uint)Marshal.SizeOf<Win32.STARTUPINFOW>(),
                dwFlags = Win32.STARTF_USESTDHANDLES,
                hStdInput = inRead,
                hStdOutput = outWrite,
                hStdError = errWrite,
            };
            // No CREATE_NO_WINDOW / CREATE_NEW_CONSOLE: under the write restriction those
            // children die with STATUS_DLL_INIT_FAILED (the harness's documented boundary —
            // console isolation is unavailable; children share the host's hidden console).
            // CREATE_SUSPENDED lets us put the child in the kill-on-close Job BEFORE any of
            // its code (or descendants) can run — no escape window.
            if (!Win32.CreateProcessAsUserW(_token, null, commandLine,
                    IntPtr.Zero, IntPtr.Zero, true,
                    Win32.CREATE_UNICODE_ENVIRONMENT | Win32.CREATE_SUSPENDED,
                    env, cwd, ref si, out pi))
                throw Win32.Fail("CreateProcessAsUserW", Marshal.GetLastWin32Error(), command);
            spawned = true;
            if (!Win32.AssignProcessToJobObject(job, pi.hProcess))
            {
                var code = Marshal.GetLastWin32Error();
                Win32.TerminateProcess(pi.hProcess, 1);
                throw Win32.Fail("AssignProcessToJobObject", code, command);
            }
            Win32.ResumeThread(pi.hThread);

            // Close the child-side ends in the parent so the drains see EOF at child exit.
            Win32.CloseHandle(outWrite); outWrite = IntPtr.Zero;
            Win32.CloseHandle(errWrite); errWrite = IntPtr.Zero;
            Win32.CloseHandle(inRead); inRead = IntPtr.Zero;
            Win32.CloseHandle(inWrite); inWrite = IntPtr.Zero; // immediate stdin EOF
            Win32.CloseHandle(pi.hThread); pi.hThread = IntPtr.Zero;

            var stdoutTask = Task.Run(() => DrainPipe(outRead), CancellationToken.None);
            var stderrTask = Task.Run(() => DrainPipe(errRead), CancellationToken.None);
            var waitTask = Task.Run(() =>
            {
                while (true)
                {
                    var status = Win32.WaitForSingleObject(pi.hProcess, 200);
                    if (status != Win32.WAIT_TIMEOUT) break;
                    if (ct.IsCancellationRequested)
                    {
                        // Kill the WHOLE tree (cmd + dotnet + MSBuild nodes + compilers) —
                        // TerminateProcess on the direct child alone orphans the build.
                        Win32.TerminateJobObject(job, 1);
                        ct.ThrowIfCancellationRequested();
                    }
                }
                Win32.GetExitCodeProcess(pi.hProcess, out var exitCode);
                return (int)exitCode;
            }, CancellationToken.None);

            var exit = await waitTask;
            // Reap lingering grandchildren (build nodes, compiler servers) BEFORE draining:
            // they hold the inherited pipe write-ends open, so EOF only arrives once the
            // whole job is gone. A normally-exited command's stragglers die here too.
            Win32.TerminateJobObject(job, 0);
            var stdout = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(10));
            var stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(10));
            return new AclChildResult(stdout, stderr, exit);
        }
        finally
        {
            if (env != IntPtr.Zero) Marshal.FreeHGlobal(env);
            foreach (var handle in new[] { outRead, outWrite, errRead, errWrite, inRead, inWrite })
                if (handle != IntPtr.Zero) Win32.CloseHandle(handle);
            if (spawned && pi.hThread != IntPtr.Zero) Win32.CloseHandle(pi.hThread);
            if (spawned && pi.hProcess != IntPtr.Zero) Win32.CloseHandle(pi.hProcess);
            // kill-on-close: anything still alive in the job dies with this handle.
            Win32.CloseHandle(job);
        }
    }

    /// <summary>Per-stream capture cap: keep the LAST 1MB, drain the rest (bounded memory).</summary>
    private const int MaxCapturedBytes = 1_048_576;

    /// <summary>Read one pipe to EOF (synchronous ReadFile on a worker thread), keeping a bounded tail.</summary>
    private static string DrainPipe(IntPtr readHandle)
    {
        var buffer = Marshal.AllocHGlobal(8192);
        var chunks = new LinkedList<byte[]>();
        var total = 0;
        var dropped = false;
        try
        {
            while (true)
            {
                if (!Win32.ReadFile(readHandle, buffer, 8192, out var read, IntPtr.Zero))
                {
                    var code = Marshal.GetLastWin32Error();
                    if (code == 109) break; // ERROR_BROKEN_PIPE: child closed its end
                    throw Win32.Fail("ReadFile", code, "stdio drain");
                }
                if (read == 0) break;
                var chunk = new byte[read];
                Marshal.Copy(buffer, chunk, 0, (int)read);
                chunks.AddLast(chunk);
                total += chunk.Length;
                // Evict the oldest chunks past the cap — a runaway command must not balloon
                // host memory (the previous unbounded MemoryStream was a memory bomb).
                while (total > MaxCapturedBytes && chunks.First is { } first)
                {
                    total -= first.Value.Length;
                    chunks.RemoveFirst();
                    dropped = true;
                }
            }
            var bytes = new byte[total];
            var offset = 0;
            foreach (var chunk in chunks)
            {
                Buffer.BlockCopy(chunk, 0, bytes, offset, chunk.Length);
                offset += chunk.Length;
            }
            var text = DecodeOutput(bytes);
            return dropped ? "[output truncated to the last 1MB]\n" + text : text;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Decode captured stdio: strict UTF-8 first (bash, .NET tools); on invalid sequences fall
    /// back to the console code page — cmd.exe on localized Windows emits OEM text (e.g. GBK
    /// "拒绝访问。" on zh-CN), which the denial dialect must be able to match.
    /// </summary>
    internal static string DecodeOutput(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding((int)GetConsoleOutputCP()).GetString(bytes);
            }
            catch (ArgumentException)
            {
                return Encoding.UTF8.GetString(bytes);
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();

    /// <summary>Quote argv per CommandLineToArgvW rules.</summary>
    internal static StringBuilder BuildCommandLine(string command, IReadOnlyList<string> args)
    {
        // cmd.exe does NOT parse its /c tail with CommandLineToArgvW: backslash-escaped
        // inner quotes confuse it ("文件名…语法不正确"). The /d /s /c convention strips the
        // FIRST and LAST quote and keeps the rest verbatim — emit the raw command inside
        // one outer quote pair (the same spelling the unconfined shell path uses).
        if (args.Count == 4 && args[0] == "/d" && args[1] == "/s" && args[2] == "/c"
            && Path.GetFileName(command).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            return new StringBuilder($"cmd.exe /d /s /c \"{args[3]}\"");
        }

        var sb = new StringBuilder();
        sb.Append(Quote(command));
        foreach (var arg in args)
        {
            sb.Append(' ');
            sb.Append(Quote(arg));
        }
        return sb;

        static string Quote(string value)
        {
            if (value.Length > 0 && !value.Any(c => c is ' ' or '\t' or '"' or '\n')) return value;
            var sb = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var c in value)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', backslashes); sb.Append(c); }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }

    /// <summary>
    /// Build a Unicode environment block from the caller's environment, pointing TEMP/TMP at
    /// the private temp directory when granted (the ambient temp root is never writable).
    /// </summary>
    private IntPtr BuildEnvironmentBlock()
    {
        var entries = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            entries[(string)entry.Key] = entry.Value?.ToString() ?? "";
        if (TempDir is not null)
        {
            entries["TEMP"] = TempDir;
            entries["TMP"] = TempDir;
        }
        ShellEnvironment.ApplyBuildHygiene(entries);
        var sb = new StringBuilder();
        foreach (var (key, value) in entries)
        {
            if (key.StartsWith('=')) continue; // hidden drive-cwd entries
            sb.Append(key).Append('=').Append(value).Append('\0');
        }
        sb.Append('\0');
        var bytes = Encoding.Unicode.GetBytes(sb.ToString());
        var block = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, block, bytes.Length);
        return block;
    }

    /// <summary>
    /// Revoke the revocable (temp) grants, free the SIDs, close the token; standing workspace
    /// ACEs stay (the reuse cache). Call only after all children have exited.
    /// </summary>
    public void Dispose()
    {
        if (_manageDacls)
        {
            foreach (var (path, sid) in _grantedPaths)
            {
                try { Acl.RevokeWrite(path, sid); } catch { /* best effort */ }
            }
        }
        _grantedPaths.Clear();
        RestrictedToken.FreeParsedSid(_writeSidPtr);
        RestrictedToken.FreeParsedSid(_tempWriteSidPtr);
        _writeSidPtr = _tempWriteSidPtr = IntPtr.Zero;
        foreach (var sid in _sidAllocations) Marshal.FreeHGlobal(sid);
        _sidAllocations.Clear();
        if (_token != IntPtr.Zero)
        {
            Win32.CloseHandle(_token);
            _token = IntPtr.Zero;
        }
        _initialized = false;
    }
}
