using System.Text.Json;
using MyAgent.Core;

namespace MyAgent.Subagents;

public sealed class SpawnSubagentTool(SubagentRuntime runtime, string provider = "spawn", string? toolName = null) : ITool
{
    public string Name => toolName ?? (provider == "fork" ? "subagent_fork" : "subagent");
    public string Description => provider == "fork"
        ? "Fork a continuable subagent seeded with this session's history up to the last completed turn. " +
          "It starts with a fresh independent turn but remembers prior context."
        : "Start a fresh continuable subagent on an independent task. Returns immediately with its name; " +
          "use send_message / wait_agent / list_agents to coordinate. Results arrive as a notice.";
    public string PromptSection =>
        "Use subagent / subagent_fork to delegate independent work. Use list_agents to see the roster, " +
        "send_message to relay messages, wait_agent to wait for pending input, and interrupt_agent to stop one.";
    public int PromptOrder => 1700;

    public JsonElement InputSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "description": { "type": "string", "description": "3-5 word task label." },
            "prompt": { "type": "string", "description": "The task for the subagent." },
            "name": { "type": "string", "description": "Optional lowercase kebab-case member name." }
          },
          "required": ["description", "prompt"],
          "additionalProperties": false
        }
        """).RootElement;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var description = Str(args, "description") ?? "";
            var prompt = Str(args, "prompt") ?? "";
            var name = Str(args, "name");
            var act = runtime.Start(context.Session, provider, description, prompt, name);
            return Task.FromResult(new ToolResult(JsonSerializer.Serialize(
                new { kind = "activation", name = act.Name }, Session.JsonOptions)));
        }
        catch (HarnessError ex) { return Task.FromResult(ToolResult.Error(ex.Message, ex.Code)); }
    }

    internal static string? Str(JsonElement o, string n) =>
        o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public sealed class ListAgentsTool(SubagentRuntime runtime) : ITool
{
    public string Name => "list_agents";
    public string Description => "List the root agent and all continuable teammates with their status.";
    public JsonElement InputSchema => JsonDocument.Parse("""{ "type": "object", "properties": {} }""").RootElement;
    public int PromptOrder => 1710;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        var entries = new List<object>
        {
            new { name = "lead", status = "root", description = "root agent" },
        };
        foreach (var child in runtime.ListDescendants(context.Session))
        {
            var act = runtime.Find(child.ChildId);
            entries.Add(new
            {
                name = child.Name,
                status = act?.Status ?? "inactive",
                description = child.Label,
            });
        }
        return Task.FromResult(new ToolResult(JsonSerializer.Serialize(entries, Session.JsonOptions)));
    }
}

public sealed class SendMessageTool(SubagentRuntime runtime) : ITool
{
    public string Name => "send_message";
    public string Description => "Send a message to a teammate by name. Returns only an acknowledgement; a reply is a separate message.";
    public JsonElement InputSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "agent": { "type": "string", "description": "Target agent name, path, or session id." },
            "message": { "type": "string" }
          },
          "required": ["agent", "message"],
          "additionalProperties": false
        }
        """).RootElement;
    public int PromptOrder => 1720;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var agent = SpawnSubagentTool.Str(args, "agent") ?? "";
            var message = SpawnSubagentTool.Str(args, "message") ?? "";
            runtime.SendMessage(context.Session, agent, message);
            return Task.FromResult(new ToolResult(JsonSerializer.Serialize(
                new { messageId = $"m_{Guid.NewGuid():N}" }, Session.JsonOptions)));
        }
        catch (HarnessError ex) { return Task.FromResult(ToolResult.Error(ex.Message, ex.Code)); }
    }
}

public sealed class InterruptAgentTool(SubagentRuntime runtime) : ITool
{
    public string Name => "interrupt_agent";
    public string Description => "Interrupt a descendant agent's current turn. Returns only an acknowledgement.";
    public JsonElement InputSchema => JsonDocument.Parse("""
        { "type": "object", "properties": { "agent": { "type": "string" } }, "required": ["agent"], "additionalProperties": false }
        """).RootElement;
    public int PromptOrder => 1730;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        var agent = SpawnSubagentTool.Str(args, "agent") ?? "";
        var accepted = runtime.Interrupt(context.Session, agent);
        return Task.FromResult(new ToolResult(JsonSerializer.Serialize(
            new { accepted }, Session.JsonOptions)));
    }
}

public sealed class WaitAgentTool(SubagentRuntime runtime, Func<string, Agent?> resolveAgent) : ITool
{
    public string Name => "wait_agent";
    public string Description => "Wait for pending input to the calling agent's own next step. Does not wait for subagent results.";
    public JsonElement InputSchema => JsonDocument.Parse("""
        { "type": "object", "properties": { "timeout_ms": { "type": "integer" } }, "additionalProperties": false }
        """).RootElement;
    public int PromptOrder => 1740;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
    {
        var timeout = args.TryGetProperty("timeout_ms", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetInt32() : 30_000;
        var agent = resolveAgent(context.Session.Id);
        var timedOut = agent is null || !await runtime.WaitForStepAsync(agent, timeout, cancellationToken);
        return new ToolResult(JsonSerializer.Serialize(new { timedOut }, Session.JsonOptions));
    }
}
