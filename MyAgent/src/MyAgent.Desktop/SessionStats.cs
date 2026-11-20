using System.Text.Json;
using MyAgent.Core;

namespace MyAgent.Desktop;

/// <summary>
/// Whole-log session statistics folded from durable events (mirrors the harness
/// sessionStats projection + deriveStats fallback): counts and summed wall times
/// survive paging and compaction because the fold reads the log, not the window.
/// </summary>
public sealed record SessionStats(
    int Turns,
    int Steps,
    long LlmMs,
    long ToolMs,
    long TtftMs,
    int TtftSteps,
    long DecodeMs,
    long DecodeTokens)
{
    public static SessionStats Fold(IReadOnlyList<SessionEvent> events)
    {
        var turns = new HashSet<string>(StringComparer.Ordinal);
        var steps = 0;
        long llmMs = 0, toolMs = 0, ttftMs = 0, decodeMs = 0, decodeTokens = 0;
        var ttftSteps = 0;
        DateTimeOffset? stepStart = null;
        var toolCallTimes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        foreach (var evt in events)
        {
            switch (evt.Type)
            {
                case SessionEventTypes.StepStart:
                    stepStart = evt.Time;
                    if (evt.Data.TryGetProperty("turnId", out var tid) && tid.GetString() is { } t) turns.Add(t);
                    break;
                case SessionEventTypes.AssistantMessage:
                    steps += 1;
                    var data = evt.Data.Deserialize<AssistantMessageData>(Session.JsonOptions);
                    // llm wall time: step/start → assistant/message.
                    if (stepStart is { } start)
                        llmMs += Math.Max(0, (long)(evt.Time - start).TotalMilliseconds);
                    // Per-step latency facts ride the message (mirrors assistantStepReading).
                    if (data?.Timing is { } timing)
                    {
                        if (timing.TtftMs is { } ttft)
                        {
                            ttftMs += ttft;
                            ttftSteps += 1;
                        }
                        if (timing.DecodeMs is { } decode && data.Usage is { CompletionTokens: > 0 } usage)
                        {
                            decodeMs += decode;
                            decodeTokens += usage.CompletionTokens;
                        }
                    }
                    break;
                case SessionEventTypes.ToolCall:
                    if (evt.Data.TryGetProperty("toolCallId", out var callId) && callId.GetString() is { } id)
                        toolCallTimes[id] = evt.Time;
                    break;
                case SessionEventTypes.ToolResult:
                    if (evt.Data.TryGetProperty("toolCallId", out var resultId) && resultId.GetString() is { } rid &&
                        toolCallTimes.Remove(rid, out var callTime))
                        toolMs += Math.Max(0, (long)(evt.Time - callTime).TotalMilliseconds);
                    break;
            }
        }
        return new SessionStats(turns.Count, steps, llmMs, toolMs, ttftMs, ttftSteps, decodeMs, decodeTokens);
    }
}

/// <summary>
/// Whole-log token usage folded from assistant messages (mirrors the tokenUsage
/// projection): the three disjoint prompt-side billing buckets plus output.
/// </summary>
public sealed record TokenUsageTotals(
    long UncachedInputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long OutputTokens)
{
    /// <summary>Sum of the three disjoint prompt-side buckets (billedInputTokens).</summary>
    public long BilledInput => UncachedInputTokens + CacheReadTokens + CacheWriteTokens;

    /// <summary>Every prompt bucket plus output (the Turn pill's totalTokens aggregate).</summary>
    public long Total => BilledInput + OutputTokens;

    public static TokenUsageTotals Fold(IReadOnlyList<SessionEvent> events)
    {
        long uncached = 0, read = 0, write = 0, output = 0;
        foreach (var evt in events)
        {
            if (evt.Type != SessionEventTypes.AssistantMessage) continue;
            var data = evt.Data.Deserialize<AssistantMessageData>(Session.JsonOptions);
            if (data?.Usage is not { } usage) continue;
            uncached += usage.UncachedInputTokens;
            read += usage.CacheReadTokens;
            write += usage.CacheWriteTokens;
            output += usage.CompletionTokens;
        }
        return new TokenUsageTotals(uncached, read, write, output);
    }
}

/// <summary>
/// Context pressure folded from the log (mirrors contextPressure): the latest
/// provider-reported prompt total anchors the reading; the newest request header
/// carries the heuristic system/tools/messages breakdown for the meter panel.
/// </summary>
public sealed record ContextPressure(
    long? UsedTokens,
    long ContextWindow,
    long SystemTokens,
    long ToolsTokens,
    long MessageTokens)
{
    public static ContextPressure? Fold(IReadOnlyList<SessionEvent> events, long contextWindow)
    {
        long? used = null;
        long system = 0, tools = 0, messages = 0;
        var haveBreakdown = false;
        for (var i = events.Count - 1; i >= 0; i--)
        {
            var evt = events[i];
            if (used is null && evt.Type == SessionEventTypes.AssistantMessage)
            {
                var data = evt.Data.Deserialize<AssistantMessageData>(Session.JsonOptions);
                if (data?.Usage is { PromptTokens: > 0 } usage)
                    used = usage.PromptTokens;
            }
            else if (!haveBreakdown && evt.Type == SessionEventTypes.RequestHeader &&
                     evt.Data.TryGetProperty("contextBreakdown", out var breakdown))
            {
                system = breakdown.TryGetProperty("systemTokens", out var s) ? s.GetInt64() : 0;
                tools = breakdown.TryGetProperty("toolsTokens", out var t) ? t.GetInt64() : 0;
                messages = breakdown.TryGetProperty("messageTokens", out var m) ? m.GetInt64() : 0;
                haveBreakdown = true;
            }
            if (used is not null && haveBreakdown) break;
        }
        // Before the first provider report, the heuristic estimate carries the meter.
        used ??= haveBreakdown ? system + tools + messages : null;
        if (used is null) return null;
        return new ContextPressure(used, contextWindow, system, tools, messages);
    }
}
