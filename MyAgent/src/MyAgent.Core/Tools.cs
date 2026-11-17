using System.Text.Json;

namespace MyAgent.Core;

public sealed record ToolResult(string Content, bool IsError = false, string? ErrorCode = null)
{
    public static ToolResult Error(string message, string code = "tool_error") =>
        new($"Error: {message}", true, code);
}

public sealed class ToolContext(Session session, string cwd)
{
    public Session Session { get; } = session;
    public string Cwd { get; } = cwd;
}

public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement InputSchema { get; }
    bool Exclusive => false;
    string? PromptSection => null;
    int PromptOrder => 0;
    Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken);
}

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);

    public IReadOnlyList<ITool> Tools => [.. _tools.Values];

    public void Register(ITool tool) => _tools[tool.Name] = tool;

    public ITool? Get(string name) => _tools.GetValueOrDefault(name);
}

public static class ToolBatch
{
    public const int DefaultMaxParallelToolCalls = 10;

    public static async Task ExecuteAsync(
        IReadOnlyList<ToolUseBlock> calls,
        ToolRegistry registry,
        ToolContext context,
        int maxParallelToolCalls,
        Action<ToolUseBlock, ToolResult> commit,
        CancellationToken cancellationToken)
    {
        var index = 0;
        while (index < calls.Count)
        {
            if (registry.Get(calls[index].Name)?.Exclusive == true)
            {
                var call = calls[index];
                var result = await RunOneAsync(call, registry, context, cancellationToken);
                commit(call, result);
                index++;
                continue;
            }

            var end = index;
            while (end < calls.Count && registry.Get(calls[end].Name)?.Exclusive != true) end++;
            var segment = calls.Skip(index).Take(end - index).ToList();

            var results = new ToolResult[segment.Count];
            using var gate = new SemaphoreSlim(Math.Max(1, maxParallelToolCalls));
            var tasks = segment.Select(async (call, i) =>
            {
                await gate.WaitAsync(cancellationToken);
                try { results[i] = await RunOneAsync(call, registry, context, cancellationToken); }
                finally { gate.Release(); }
            }).ToArray();
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                for (var i = 0; i < segment.Count; i++)
                    results[i] ??= ToolResult.Error("Tool call cancelled before completion.", "cancelled");
            }
            for (var i = 0; i < segment.Count; i++)
                commit(segment[i], results[i] ?? ToolResult.Error("Tool call cancelled before completion.", "cancelled"));
            index = end;
        }
    }

    private static async Task<ToolResult> RunOneAsync(
        ToolUseBlock call, ToolRegistry registry, ToolContext context, CancellationToken cancellationToken)
    {
        var tool = registry.Get(call.Name);
        if (tool is null)
            return ToolResult.Error($"Unknown tool '{call.Name}'.", "unknown_tool");
        try
        {
            return await tool.ExecuteAsync(call.Input, context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("Tool call cancelled.", "cancelled");
        }
        catch (HarnessError ex)
        {
            return ToolResult.Error(ex.Message, ex.Code);
        }
        catch (Exception ex)
        {
            return ToolResult.Error(ex.Message, "tool_exception");
        }
    }
}