using System.Text.Json;
using MyAgent.Core;

namespace MyAgent.Jobs;

public sealed class JobOutputTool(JobRegistry jobs) : ITool
{
    public string Name => "job_output";
    public string Description => "Read a background job's output from a byte cursor. With wait=true, block until it settles or the timeout elapses.";
    public JsonElement InputSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "job_id": { "type": "string" },
            "wait": { "type": "boolean" },
            "timeout_ms": { "type": "integer" }
          },
          "required": ["job_id"],
          "additionalProperties": false
        }
        """).RootElement;
    public string PromptSection =>
        "Track background job ids; do not poll or sleep. You will be notified when a job finishes. " +
        "Use job_output to collect its output, and job_kill when it is no longer needed.";
    public int PromptOrder => 1800;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        var id = Str(args, "job_id") ?? "";
        var wait = args.TryGetProperty("wait", out var w) && w.ValueKind == JsonValueKind.True;
        var timeout = args.TryGetProperty("timeout_ms", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetInt32() : 30_000;
        var entry = jobs.Get(id, context.Session.Id);
        if (entry is null) return ToolResult.Error($"Job '{id}' not found.", "job_not_found");

        if (wait && entry.Status == JobStatus.Running)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Math.Clamp(timeout, 100, 600_000));
            try { await Task.WhenAny(entry.Done.Task, Task.Delay(Timeout.Infinite, cts.Token)); }
            catch (OperationCanceledException) { }
        }

        var (chunks, next, lossy) = jobs.ReadAt(id, 0, context.Session.Id);
        var text = string.Concat(chunks.Where(c => c.Channel != "log").Select(c => c.Text));
        var rendered = JsonSerializer.Serialize(new
        {
            text, job = entry.View(), next, lossy,
        }, Session.JsonOptions);
        return new ToolResult(rendered);
    }

    internal static string? Str(JsonElement o, string n) =>
        o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public sealed class JobListTool(JobRegistry jobs) : ITool
{
    public string Name => "job_list";
    public string Description => "List background jobs visible to this session.";
    public JsonElement InputSchema => JsonDocument.Parse("""{ "type": "object", "properties": {} }""").RootElement;
    public int PromptOrder => 1810;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new ToolResult(JsonSerializer.Serialize(
            jobs.List(context.Session.Id), Session.JsonOptions)));
}

public sealed class JobKillTool(JobRegistry jobs) : ITool
{
    public string Name => "job_kill";
    public string Description => "Request cancellation of a background job.";
    public JsonElement InputSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": { "job_id": { "type": "string" }, "reason": { "type": "string" } },
          "required": ["job_id"],
          "additionalProperties": false
        }
        """).RootElement;
    public int PromptOrder => 1820;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        var id = JobOutputTool.Str(args, "job_id") ?? "";
        var reason = JobOutputTool.Str(args, "reason") ?? "user";
        var outcome = jobs.Kill(id, reason, context.Session.Id);
        return Task.FromResult(new ToolResult(JsonSerializer.Serialize(
            new { outcome = outcome == "requested" ? "cancellation-requested" : outcome }, Session.JsonOptions)));
    }
}
