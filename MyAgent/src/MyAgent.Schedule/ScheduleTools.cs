using System.Text.Json;
using System.Text.Json.Serialization;
using MyAgent.Core;

namespace MyAgent.Schedule;

public static class ScheduleJson
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Record(ScheduleRecord r) => JsonSerializer.Serialize(new
    {
        id = r.Id,
        title = r.Title,
        prompt = r.Prompt,
        kind = r.Kind,
        scheduledAt = r.ScheduledAt,
        status = StatusName(r.Status),
        targetSessionId = r.TargetSessionId,
        deliveryCount = r.DeliveryCount,
        expression = r.Expression,
        everySeconds = r.EverySeconds,
        time = r.Time,
        weekdays = r.Weekdays,
    }, Json);

    public static string StatusName(ScheduleStatus s) => s switch
    {
        ScheduleStatus.Active => "active",
        ScheduleStatus.Paused => "paused",
        ScheduleStatus.Ended => "ended",
        _ => "active",
    };

    public static string? GetString(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    public static int? GetInt(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : null;

    public static bool? GetBool(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => (bool?)null,
        };
    }

    public static List<int>? GetInts(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return null;
        return [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32())];
    }
}

public sealed class ScheduleCreateTool(ScheduleStore store) : ITool
{
    public string Name => "schedule_create";
    public string Description =>
        "Create a scheduled task. At each occurrence the prompt is delivered to the target session. " +
        "Choose a timing: after_seconds (one-shot delay), at (ISO-8601 instant), every_seconds (fixed rate), " +
        "time+timeZone (daily), time+timeZone+weekdays (weekly), or expression (5-field cron: minute hour dom month dow). " +
        "After downtime only the latest missed occurrence is delivered.";
    public JsonElement InputSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "title": { "type": "string", "description": "Short task name." },
            "prompt": { "type": "string", "description": "Instruction delivered at each occurrence." },
            "kind": { "type": "string", "enum": ["after", "at", "every", "daily", "weekly", "cron"] },
            "after_seconds": { "type": "integer" },
            "at": { "type": "string", "description": "ISO-8601 UTC instant for kind=at." },
            "every_seconds": { "type": "integer" },
            "time": { "type": "string", "description": "Local HH:mm for daily/weekly." },
            "time_zone": { "type": "string", "description": "IANA or UTC time zone id." },
            "weekdays": { "type": "array", "items": { "type": "integer" }, "description": "ISO weekdays 1=Mon..7=Sun." },
            "expression": { "type": "string", "description": "5-field cron for kind=cron." },
            "target_session_id": { "type": "string", "description": "Session to deliver to; omit for a new session." }
          },
          "required": ["title", "prompt", "kind"],
          "additionalProperties": false
        }
        """).RootElement;
    public string PromptSection =>
        "Use schedule_create for reminders and recurring follow-ups. A one-shot reminder uses kind=after or at; " +
        "recurring work uses every/daily/weekly/cron. Deliveries to this conversation use this session's id.";
    public int PromptOrder => 1600;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var title = ScheduleJson.GetString(args, "title")?.Trim() ?? "";
            var prompt = ScheduleJson.GetString(args, "prompt")?.Trim() ?? "";
            var kind = ScheduleJson.GetString(args, "kind") ?? "after";
            if (title.Length == 0 || title.Length > 120)
                return Task.FromResult(ToolResult.Error("title must be 1-120 characters.", "invalid_prompt"));
            if (prompt.Length == 0)
                return Task.FromResult(ToolResult.Error("prompt must be non-empty.", "invalid_prompt"));

            var now = DateTimeOffset.UtcNow;
            var record = new ScheduleRecord
            {
                Id = $"sch_{Guid.NewGuid():N}",
                Title = title,
                Prompt = prompt,
                Kind = kind,
                TimeZone = ScheduleJson.GetString(args, "time_zone"),
                Time = ScheduleJson.GetString(args, "time"),
                Weekdays = ScheduleJson.GetInts(args, "weekdays"),
                Expression = ScheduleJson.GetString(args, "expression"),
                TargetSessionId = ScheduleJson.GetString(args, "target_session_id") ?? context.Session.Id,
                CreatedAt = now,
                UpdatedAt = now,
            };

            DateTimeOffset? scheduled;
            switch (kind)
            {
                case "after":
                    var secs = ScheduleJson.GetInt(args, "after_seconds") ?? 0;
                    if (secs <= 0) return Task.FromResult(ToolResult.Error("after_seconds must be positive.", "invalid_selector"));
                    record.AfterSeconds = secs;
                    scheduled = now.AddSeconds(secs);
                    break;
                case "at":
                    var atRaw = ScheduleJson.GetString(args, "at");
                    if (!DateTimeOffset.TryParse(atRaw, out var at))
                        return Task.FromResult(ToolResult.Error("at must be an ISO-8601 instant.", "invalid_selector"));
                    scheduled = at;
                    break;
                case "every":
                    var every = ScheduleJson.GetInt(args, "every_seconds") ?? 0;
                    if (every < 60) return Task.FromResult(ToolResult.Error("every_seconds must be >= 60.", "frequency_too_high"));
                    record.EverySeconds = every;
                    scheduled = now.AddSeconds(every);
                    break;
                case "daily":
                case "weekly":
                    if (!TimeSpan.TryParse(record.Time, out _))
                        return Task.FromResult(ToolResult.Error("time must be HH:mm.", "invalid_selector"));
                    if (kind == "weekly" && (record.Weekdays is null || record.Weekdays.Count == 0))
                        return Task.FromResult(ToolResult.Error("weekdays is required for weekly.", "invalid_selector"));
                    scheduled = ScheduleRunner.NextOccurrence(record, now);
                    break;
                case "cron":
                    scheduled = ScheduleRunner.CronNext(record.Expression, now);
                    if (scheduled is null)
                        return Task.FromResult(ToolResult.Error("expression must be a valid 5-field cron.", "invalid_selector"));
                    break;
                default:
                    return Task.FromResult(ToolResult.Error($"Unknown kind '{kind}'.", "invalid_selector"));
            }

            record.ScheduledAt = scheduled ?? now.AddSeconds(60);
            store.Add(record);
            return Task.FromResult(new ToolResult(ScheduleJson.Record(record)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error(ex.Message, "internal_error"));
        }
    }
}

public sealed class ScheduleListTool(ScheduleStore store) : ITool
{
    public string Name => "schedule_list";
    public string Description => "List scheduled tasks. active=true lists only active tasks; false lists paused and ended tasks.";
    public JsonElement InputSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "active": { "type": "boolean" },
            "limit": { "type": "integer" },
            "offset": { "type": "integer" }
          },
          "additionalProperties": false
        }
        """).RootElement;
    public int PromptOrder => 1610;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        var items = store.List(ScheduleJson.GetBool(args, "active"),
            ScheduleJson.GetInt(args, "limit") ?? 100,
            ScheduleJson.GetInt(args, "offset") ?? 0);
        var result = JsonSerializer.Serialize(new
        {
            items = items.Select(r => JsonDocument.Parse(ScheduleJson.Record(r)).RootElement).ToList(),
            shown = items.Count,
        }, Session.JsonOptions);
        return Task.FromResult(new ToolResult(result));
    }
}

