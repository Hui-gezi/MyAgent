using System.Text;
using System.Text.Json;
using MyAgent.Core;
using MyAgent.Llm;

namespace MyAgent.Compaction;

public sealed class BasicCompactor(ILlmProvider provider, string model, int contextWindow, int maxTokens)
{
    private const int Headroom = 65_536;
    private const double TriggerRatio = 0.8;
    private const double TailRatio = 0.16;

    /// <summary>The summarization request itself must stay far below the window: cap the transcript.</summary>
    private const int TranscriptCharBudget = 200_000; // ≈ 50–100k tokens depending on language
    private const int HeadBudget = TranscriptCharBudget / 4;
    private const int TailBudget = TranscriptCharBudget - HeadBudget;

    private long _failedThroughSeq = -1; // skip re-summarizing the exact range that just failed

    /// <summary>Raised when a compaction attempt fails; the turn continues uncompacted.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Raised right before the summarization request starts (UI: "正在压缩上下文…").</summary>
    public event Action? Started;

    /// <summary>Raised after a compaction attempt settles, successfully or not.</summary>
    public event Action? Finished;

    // Reserve room for output + headroom, but never more than half the window —
    // otherwise small windows (e.g. 128k) would yield a zero/negative threshold
    // and compact on every single step.
    public int Threshold
    {
        get
        {
            var reserve = Math.Min((long)maxTokens + Headroom, contextWindow / 2);
            return (int)Math.Min(TriggerRatio * contextWindow, contextWindow - reserve);
        }
    }

    public Task MaybeCompactAsync(Session session, CancellationToken cancellationToken) =>
        CompactCoreAsync(session, force: false, cancellationToken);

    /// <summary>
    /// Overflow recovery: the provider rejected the request as too long. Compact regardless
    /// of the local estimate (which can badly undercount CJK text), preferring the newest
    /// turn boundary so tool_use/tool_result pairs are never split.
    /// </summary>
    public Task<bool> ForceCompactAsync(Session session, CancellationToken cancellationToken) =>
        CompactCoreAsync(session, force: true, cancellationToken);

    private async Task<bool> CompactCoreAsync(Session session, bool force, CancellationToken cancellationToken)
    {
        var events = session.Events; // one stable snapshot for the whole decision
        var compactedThrough = LastCompactedThrough(events);

        if (!force)
        {
            var (_, messages) = session.DeriveMessages();
            if (EstimateTokens(messages) < Threshold) return false;
        }

        var tailBudget = (int)(Threshold * TailRatio);
        var boundarySeq = FindBoundarySeq(events, compactedThrough, tailBudget);
        if (boundarySeq is null && force)
        {
            // The tail alone outgrew the budget: fall back to the newest completed turn so
            // the checkpoint at least drops the older history instead of giving up entirely.
            boundarySeq = events.LastOrDefault(e =>
                e.Type == SessionEventTypes.TurnEnd && e.Seq > compactedThrough)?.Seq;
        }
        if (boundarySeq is null || boundarySeq <= compactedThrough) return false;
        if (!force && boundarySeq <= _failedThroughSeq) return false; // same failing range: don't retry every step

        var transcript = RenderTranscript(events, compactedThrough, boundarySeq.Value);
        Started?.Invoke();
        try
        {
            var summary = await SummarizeAsync(transcript, cancellationToken);
            if (string.IsNullOrWhiteSpace(summary)) return false;
            session.Append(SessionEventTypes.Compaction, new CompactionData(summary, boundarySeq.Value));
            _failedThroughSeq = -1;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw; // user cancelled the turn — not a compaction failure
        }
        catch (Exception ex)
        {
            _failedThroughSeq = boundarySeq.Value;
            Failed?.Invoke(ex);
            return false;
        }
        finally
        {
            Finished?.Invoke();
        }
    }

