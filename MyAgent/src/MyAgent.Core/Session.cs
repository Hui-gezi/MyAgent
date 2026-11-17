using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyAgent.Core;

public static class SessionEventTypes
{
    public const string TurnStart = "turn/start";
    public const string TurnEnd = "turn/end";
    public const string StepStart = "step/start";
    public const string StepEnd = "step/end";
    public const string UserMessage = "user/message";
    public const string SystemMessage = "system/message";
    public const string AssistantMessage = "assistant/message";
    public const string AssistantAttempt = "assistant/attempt";
    public const string ToolCall = "tool/call";
    public const string ToolResult = "tool/result";
    public const string RequestHeader = "request/header";
    public const string RequestContext = "request/context";
    public const string Compaction = "session/compaction";
    public const string Rename = "session/rename";
    public const string TodoUpdate = "todo/update";
    public const string SessionEndSeed = "session/end-seed";
}

public sealed class SessionEvent
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("seq")]
    public long Seq { get; init; }

    [JsonPropertyName("time")]
    public DateTimeOffset Time { get; init; }

    [JsonPropertyName("data")]
    public JsonElement Data { get; init; }

    [JsonPropertyName("ignorable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Ignorable { get; init; }
}

public sealed record TokenUsageInfo(int PromptTokens, int CompletionTokens, int TotalTokens);

public sealed record AssistantMessageData(
    List<ContentBlock> Blocks,
    string StopReason,
    TokenUsageInfo? Usage);

public sealed record ToolResultData(
    string ToolCallId,
    string Content,
    bool IsError,
    string? ErrorCode = null);

public sealed record CompactionData(string Summary, long ReplacesThroughSeq);

public sealed record TodoItem(string Content, string Status);

public sealed class Session
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new ContentBlockConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly List<SessionEvent> _events = [];
    private long _seq;

    public required string Id { get; init; }
    public required string Cwd { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? Title { get; set; }

    public IReadOnlyList<SessionEvent> Events => _events;

    public event Action<SessionEvent>? EventAppended;

    public SessionEvent Append(string type, object data, bool ignorable = false)
    {
        var evt = new SessionEvent
        {
            Type = type,
            Seq = Interlocked.Increment(ref _seq),
            Time = DateTimeOffset.UtcNow,
            Data = JsonSerializer.SerializeToElement(data, JsonOptions),
            Ignorable = ignorable,
        };
        lock (_events) _events.Add(evt);
        if (type == SessionEventTypes.Rename && evt.Data.TryGetProperty("title", out var renamed))
            Title = renamed.GetString();
        if (type == SessionEventTypes.UserMessage && Title is null &&
            evt.Data.TryGetProperty("text", out var text))
        {
            var first = text.GetString() ?? "";
            Title = first.Length > 60 ? first[..60] + "…" : first;
        }
        EventAppended?.Invoke(evt);
        return evt;
    }

    public void Restore(IEnumerable<SessionEvent> events)
    {
        lock (_events)
        {
            foreach (var evt in events)
            {
                _events.Add(evt);
                _seq = Math.Max(_seq, evt.Seq);
                if (evt.Type == SessionEventTypes.Rename && evt.Data.TryGetProperty("title", out var renamed))
                    Title = renamed.GetString();
                if (evt.Type == SessionEventTypes.UserMessage && Title is null &&
                    evt.Data.TryGetProperty("text", out var text))
                {
                    var first = text.GetString() ?? "";
                    Title = first.Length > 60 ? first[..60] + "…" : first;
                }
            }
        }
    }

    public (string? System, List<LlmMessage> Messages) DeriveMessages()
    {
        string? system = null;
        var messages = new List<LlmMessage>();
        string? runtimeContext = null;
        long compactedThrough = -1;
        string? compactionSummary = null;

        List<SessionEvent> snapshot;
        lock (_events) snapshot = [.. _events];

        foreach (var evt in snapshot)
        {
            switch (evt.Type)
            {
                case SessionEventTypes.SystemMessage:
                    system = evt.Data.GetProperty("text").GetString();
                    break;
                case SessionEventTypes.RequestContext:
                    runtimeContext = evt.Data.GetProperty("text").GetString();
                    break;
                case SessionEventTypes.Compaction:
                    var compaction = evt.Data.Deserialize<CompactionData>(JsonOptions)!;
                    compactedThrough = compaction.ReplacesThroughSeq;
                    compactionSummary = compaction.Summary;
                    break;
                case SessionEventTypes.UserMessage:
                    if (evt.Seq <= compactedThrough) break;
                    messages.Add(LlmMessage.User(evt.Data.GetProperty("text").GetString() ?? ""));
                    break;
                case SessionEventTypes.AssistantMessage:
                    if (evt.Seq <= compactedThrough) break;
                    var assistant = evt.Data.Deserialize<AssistantMessageData>(JsonOptions)!;
                    messages.Add(LlmMessage.Assistant(assistant.Blocks));
                    break;
                case SessionEventTypes.ToolResult:
                    if (evt.Seq <= compactedThrough) break;
                    var result = evt.Data.Deserialize<ToolResultData>(JsonOptions)!;
                    messages.Add(new LlmMessage
                    {
                        Role = "user",
                        Blocks = [new ToolResultBlock(result.ToolCallId, result.Content, result.IsError)],
                    });
                    break;
            }
        }

        if (compactionSummary is not null)
            messages.Insert(0, LlmMessage.User($"<compacted-summary>\n{compactionSummary}\n</compacted-summary>"));
        if (runtimeContext is not null)
            messages.Add(LlmMessage.User(runtimeContext));

        return (system, MergeAdjacent(messages));
    }

    private static List<LlmMessage> MergeAdjacent(List<LlmMessage> messages)
    {
        var merged = new List<LlmMessage>();
        foreach (var message in messages)
        {
            if (message.Blocks.Count == 0) continue;
            if (merged.Count > 0 && merged[^1].Role == message.Role)
                merged[^1].Blocks.AddRange(message.Blocks);
            else
                merged.Add(new LlmMessage { Role = message.Role, Blocks = [.. message.Blocks] });
        }
        return merged;
    }
}