public sealed class ScheduleViewTool(ScheduleStore store) : ITool
{
    public string Name => "schedule_view";
    public string Description => "View one scheduled task by id.";
    public JsonElement InputSchema => JsonDocument.Parse("""
        { "type": "object", "properties": { "id": { "type": "string" } }, "required": ["id"], "additionalProperties": false }
        """).RootElement;
    public int PromptOrder => 1620;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        var id = ScheduleJson.GetString(args, "id") ?? "";
        var record = store.Get(id);
        return Task.FromResult(record is null
            ? ToolResult.Error($"Schedule '{id}' not found.", "schedule_not_found")
            : new ToolResult(ScheduleJson.Record(record)));
    }
}

public sealed class ScheduleEditTool(ScheduleStore store) : ITool
{
    public string Name => "schedule_edit";
    public string Description => "Update, pause, resume, or delete a scheduled task by id.";
    public JsonElement InputSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "action": { "type": "string", "enum": ["update", "delete"] },
            "title": { "type": "string" },
            "prompt": { "type": "string" },
            "active": { "type": "boolean" }
          },
          "required": ["id", "action"],
          "additionalProperties": false
        }
        """).RootElement;
    public int PromptOrder => 1630;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        var id = ScheduleJson.GetString(args, "id") ?? "";
        var action = ScheduleJson.GetString(args, "action") ?? "";
        if (action == "delete")
        {
            return Task.FromResult(store.Delete(id)
                ? new ToolResult($"Schedule '{id}' deleted.")
                : ToolResult.Error($"Schedule '{id}' not found.", "schedule_not_found"));
        }
        if (action != "update")
            return Task.FromResult(ToolResult.Error("action must be update or delete.", "invalid_selector"));

        var ok = store.Update(id, r =>
        {
            var title = ScheduleJson.GetString(args, "title");
            var prompt = ScheduleJson.GetString(args, "prompt");
            if (title is not null) r.Title = title;
            if (prompt is not null) r.Prompt = prompt;
            var active = ScheduleJson.GetBool(args, "active");
            if (active is not null)
                r.Status = active.Value ? ScheduleStatus.Active : ScheduleStatus.Paused;
            r.UpdatedAt = DateTimeOffset.UtcNow;
        });
        if (!ok) return Task.FromResult(ToolResult.Error($"Schedule '{id}' not found.", "schedule_not_found"));
        return Task.FromResult(new ToolResult(ScheduleJson.Record(store.Get(id)!)));
    }
}
