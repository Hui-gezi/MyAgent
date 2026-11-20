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

        // ---- feature tools (goal / plan / team / workflow / schedule) ----
        // Mock mode cannot reason, so map obvious keywords to tool calls for hands-on demos.
        if (!hasToolResult && toolNames.Contains("exit_plan_mode") &&
            (lastUserText.Contains("提交计划") || lastUserText.Contains("审批计划") || lastUserText.Contains("方案提交")))
        {
            var args = JsonSerializer.Serialize(new
            {
                plan = "# 实施计划\n\n1. 分析现状\n2. 分步实现\n3. 验证结果",
            });
            yield return new ToolCallDelta(0, "call_mock_plan", "exit_plan_mode", args);
            yield return new UsageChunk(120, 40);
            yield return new FinishChunk("tool-calls");
            yield break;
        }

        if (!hasToolResult && toolNames.Contains("create_goal") &&
            (lastUserText.Contains("目标", StringComparison.OrdinalIgnoreCase) || lastUserText.Contains("goal", StringComparison.OrdinalIgnoreCase)))
        {
            var args = JsonSerializer.Serialize(new { objective = lastUserText, max_goal_rounds = 3 });
            yield return new ToolCallDelta(0, "call_mock_goal", "create_goal", args);
            yield return new UsageChunk(120, 40);
            yield return new FinishChunk("tool-calls");
            yield break;
        }

        if (!hasToolResult && toolNames.Contains("team_task_create") &&
            (lastUserText.Contains("团队", StringComparison.OrdinalIgnoreCase) || lastUserText.Contains("任务板", StringComparison.OrdinalIgnoreCase) || lastUserText.Contains("team", StringComparison.OrdinalIgnoreCase)))
        {
            var args = JsonSerializer.Serialize(new { subject = "示例任务", description = lastUserText });
            yield return new ToolCallDelta(0, "call_mock_team", "team_task_create", args);
            yield return new UsageChunk(120, 40);
            yield return new FinishChunk("tool-calls");
            yield break;
        }

        if (!hasToolResult && toolNames.Contains("schedule_create") &&
            (lastUserText.Contains("定时", StringComparison.OrdinalIgnoreCase) || lastUserText.Contains("提醒", StringComparison.OrdinalIgnoreCase) || lastUserText.Contains("schedule", StringComparison.OrdinalIgnoreCase)))
        {
            var args = JsonSerializer.Serialize(new
            {
                title = "定时提醒", prompt = lastUserText, kind = "after", after_seconds = 60,
            });
            yield return new ToolCallDelta(0, "call_mock_schedule", "schedule_create", args);
            yield return new UsageChunk(120, 40);
            yield return new FinishChunk("tool-calls");
            yield break;
        }

        if (!hasToolResult && toolNames.Contains("workflow") &&
            (lastUserText.Contains("工作流", StringComparison.OrdinalIgnoreCase) || lastUserText.Contains("编排", StringComparison.OrdinalIgnoreCase) || lastUserText.Contains("workflow", StringComparison.OrdinalIgnoreCase)))
        {
            var args = JsonSerializer.Serialize(new
            {
                meta = new { name = "demo-workflow", description = "演示编排" },
                script = new
                {
                    agents = new[]
                    {
                        new { prompt = "分析任务：" + lastUserText, label = "分析" },
                        new { prompt = "总结分析结果", label = "总结" },
                    },
                },
            });
            yield return new ToolCallDelta(0, "call_mock_workflow", "workflow", args);
            yield return new UsageChunk(120, 40);
            yield return new FinishChunk("tool-calls");
            yield break;
        }

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

        // Second step of the todo demo: close out the list before the final reply,
        // mirroring what a well-behaved model should do (nothing left in_progress).
        var todoResultCount = request.Messages
            .SelectMany(m => m.Blocks.OfType<ToolResultBlock>())
            .Count(b => b.Content.StartsWith("Todo list updated", StringComparison.Ordinal));
        if (hasToolResult && todoResultCount == 1 && toolNames.Contains("todo_write") &&
            (lastUserText.Contains("todo", StringComparison.OrdinalIgnoreCase) || lastUserText.Contains("计划")))
        {
            var args = JsonSerializer.Serialize(new
            {
                todos = new[]
                {
                    new { content = "理解需求", status = "completed" },
                    new { content = "执行任务", status = "completed" },
                    new { content = "验证结果", status = "completed" },
                },
            });
            yield return new ToolCallDelta(0, "call_mock_todo_done", "todo_write", args);
            yield return new UsageChunk(120, 40);
            yield return new FinishChunk("tool-calls");
            yield break;
        }

        // Sandbox escalation demo: request a strictly wider mode with a justification so the
        // approval panel appears (mirrors the model's sanctioned one-shot retry choreography).
        if (!hasToolResult && toolNames.Contains("bash") &&
            (lastUserText.Contains("沙箱") || lastUserText.Contains("权限升级") ||
             lastUserText.Contains("sandbox", StringComparison.OrdinalIgnoreCase)))
        {
            var args = JsonSerializer.Serialize(new
            {
                description = "Sandbox escalation demo: write a sample file outside the workspace",
                command = "echo myagent-sandbox-demo > \"%USERPROFILE%\\myagent-sandbox-demo.txt\"",
                sandbox_permissions = "danger-full-access",
                justification = "演示沙箱升级：需要在工作区之外写入一个示例文件，仅本次操作。",
            });
            yield return new ToolCallDelta(0, "call_mock_escalation", "bash", args);
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
        // Demo cache buckets: 40 of the 100 prompt tokens served from cache → 40% hit.
        yield return new UsageChunk(100, 60, 40, 0);
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