    private static long LastCompactedThrough(IReadOnlyList<SessionEvent> events)
    {
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i].Type != SessionEventTypes.Compaction) continue;
            return events[i].Data.Deserialize<CompactionData>(Session.JsonOptions)!.ReplacesThroughSeq;
        }
        return -1;
    }

    /// <summary>
    /// Token heuristic weighted for CJK: ASCII ≈ 4 chars/token, everything else ≈ 1 token/char.
    /// The old chars/4 estimate undercounted Chinese transcripts 3–4×, so compaction fired
    /// only AFTER the provider had already rejected the request.
    /// </summary>
    public static int EstimateTextTokens(string text)
    {
        int ascii = 0, other = 0;
        foreach (var ch in text)
        {
            if (ch < 128) ascii++;
            else other++;
        }
        return ascii / 4 + other;
    }

    private static int EstimateTokens(IEnumerable<LlmMessage> messages) =>
        messages.Sum(m => m.Blocks.Sum(b => b switch
        {
            TextBlock t => EstimateTextTokens(t.Text),
            ThinkingBlock th => EstimateTextTokens(th.Text),
            ToolUseBlock tu => EstimateTextTokens(tu.Input.GetRawText()) + tu.Name.Length,
            ToolResultBlock tr => EstimateTextTokens(tr.Content),
            _ => 0,
        }));

    private static long? FindBoundarySeq(IReadOnlyList<SessionEvent> events, long compactedThrough, int tailBudget)
    {
        // Prefer turn ends NEWER than the last checkpoint; the tail must fit the budget.
        var tailTokens = 0L;
        long? candidate = null;
        for (var i = events.Count - 1; i >= 0; i--)
        {
            var evt = events[i];
            if (evt.Seq <= compactedThrough) break;
            tailTokens += EstimateTextTokens(evt.Data.GetRawText());
            if (tailTokens > tailBudget) break;
            if (evt.Type == SessionEventTypes.TurnEnd &&
                events.Any(e => e.Seq > compactedThrough && e.Seq < evt.Seq &&
                                e.Type is SessionEventTypes.UserMessage or SessionEventTypes.AssistantMessage))
                candidate = evt.Seq; // newest fitting boundary wins (loop walks backwards; keep first hit)
            if (candidate is not null) break;
        }
        return candidate;
    }

    /// <summary>
    /// Render ONLY the not-yet-compacted range, bounded to a fixed char budget (head + tail).
    /// The previous checkpoint summary leads the transcript so successive checkpoints chain.
    /// </summary>
    private static string RenderTranscript(IReadOnlyList<SessionEvent> events, long compactedThrough, long throughSeq)
    {
        string? previousSummary = null;
        var lines = new List<string>();
        foreach (var evt in events)
        {
            if (evt.Type == SessionEventTypes.Compaction && evt.Seq <= throughSeq)
            {
                previousSummary = evt.Data.Deserialize<CompactionData>(Session.JsonOptions)!.Summary;
                continue;
            }
            if (evt.Seq <= compactedThrough || evt.Seq > throughSeq) continue;
            switch (evt.Type)
            {
                case SessionEventTypes.UserMessage:
                    lines.Add($"[user] {evt.Data.GetProperty("text").GetString()}");
                    break;
                case SessionEventTypes.AssistantMessage:
                    var data = evt.Data.Deserialize<AssistantMessageData>(Session.JsonOptions)!;
                    foreach (var block in data.Blocks)
                    {
                        switch (block)
                        {
                            case TextBlock t:
                                lines.Add($"[assistant] {t.Text}");
                                break;
                            case ToolUseBlock tu:
                                lines.Add($"[tool call] {tu.Name} {Truncate(tu.Input.GetRawText(), 1500)}");
                                break;
                        }
                    }
                    break;
                case SessionEventTypes.ToolResult:
                    var result = evt.Data.Deserialize<ToolResultData>(Session.JsonOptions)!;
                    lines.Add($"[tool result{(result.IsError ? " error" : "")}] {Truncate(result.Content, 2000)}");
                    break;
            }
        }

        var transcript = new StringBuilder();
        if (previousSummary is not null)
            transcript.AppendLine($"[previous checkpoint]\n{previousSummary}\n");

        // Bound the total: keep the earliest lines (goals/decisions) and the newest lines
        // (current state), eliding the middle when the range is huge.
        var head = new List<string>();
        var tail = new List<string>();
        int headChars = 0, tailChars = 0, elided = 0;
        foreach (var line in lines)
        {
            if (headChars + line.Length <= HeadBudget)
            {
                head.Add(line);
                headChars += line.Length;
            }
            else
            {
                tail.Add(line);
                tailChars += line.Length;
            }
        }
        while (tailChars > TailBudget && tail.Count > 1)
        {
            tailChars -= tail[0].Length;
            tail.RemoveAt(0);
            elided++;
        }

        foreach (var line in head) transcript.AppendLine(line);
        if (elided > 0) transcript.AppendLine($"[… {elided} earlier entries omitted …]");
        foreach (var line in tail) transcript.AppendLine(line);
        return transcript.ToString();
    }

    private static string Truncate(string content, int max) =>
        content.Length > max
            ? content[..(max / 2)] + "\n…\n" + content[^Math.Min(max / 4, content.Length)..]
            : content;

    private async Task<string> SummarizeAsync(string transcript, CancellationToken cancellationToken)
    {
        var request = new LlmRequest
        {
            Model = model,
            System = "You compress agent conversation history into a checkpoint summary.",
            Messages =
            [
                LlmMessage.User(
                    "Summarize the following agent transcript into a structured markdown checkpoint inside <compacted-summary> tags. " +
                    "Preserve: the user's goals and constraints, decisions made, files created or modified (with paths), " +
                    "commands run and their outcomes, current task state, and pending work. Omit chit-chat.\n\n" +
                    transcript),
            ],
            MaxTokens = 8192,
            ThinkingEnabled = false,
        };
        var outcome = await LlmAccumulator.CollectAsync(provider.StreamAsync(request, cancellationToken), null, cancellationToken);
        var text = string.Join("\n", outcome.Blocks.OfType<TextBlock>().Select(b => b.Text));
        var start = text.IndexOf("<compacted-summary>", StringComparison.Ordinal);
        var end = text.IndexOf("</compacted-summary>", StringComparison.Ordinal);
        if (start >= 0 && end > start)
            return text[(start + "<compacted-summary>".Length)..end].Trim();
        return text.Trim();
    }
}
