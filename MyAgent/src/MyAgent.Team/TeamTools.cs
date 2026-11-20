using System.Text.Json;
using MyAgent.Core;

namespace MyAgent.Team;

public static class TeamJson
{
    public static string Task(TeamTaskView v) => JsonSerializer.Serialize(new
    {
        id = v.Task.Id,
        revision = v.Task.Revision,
        subject = v.Task.Subject,
        description = v.Task.Description,
        status = StatusName(v.Task.Status),
        owner = v.Task.OwnerName,
        blockedBy = v.Task.BlockedBy,
        writeScopes = v.Task.WriteScopes,
        ready = v.Ready,
        writeScopeWarnings = v.WriteScopeWarnings,
    }, Session.JsonOptions);

    public static string List(IReadOnlyList<TeamTaskView> views) =>
        JsonSerializer.Serialize(new { tasks = views.Select(v => JsonDocument.Parse(Task(v)).RootElement).ToList() }, Session.JsonOptions);

    public static string StatusName(TeamTaskStatus status) => status switch
    {
        TeamTaskStatus.Pending => "pending",
        TeamTaskStatus.InProgress => "in_progress",
        TeamTaskStatus.Completed => "completed",
        TeamTaskStatus.Deleted => "deleted",
        _ => "pending",
    };

    public static TeamTaskStatus? ParseStatus(string? s) => s switch
    {
        "pending" => TeamTaskStatus.Pending,
        "in_progress" => TeamTaskStatus.InProgress,
        "completed" => TeamTaskStatus.Completed,
        _ => null,
    };
}

public sealed class TeamTaskCreateTool(TeamService team) : ITool
{
    public string Name => "team_task_create";
    public string Description => "Create one unowned pending task on the shared Team task board.";
    public JsonElement InputSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "subject": { "type": "string", "description": "Concise task title." },
            "description": { "type": "string", "description": "Complete details and acceptance criteria." },
            "blocked_by": { "type": "array", "items": { "type": "string" }, "description": "Task ids that must complete first." },
            "write_scopes": { "type": "array", "items": { "type": "string" }, "description": "Advisory workspace-relative paths this task expects to modify." }
          },
          "required": ["subject", "description"],
          "additionalProperties": false
        }
        """).RootElement;
    public string PromptSection => TeamService.PolicySection;
    public int PromptOrder => 1400;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var subject = GetString(args, "subject") ?? "";
            var description = GetString(args, "description") ?? "";
            var task = team.Create(context.Session, subject, description,
                GetArray(args, "blocked_by"), GetArray(args, "write_scopes"));
            return Task.FromResult(new ToolResult(TeamJson.Task(new TeamTaskView(task, false, []))));
        }
        catch (HarnessError ex) { return Task.FromResult(ToolResult.Error(ex.Message, ex.Code)); }
    }

    internal static string? GetString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static List<string>? GetArray(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        return [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)];
    }

    internal static int? GetInt(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}

public sealed class TeamTaskListTool(TeamService team) : ITool
{
    public string Name => "team_task_list";
    public string Description => "List shared tasks, including readiness, owner, revision, blockers, and write-scope warnings.";
    public JsonElement InputSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "status": { "type": "string", "enum": ["pending", "in_progress", "completed"] },
            "owner": { "type": "string" },
            "unowned": { "type": "boolean" },
            "ready": { "type": "boolean" },
            "cursor": { "type": "integer" },
            "limit": { "type": "integer" }
          },
          "additionalProperties": false
        }
        """).RootElement;
    public int PromptOrder => 1410;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        var status = TeamJson.ParseStatus(TeamTaskCreateTool.GetString(args, "status"));
        var owner = TeamTaskCreateTool.GetString(args, "owner");
        bool? unowned = args.TryGetProperty("unowned", out var u) && u.ValueKind == JsonValueKind.True ? true
            : args.TryGetProperty("unowned", out var u2) && u2.ValueKind == JsonValueKind.False ? false : null;
        bool? ready = args.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True ? true
            : args.TryGetProperty("ready", out var r2) && r2.ValueKind == JsonValueKind.False ? false : null;
        var cursor = TeamTaskCreateTool.GetInt(args, "cursor") ?? 0;
        var limit = TeamTaskCreateTool.GetInt(args, "limit") ?? 50;
        var views = team.List(context.Session, status, owner, unowned, ready, cursor, limit);
        return Task.FromResult(new ToolResult(TeamJson.List(views)));
    }
}

public sealed class TeamTaskGetTool(TeamService team) : ITool
{
    public string Name => "team_task_get";
    public string Description => "Read the complete latest value of one shared task before changing or executing it.";
    public JsonElement InputSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": { "task_id": { "type": "string" } },
          "required": ["task_id"],
          "additionalProperties": false
        }
        """).RootElement;
    public int PromptOrder => 1420;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        var id = TeamTaskCreateTool.GetString(args, "task_id") ?? "";
        var view = team.Get(context.Session, id);
        return Task.FromResult(view is null
            ? ToolResult.Error($"Task '{id}' not found.", "team_task_not_found")
            : new ToolResult(TeamJson.Task(view)));
    }
}

public sealed class TeamTaskUpdateTool(TeamService team) : ITool
{
    private static readonly HashSet<string> Actions =
        ["claim", "release", "edit", "set_dependencies", "complete", "reopen", "reassign", "delete"];

    public string Name => "team_task_update";
    public string Description =>
        "Compare-and-set a shared task action using the latest revision from team_task_get or team_task_list. " +
        "Actions: claim, release, edit, set_dependencies, complete, reopen, reassign, delete.";
    public JsonElement InputSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "task_id": { "type": "string" },
            "expected_revision": { "type": "integer" },
            "action": { "type": "string", "enum": ["claim", "release", "edit", "set_dependencies", "complete", "reopen", "reassign", "delete"] },
            "subject": { "type": "string" },
            "description": { "type": "string" },
            "blocked_by": { "type": "array", "items": { "type": "string" } },
            "write_scopes": { "type": "array", "items": { "type": "string" } },
            "owner": { "type": "string" }
          },
          "required": ["task_id", "expected_revision", "action"],
          "additionalProperties": false
        }
        """).RootElement;
    public int PromptOrder => 1430;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var id = TeamTaskCreateTool.GetString(args, "task_id") ?? "";
            var revision = TeamTaskCreateTool.GetInt(args, "expected_revision") ?? 0;
            var action = TeamTaskCreateTool.GetString(args, "action") ?? "";
            if (!Actions.Contains(action))
                return Task.FromResult(ToolResult.Error($"Unknown action '{action}'.", "team_invalid_action"));

            var task = team.Update(context.Session, id, revision, action,
                TeamTaskCreateTool.GetString(args, "subject"),
                TeamTaskCreateTool.GetString(args, "description"),
                TeamTaskCreateTool.GetArray(args, "blocked_by"),
                TeamTaskCreateTool.GetArray(args, "write_scopes"),
                TeamTaskCreateTool.GetString(args, "owner"));
            var view = team.Get(context.Session, task.Id)!;
            return Task.FromResult(new ToolResult(TeamJson.Task(view)));
        }
        catch (HarnessError ex) { return Task.FromResult(ToolResult.Error(ex.Message, ex.Code)); }
    }
}
