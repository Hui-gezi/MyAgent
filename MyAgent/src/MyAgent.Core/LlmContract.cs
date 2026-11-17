using System.Text.Json;

namespace MyAgent.Core;

public sealed record ToolSpec(string Name, string Description, JsonElement InputSchema);

public sealed class LlmRequest
{
    public required string Model { get; init; }
    public string? System { get; init; }
    public required IReadOnlyList<LlmMessage> Messages { get; init; }
    public IReadOnlyList<ToolSpec>? Tools { get; init; }
    public int MaxTokens { get; init; } = 256_000;
    public bool ThinkingEnabled { get; init; } = true;
}

public abstract record StreamChunk;

public sealed record TextDelta(string Text) : StreamChunk;

public sealed record ReasoningDelta(string Text) : StreamChunk;

public sealed record SignatureDelta(string Signature) : StreamChunk;

public sealed record ToolCallDelta(int Index, string? Id, string? Name, string? PartialJson) : StreamChunk;

public sealed record UsageChunk(int PromptTokens, int CompletionTokens) : StreamChunk;

public sealed record FinishChunk(string StopReason) : StreamChunk;

public sealed record AssistantOutcome(List<ContentBlock> Blocks, string StopReason, TokenUsageInfo? Usage);

public interface ILlmProvider
{
    string Name { get; }
    IAsyncEnumerable<StreamChunk> StreamAsync(LlmRequest request, CancellationToken cancellationToken = default);
}

public static class LlmAccumulator
{
    public static async Task<AssistantOutcome> CollectAsync(
        IAsyncEnumerable<StreamChunk> chunks,
        Action<StreamChunk>? onChunk = null,
        CancellationToken cancellationToken = default)
    {
        var blocks = new List<ContentBlock>();
        var text = new System.Text.StringBuilder();
        var thinking = new System.Text.StringBuilder();
        string? thinkingSignature = null;
        var toolCalls = new SortedDictionary<int, (string? Id, string? Name, System.Text.StringBuilder Json)>();
        var stopReason = "stop";
        int promptTokens = 0, completionTokens = 0;

        void FlushText()
        {
            if (text.Length > 0)
            {
                blocks.Add(new TextBlock(text.ToString()));
                text.Clear();
            }
        }

        void FlushThinking()
        {
            if (thinking.Length > 0)
            {
                blocks.Add(new ThinkingBlock(thinking.ToString(), thinkingSignature));
                thinking.Clear();
                thinkingSignature = null;
            }
        }

        await foreach (var chunk in chunks.WithCancellation(cancellationToken))
        {
            onChunk?.Invoke(chunk);
            switch (chunk)
            {
                case TextDelta t:
                    FlushThinking();
                    text.Append(t.Text);
                    break;
                case ReasoningDelta r:
                    FlushText();
                    thinking.Append(r.Text);
                    break;
                case SignatureDelta s:
                    thinkingSignature = s.Signature;
                    break;
                case ToolCallDelta tc:
                    FlushText();
                    FlushThinking();
                    if (!toolCalls.TryGetValue(tc.Index, out var call))
                    {
                        call = (null, null, new System.Text.StringBuilder());
                        toolCalls[tc.Index] = call;
                    }
                    var updated = call with
                    {
                        Id = tc.Id ?? call.Id,
                        Name = tc.Name ?? call.Name,
                    };
                    if (tc.PartialJson is not null) updated.Json.Append(tc.PartialJson);
                    toolCalls[tc.Index] = updated;
                    break;
                case UsageChunk u:
                    promptTokens = u.PromptTokens;
                    completionTokens = u.CompletionTokens;
                    break;
                case FinishChunk f:
                    stopReason = f.StopReason;
                    break;
            }
        }

        FlushText();
        FlushThinking();
        foreach (var (id, name, json) in toolCalls.Values)
        {
            JsonElement input;
            var raw = json.ToString();
            try
            {
                input = string.IsNullOrWhiteSpace(raw)
                    ? JsonDocument.Parse("{}").RootElement
                    : JsonDocument.Parse(raw).RootElement;
            }
            catch (JsonException)
            {
                input = JsonDocument.Parse("{}").RootElement;
            }
            blocks.Add(new ToolUseBlock(id ?? $"call_{Guid.NewGuid():N}", name ?? "", input));
        }

        var usage = promptTokens + completionTokens > 0
            ? new TokenUsageInfo(promptTokens, completionTokens, promptTokens + completionTokens)
            : null;
        return new AssistantOutcome(blocks, stopReason, usage);
    }
}