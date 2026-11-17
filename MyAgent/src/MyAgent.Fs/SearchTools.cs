using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyAgent.Core;

namespace MyAgent.Fs;

public sealed class GlobTool : ITool
{
    private const int MaxResults = 100;
    private static readonly string[] SkippedDirectories = [".git", "node_modules", "bin", "obj"];

    public string Name => "glob";
    public string Description =>
        "Find files by glob pattern (e.g. \"**/*.cs\" or \"src/**/*.ts\"). " +
        "Returns matching paths sorted by modification time, newest first, up to 100 results.";
    public JsonElement InputSchema => FsSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Glob pattern to match file paths against" },
            "path": { "type": "string", "description": "Directory to search in; defaults to the working directory" }
          },
          "required": ["pattern"]
        }
        """);
    public string PromptSection => "Use glob to locate files by name pattern before reading them.";
    public int PromptOrder => 1130;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        var pattern = FsSchema.GetString(args, "pattern");
        var root = args.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String
            ? FsSchema.ResolvePath(context, pathEl.GetString()!)
            : context.Cwd;
        if (!Directory.Exists(root))
            return Task.FromResult(ToolResult.Error($"Directory not found: {root}", "directory_not_found"));

        var regex = GlobToRegex(pattern);
        var matches = new List<(string Path, DateTime Modified)>();
        foreach (var file in EnumerateFiles(root, cancellationToken))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!regex.IsMatch(relative)) continue;
            matches.Add((relative, File.GetLastWriteTimeUtc(file)));
            if (matches.Count >= MaxResults * 4) break;
        }

        var ordered = matches.OrderByDescending(m => m.Modified).Take(MaxResults).ToList();
        if (ordered.Count == 0)
            return Task.FromResult(new ToolResult("No files matched the pattern."));
        var output = new StringBuilder();
        foreach (var (path, _) in ordered) output.AppendLine(path);
        if (matches.Count > MaxResults)
            output.AppendLine($"[showing newest {MaxResults} of {matches.Count}+ matches; narrow the pattern]");
        return Task.FromResult(new ToolResult(output.ToString()));
    }

    internal static IEnumerable<string> EnumerateFiles(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    if (!SkippedDirectories.Contains(Path.GetFileName(entry)))
                        pending.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    internal static Regex GlobToRegex(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        var sb = new StringBuilder("^");
        for (var i = 0; i < normalized.Length; i++)
        {
            var ch = normalized[i];
            if (ch == '*' && i + 1 < normalized.Length && normalized[i + 1] == '*')
            {
                sb.Append(".*");
                i++;
                if (i + 1 < normalized.Length && normalized[i + 1] == '/') i++;
            }
            else if (ch == '*') sb.Append("[^/]*");
            else if (ch == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(ch.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    }
}

public sealed class GrepTool : ITool
{
    private const int MaxMatches = 250;
    private const long MaxFileSize = 1024 * 1024;

    public string Name => "grep";
    public string Description =>
        "Search file contents with a regular expression. Returns matching lines as path:line:text, up to 250 matches. " +
        "Use include to restrict the searched file names (e.g. \"*.cs\").";
    public JsonElement InputSchema => FsSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Regular expression to search for" },
            "path": { "type": "string", "description": "Directory or file to search; defaults to the working directory" },
            "include": { "type": "string", "description": "Glob filter for file names, e.g. *.cs" }
          },
          "required": ["pattern"]
        }
        """);
    public string PromptSection => "Use grep to search file contents; narrow with include and path to stay under the match limit.";
    public int PromptOrder => 1140;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        var pattern = FsSchema.GetString(args, "pattern");
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolResult.Error($"Invalid regular expression: {ex.Message}", "invalid_pattern"));
        }

        var root = args.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String
            ? FsSchema.ResolvePath(context, pathEl.GetString()!)
            : context.Cwd;
        Regex? include = args.TryGetProperty("include", out var includeEl) && includeEl.ValueKind == JsonValueKind.String
            ? GlobTool.GlobToRegex(includeEl.GetString()!)
            : null;

        IEnumerable<string> files;
        if (File.Exists(root)) files = [root];
        else if (Directory.Exists(root)) files = GlobTool.EnumerateFiles(root, cancellationToken);
        else return Task.FromResult(ToolResult.Error($"Path not found: {root}", "path_not_found"));

        var output = new StringBuilder();
        var matchCount = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matchCount >= MaxMatches) break;
            var name = Path.GetFileName(file).Replace('\\', '/');
            if (include is not null && !include.IsMatch(name)) continue;
            try
            {
                if (new FileInfo(file).Length > MaxFileSize) continue;
            }
            catch (IOException) { continue; }

            string[] lines;
            try { lines = File.ReadAllLines(file, Encoding.UTF8); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            var relative = Path.GetRelativePath(context.Cwd, file).Replace('\\', '/');
            for (var i = 0; i < lines.Length && matchCount < MaxMatches; i++)
            {
                if (!regex.IsMatch(lines[i])) continue;
                var text = lines[i].Length > 500 ? lines[i][..500] + "…" : lines[i];
                output.AppendLine($"{relative}:{i + 1}:{text}");
                matchCount++;
            }
        }

        if (matchCount == 0)
            return Task.FromResult(new ToolResult("No matches found."));
        if (matchCount >= MaxMatches)
            output.AppendLine($"[truncated at {MaxMatches} matches; narrow the pattern or path]");
        return Task.FromResult(new ToolResult(output.ToString()));
    }
}