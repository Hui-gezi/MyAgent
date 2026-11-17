using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MyAgent.Core;

namespace MyAgent.Shell;

public sealed class BashTool : ITool
{
    private const int DefaultTimeoutMs = 120_000;
    private const int MaxTimeoutMs = 600_000;
    private const int TailBytes = 64_000;

    public string Name => "bash";
    public string Description =>
        "Execute a shell command and return its output. Commands run in bash when available, otherwise the system shell. " +
        "Provide a short description of what the command does. Default timeout is 120000ms (max 600000ms). " +
        "The working directory defaults to the session working directory; use workdir to override.";
    public JsonElement InputSchema => JsonDocument.Parse("""
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