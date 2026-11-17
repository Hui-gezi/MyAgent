using System.Text;
using System.Text.Json;
using MyAgent.Core;

namespace MyAgent.Fs;

internal static class FsSchema
{
    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    public static string ResolvePath(ToolContext context, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(context.Cwd, path));

    public static string GetString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : throw new HarnessError("invalid_args", $"Missing required string argument '{name}'.");

    public static int GetInt(JsonElement args, string name, int fallback) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : fallback;

    public static bool GetBool(JsonElement args, string name, bool fallback) =>
        args.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
}

public sealed class ReadTool : ITool
{
    private const int MaxLineLength = 2000;
    private const int MaxTotalBytes = 50 * 1024;

    public string Name => "read";
    public string Description =>
        "Read a file from the filesystem. Returns content with line numbers starting at 1. " +
        "Use offset and limit to read a specific line range of a large file. " +
        "file_path may be absolute or relative to the working directory.";
    public JsonElement InputSchema => FsSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "file_path": { "type": "string", "description": "Path of the file to read" },
            "offset": { "type": "integer", "description": "1-based line number to start reading from", "default": 1 },
            "limit": { "type": "integer", "description": "Maximum number of lines to read", "default": 2000 }
          },
          "required": ["file_path"]
        }
        """);
    public string PromptSection => "Use read with offset/limit for large files instead of reading everything at once.";
    public int PromptOrder => 1100;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        var path = FsSchema.ResolvePath(context, FsSchema.GetString(args, "file_path"));
        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Error($"File not found: {path}", "file_not_found"));

        var offset = Math.Max(1, FsSchema.GetInt(args, "offset", 1));
        var limit = Math.Max(1, FsSchema.GetInt(args, "limit", 2000));

        var output = new StringBuilder();
        var lineNumber = 0;
        var emitted = 0;
        var truncated = false;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            if (lineNumber < offset) continue;
            if (emitted >= limit) break;
            var text = line.Length > MaxLineLength ? line[..MaxLineLength] + "… [line truncated]" : line;
            var rendered = $"{lineNumber,6}\t{text}\n";
            if (Encoding.UTF8.GetByteCount(output.ToString()) + Encoding.UTF8.GetByteCount(rendered) > MaxTotalBytes)
            {
                truncated = true;
                break;
            }
            output.Append(rendered);
            emitted++;
        }

        if (emitted == 0 && lineNumber < offset)
            return Task.FromResult(ToolResult.Error($"Offset {offset} is beyond the end of the file ({lineNumber} lines).", "invalid_offset"));

        var result = output.ToString();
        if (truncated) result += "\n[output truncated at 50KB; use offset/limit to read more]";
        else if (lineNumber >= offset + limit && emitted == limit)
            result += $"\n[read {emitted} lines; more content may follow at offset {offset + limit}]";
        return Task.FromResult(new ToolResult(result));
    }
}

public sealed class WriteTool : ITool
{
    public string Name => "write";
    public string Description =>
        "Write a file to the filesystem, creating parent directories as needed. " +
        "Overwrites the file if it already exists. Prefer edit for modifying existing files.";
    public JsonElement InputSchema => FsSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "file_path": { "type": "string", "description": "Path of the file to write" },
            "content": { "type": "string", "description": "Full content to write to the file" }
          },
          "required": ["file_path", "content"]
        }
        """);
    public string PromptSection => "Prefer edit over write when changing part of an existing file.";
    public int PromptOrder => 1110;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        var path = FsSchema.ResolvePath(context, FsSchema.GetString(args, "file_path"));
        var content = FsSchema.GetString(args, "content");
        var existed = File.Exists(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken);
        var lines = content.Length == 0 ? 0 : content.Split('\n').Length;
        return new ToolResult($"{(existed ? "Updated" : "Created")} {path} ({lines} lines, {Encoding.UTF8.GetByteCount(content)} bytes).");
    }
}

public sealed class EditTool : ITool
{
    public string Name => "edit";
    public string Description =>
        "Replace old_string with new_string in a file. Without replace_all, old_string must match exactly once; " +
        "include enough surrounding context to make the match unique. Preserves the rest of the file unchanged.";
    public JsonElement InputSchema => FsSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "file_path": { "type": "string", "description": "Path of the file to edit" },
            "old_string": { "type": "string", "description": "Exact text to replace" },
            "new_string": { "type": "string", "description": "Replacement text" },
            "replace_all": { "type": "boolean", "description": "Replace every occurrence instead of requiring a unique match", "default": false }
          },
          "required": ["file_path", "old_string", "new_string"]
        }
        """);
    public string PromptSection => "edit requires a unique old_string match unless replace_all is set; add surrounding context to disambiguate.";
    public int PromptOrder => 1120;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        var path = FsSchema.ResolvePath(context, FsSchema.GetString(args, "file_path"));
        var oldString = FsSchema.GetString(args, "old_string");
        var newString = FsSchema.GetString(args, "new_string");
        var replaceAll = FsSchema.GetBool(args, "replace_all", false);

        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}", "file_not_found");
        if (oldString.Length == 0)
            return ToolResult.Error("old_string must not be empty.", "invalid_args");
        if (oldString == newString)
            return ToolResult.Error("old_string and new_string are identical; nothing to change.", "invalid_args");

        var content = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        var occurrences = CountOccurrences(content, oldString);
        if (occurrences == 0)
            return ToolResult.Error("old_string was not found in the file. Read the file and copy the exact text.", "no_match");
        if (occurrences > 1 && !replaceAll)
            return ToolResult.Error($"old_string matches {occurrences} locations; add context to make it unique or set replace_all.", "ambiguous_match");

        var updated = replaceAll
            ? content.Replace(oldString, newString, StringComparison.Ordinal)
            : ReplaceFirst(content, oldString, newString);
        await File.WriteAllTextAsync(path, updated, Encoding.UTF8, cancellationToken);
        return new ToolResult($"Edited {path}: replaced {(replaceAll ? occurrences : 1)} occurrence(s).");
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string content, string oldValue, string newValue)
    {
        var index = content.IndexOf(oldValue, StringComparison.Ordinal);
        return string.Concat(content.AsSpan(0, index), newValue, content.AsSpan(index + oldValue.Length));
    }
}