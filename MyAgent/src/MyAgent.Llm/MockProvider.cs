using System.Runtime.CompilerServices;
using System.Text.Json;
using MyAgent.Core;

namespace MyAgent.Llm;

public sealed class MockProvider : ILlmProvider
{
    public string Name => "mock";

    public async IAsyncEnumerable<StreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var hasToolResult = request.Messages.LastOrDefault()?.Blocks.Any(b => b is ToolResultBlock) ?? false;
        var lastUserText = request.Messages
            .SelectMany(m => m.Role == "user" ? m.Blocks.OfType<TextBlock>() : [])
            .Select(b => b.Text)
            .LastOrDefault(t => !t.StartsWith('<')) ?? "";
        var toolNames = request.Tools?.Select(t => t.Name).ToHashSet() ?? [];

        await foreach (var chunk in EmitReasoningAsync("分析用户请求，决定下一步行动。", cancellationToken))
            yield return chunk;

        if (!hasToolResult && toolNames.Contains("todo_write") &&
            (lastUserText.Contains("todo", StringComparison.OrdinalIgnoreCase) || lastUserText.Contains("计划")))
        {
            var args = JsonSerializer.Serialize(new
            {
                todos = new[]
                {
                    new { content = "理解需求", status = "completed" },
                    new { content = "执行任务", status = "in_progress" },
                    new { content = "验证结果", status = "pending" },
                },
            });
            yield return new ToolCallDelta(0, "call_mock_todo", "todo_write", args);
            yield return new UsageChunk(120, 40);
            yield return new FinishChunk("tool-calls");
            yield break;
        }

        if (!hasToolResult && toolNames.Contains("bash") &&
            (lastUserText.Contains("运行") || lastUserText.Contains("bash", StringComparison.OrdinalIgnoreCase)))
        {
            var args = JsonSerializer.Serialize(new { description = "Print a greeting", command = "echo hello from dsh" });
            yield return new ToolCallDelta(0, "call_mock_bash", "bash", args);
            yield return new UsageChunk(120, 40);
            yield return new FinishChunk("tool-calls");
            yield break;
        }

        var reply = hasToolResult
            ? "工具已执行完成。以上是本次任务的结果，如需继续请告诉我。"
            : $"收到你的消息：「{lastUserText}」。这是 mock 模型的回复，配置 API Key 后即可连接真实模型。";
        await foreach (var chunk in EmitTextAsync(reply, cancellationToken))
            yield return chunk;
        yield return new UsageChunk(100, 60);
        yield return new FinishChunk("stop");
    }

    private static async IAsyncEnumerable<StreamChunk> EmitReasoningAsync(
        string text, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var piece in Chunk(text, 6))
        {
            await Task.Delay(15, cancellationToken);
            yield return new ReasoningDelta(piece);
        }
    }

    private static async IAsyncEnumerable<StreamChunk> EmitTextAsync(
        string text, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var piece in Chunk(text, 4))
        {
            await Task.Delay(20, cancellationToken);
            yield return new TextDelta(piece);
        }
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }
}