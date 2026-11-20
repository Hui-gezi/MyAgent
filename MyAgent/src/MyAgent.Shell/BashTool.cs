using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MyAgent.Core;
using MyAgent.Sandbox;

namespace MyAgent.Shell;

public sealed class BashTool : ITool
{
    private const int DefaultTimeoutMs = 120_000;
    private const int MaxTimeoutMs = 600_000;
    private const int TailBytes = 64_000;

    private readonly ToolSandbox? _sandbox;
    private readonly JsonElement _schema;

    /// <summary>Without a ToolSandbox the tool runs unconfined (danger-full-access composition).</summary>
    public BashTool(ToolSandbox? sandbox = null)
    {
        _sandbox = sandbox;
        var baseSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "description": { "type": "string", "description": "Short description of what this command does" },
            "command": { "type": "string", "description": "The shell command to execute" },
            "timeoutMs": { "type": "integer", "description": "Timeout in milliseconds (default 120000, max 600000)" },
            "workdir": { "type": "string", "description": "Working directory for the command" }
          },
          "required": ["description", "command"]
        }
        """).RootElement;
        _schema = sandbox?.AddEscalationFields(baseSchema, "command") ?? baseSchema;
    }

    public string Name => "bash";
    public string Description =>
        "Execute a shell command and return its output. Commands run in bash when available, otherwise the system shell. " +
        "Provide a short description of what the command does. Default timeout is 120000ms (max 600000ms). " +
        "The working directory defaults to the session working directory; use workdir to override.";
    public JsonElement InputSchema => _schema;
    public string PromptSection => "Check the [exit code: N] marker on every bash result; investigate failures before moving on.";
    public int PromptOrder => 1000;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        var command = GetString(args, "command");
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Error("command must not be empty.", "invalid_args");
        var timeoutMs = Math.Clamp(GetInt(args, "timeoutMs", DefaultTimeoutMs), 1, MaxTimeoutMs);
        var workdir = args.TryGetProperty("workdir", out var wd) && wd.ValueKind == JsonValueKind.String
            ? Path.GetFullPath(Path.IsPathRooted(wd.GetString()!) ? wd.GetString()! : Path.Combine(context.Cwd, wd.GetString()!))
            : context.Cwd;
        if (!Directory.Exists(workdir))
            return ToolResult.Error($"Working directory not found: {workdir}", "workdir_not_found");

        // Resolve the per-call policy FIRST: an approved escalation is a new call with a wider
        // policy; a rejection/unavailable outcome throws before anything executes (fail closed).
        if (_sandbox is not null)
        {
            var policy = await _sandbox.ResolvePolicyAsync(Name, args, context, cancellationToken);
            if (policy.Mode != SandboxMode.DangerFullAccess)
            {
                var (confinedFile, confinedArgs) = ResolveShellArgv(command);
                using var confinedTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                confinedTimeout.CancelAfter(timeoutMs);
                try
                {
                    var confined = await _sandbox.RunConfinedAsync(confinedFile, confinedArgs, workdir, policy, confinedTimeout.Token);
                    if (confined is not null)
                        return RenderConfined(confined, policy.Mode, timeoutMs);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new ToolResult($"(no output)\n[timed out after {timeoutMs}ms]");
                }
            }
            // danger-full-access: fall through to the explicit unconfined path below.
        }

        var (fileName, arguments) = ResolveShell(command);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Same build hygiene as the confined path: no lingering MSBuild/Roslyn servers.
        var hygiene = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        MyAgent.Sandbox.ShellEnvironment.ApplyBuildHygiene(hygiene);
        foreach (var (key, value) in hygiene)
            process.StartInfo.Environment[key] = value;

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to start shell '{fileName}': {ex.Message}", "spawn_failed");
        }

        var stdout = new TailBuffer(TailBytes);
        var stderr = new TailBuffer(TailBytes);
        var stdoutTask = PumpAsync(process.StandardOutput, stdout, cancellationToken);
        var stderrTask = PumpAsync(process.StandardError, stderr, cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (!timedOut)
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2));
                throw;
            }
        }

        try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { }

        var output = new StringBuilder();
        output.Append(stdout.ToString());
        if (stderr.Length > 0)
            output.Append("\n[stderr]\n").Append(stderr.ToString());
        if (timedOut)
            output.Append($"\n[timed out after {timeoutMs}ms]");
        else
            output.Append($"\n[exit code: {process.ExitCode}]");
        return new ToolResult(output.ToString());
    }

    private static (string FileName, string Arguments) ResolveShell(string command)
    {
        var bash = FindOnPath("bash.exe") ?? FindOnPath("bash");
        if (bash is not null) return (bash, $"-c \"{command.Replace("\"", "\\\"")}\"");
        return ("cmd.exe", $"/d /s /c \"{command}\"");
    }

    /// <summary>
    /// The exact argv a confined execution spawns (a shell-shaped consumer passes
    /// ['bash','-c',command] — no pre-joined shell string, quoting is the runner's job).
    /// </summary>
    private static (string FileName, IReadOnlyList<string> Args) ResolveShellArgv(string command)
    {
        var bash = FindOnPath("bash.exe") ?? FindOnPath("bash");
        if (bash is not null) return (bash, ["-c", command]);
        return ("cmd.exe", ["/d", "/s", "/c", command]);
    }

    /// <summary>
    /// Shape one confined run into the text the model sees (mirrors tool-bash renderResult):
    /// stdout, [stderr] section, then markers — denial marker + escalation hint BEFORE the
    /// final exit marker. Runner failure is classified first: it means the command never ran.
    /// </summary>
    private ToolResult RenderConfined(ConfinedRunResult run, SandboxMode mode, int timeoutMs)
    {
        var body = new StringBuilder();
        body.Append(Tail(run.Stdout));
        if (run.Stderr.Length > 0)
        {
            if (body.Length > 0 && body[^1] != '\n') body.Append('\n');
            body.Append("[stderr]\n").Append(Tail(run.Stderr));
        }
        if (body.Length == 0) body.Append("(no output)");

        var markers = new List<string>();
        var runnerFailure = SandboxDiagnostics.ClassifyRunnerFailure(run.ExitCode, run.Stderr, run.RunnerFailureRules);
        if (runnerFailure is not null)
        {
            markers.Add($"[sandbox: the sandbox runner itself failed under {mode.Wire()} mode — the command did not run; this is a sandbox problem, not a command failure]");
            markers.Add($"[exit code: {run.ExitCode}]");
            return new ToolResult(AppendMarkers(body, markers));
        }
        if (SandboxDiagnostics.MatchesSignature(run.ExitCode, run.Stderr, run.DenialSignatures))
        {
            markers.Add(Escalation.DenialMarker(mode));
            if (_sandbox is { EscalationModes.Count: > 0 })
                markers.Add(Escalation.HintMarker("command"));
        }
        markers.Add($"[exit code: {run.ExitCode}]");
        return new ToolResult(AppendMarkers(body, markers));

        static string AppendMarkers(StringBuilder body, List<string> markers)
        {
            if (body.Length > 0 && body[^1] != '\n') body.Append('\n');
            return body.Append(string.Join('\n', markers)).ToString();
        }

        static string Tail(string text) =>
            Encoding.UTF8.GetByteCount(text) <= TailBytes
                ? text
                : text[^Math.Min(text.Length, TailBytes)..];
    }

    private static string? FindOnPath(string executable)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim(), executable);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static async Task PumpAsync(StreamReader reader, TailBuffer buffer, CancellationToken cancellationToken)
    {
        var chunk = new char[4096];
        try
        {
            while (await reader.ReadAsync(chunk.AsMemory(), cancellationToken) is var read && read > 0)
                buffer.Append(chunk.AsSpan(0, read));
        }
        catch (OperationCanceledException) { }
    }

    private static string GetString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : throw new HarnessError("invalid_args", $"Missing required string argument '{name}'.");

    private static int GetInt(JsonElement args, string name, int fallback) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : fallback;

    private sealed class TailBuffer(int capacity)
    {
        private readonly StringBuilder _builder = new();

        public int Length => _builder.Length;

        public void Append(ReadOnlySpan<char> text)
        {
            lock (_builder)
            {
                _builder.Append(text);
                if (_builder.Length > capacity)
                    _builder.Remove(0, _builder.Length - capacity);
            }
        }

        public override string ToString()
        {
            lock (_builder) return _builder.ToString();
        }
    }
}