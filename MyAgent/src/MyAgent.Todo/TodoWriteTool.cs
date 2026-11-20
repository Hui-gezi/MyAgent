using System.Text.Json;
using MyAgent.Core;

namespace MyAgent.Todo;

public sealed class TodoWriteTool : ITool
{
    private static readonly HashSet<string> ValidStatuses = ["pending", "in_progress", "completed"];

    public string Name => "todo_write";
    public string Description =>
        "Create or update the session task list. Pass the complete list every time; it replaces the previous list. " +
        "Each item has content and status (pending, in_progress, completed). Keep exactly one item in_progress while working. " +
        "Mark items completed immediately after finishing them; never leave an item in_progress once its work is done. " +
        "Before your final reply, call todo_write once more so every finished item is completed and nothing is left in_progress.";
    public JsonElement InputSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "todos": {
              "type": "array",
              "description": "The complete updated task list",
              "items": {
                "type": "object",
                "properties": {
                  "content": { "type": "string", "description": "Task description" },
                  "status": { "type": "string", "enum": ["pending", "in_progress", "completed"] }
                },
                "required": ["content", "status"]
              }
            }
          },
          "required": ["todos"]
        }
        """).RootElement;
    public string PromptSection => "Use todo_write to track multi-step work; update statuses as you progress.";
    public int PromptOrder => 1200;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        if (!args.TryGetProperty("todos", out var todosEl) || todosEl.ValueKind != JsonValueKind.Array)
            return Task.FromResult(ToolResult.Error("todos must be an array.", "invalid_args"));

        var todos = new List<TodoItem>();
        foreach (var item in todosEl.EnumerateArray())
        {
            var content = item.TryGetProperty("content", out var c) ? c.GetString() : null;
            var status = item.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (string.IsNullOrWhiteSpace(content) || status is null || !ValidStatuses.Contains(status))
                return Task.FromResult(ToolResult.Error("Each todo needs content and a valid status (pending, in_progress, completed).", "invalid_args"));
            todos.Add(new TodoItem(content, status));
        }

        context.Session.Append(SessionEventTypes.TodoUpdate, new { todos });
        var completed = todos.Count(t => t.Status == "completed");
        return Task.FromResult(new ToolResult($"Todo list updated: {todos.Count} items ({completed} completed)."));
    }
}