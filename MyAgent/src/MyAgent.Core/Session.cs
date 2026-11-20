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

    // ---- goal ----
    public const string GoalChange = "goal/change";
    // ---- plan ----
    public const string PlanMode = "plan/mode";
    // ---- team ----
    public const string TeamMode = "team/mode";
    public const string TeamTask = "team/task";
    // ---- workflow ----
    public const string WorkflowStart = "workflow/start";
    public const string WorkflowPhase = "workflow/phase";
    public const string WorkflowAgentStart = "workflow/agent-start";
    public const string WorkflowAgentEnd = "workflow/agent-end";
    public const string WorkflowLog = "workflow/log";
    public const string WorkflowEnd = "workflow/end";
    // ---- schedule (historical session-local reminder log) ----
    public const string ScheduleChange = "schedule/change";
    // ---- subagents ----
    public const string SubagentCatalog = "subagent/catalog";
    public const string SubagentDescriptor = "subagent/descriptor";
    public const string SubagentStart = "subagent/start";
    public const string SubagentEnd = "subagent/end";
    // ---- jobs ----
    public const string JobRegistered = "job/registered";
    public const string JobSettled = "job/settled";
    // ---- review / questions ----
    public const string PlanSubmitted = "plan/submitted";
    // ---- approval (log-only audit; mirrors dsh-user-approval) ----
    public const string ApprovalAsked = "approval/asked";
    public const string ApprovalDecided = "approval/decided";
    public const string ApprovalPolicy = "approval/policy";
    // ---- sandbox (log-only policy override; mirrors dsh-sandbox-policy) ----
    public const string SandboxMode = "sandbox/mode";
    // ---- permission presets (log-only user intent; mirrors dsh-permission-presets) ----
    public const string PermissionPreset = "permission/preset";
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

public sealed record TokenUsageInfo(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    int CacheReadTokens = 0,
    int CacheWriteTokens = 0)
{
    /// <summary>The three disjoint prompt-side billing buckets (mirrors token-meter):
    /// PromptTokens is the aggregate, so uncached input is the remainder.</summary>
    public int UncachedInputTokens => Math.Max(0, PromptTokens - CacheReadTokens - CacheWriteTokens);
}

/// <summary>
/// Per-step latency facts persisted with the assistant message (mirrors the harness
/// assistant-node timing): step/start → first token delta, and first delta → final message.
/// Null parts mark an unrecorded reading (e.g. an empty stream).
/// </summary>
public sealed record AssistantTimingInfo(int? TtftMs, int? DecodeMs);

public sealed record AssistantMessageData(
    List<ContentBlock> Blocks,
    string StopReason,
    TokenUsageInfo? Usage,
    AssistantTimingInfo? Timing = null);

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

    /// <summary>
    /// System-generated notice sources (mirrors harness non-user message sources:
    /// plan-mode narration, approval-policy notice, goal continuation prompts).
    /// They MUST stay in the log — the model reads them via DeriveMessages — but the
    /// transcript renders only user-authored bubbles, and they never name a session.
    /// </summary>
    public static bool IsNoticeSource(string? source) =>
        source is "notice" or "user-approval" or "goal-round" or "tool-goal" or "feature";

    /// <summary>True for a user/message event carrying a system notice (hidden from the transcript).</summary>
    public static bool IsNoticeMessage(SessionEvent evt) =>
        evt.Type == SessionEventTypes.UserMessage &&
        evt.Data.TryGetProperty("source", out var src) && IsNoticeSource(src.GetString());

    public required string Id { get; init; }
    public required string Cwd { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? Title { get; set; }

    // ---- subagent identity (mirrors dsh-session header fields) ----
    public string? ParentSessionId { get; init; }
    public string? Origin { get; init; }
    public int DelegationDepth { get; init; }
    public int InheritedEventCount { get; init; }

    /// <summary>
    /// A stable snapshot of the durable log. NEVER expose the live list: the agent thread
    /// appends while UI/feature threads enumerate, and List&lt;T&gt; enumeration throws
    /// "Collection was modified" the moment an append lands mid-loop — the random
    /// "application error" crash dialog during long runs. Copying under the same lock
    /// Append uses makes every reader race-free at the cost of one array copy per read.
    /// </summary>
    public IReadOnlyList<SessionEvent> Events
    {
        get { lock (_events) return [.. _events]; }
    }

    /// <summary>Number of committed events without copying the log.</summary>
    public int EventCount
    {
        get { lock (_events) return _events.Count; }
    }

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
        if (type == SessionEventTypes.UserMessage && Title is null && !IsNoticeMessage(evt) &&
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
                if (evt.Type == SessionEventTypes.UserMessage && Title is null && !IsNoticeMessage(evt) &&
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

        // Pre-scan for the LATEST checkpoint: the fold below walks forward, so without this
        // every message older than the compaction event would already be added before the
        // boundary is known — compaction would prepend a summary but never drop history.
        foreach (var evt in snapshot)
        {
            if (evt.Type != SessionEventTypes.Compaction) continue;
            var compaction = evt.Data.Deserialize<CompactionData>(JsonOptions)!;
            compactedThrough = compaction.ReplacesThroughSeq;
            compactionSummary = compaction.Summary;
        }

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