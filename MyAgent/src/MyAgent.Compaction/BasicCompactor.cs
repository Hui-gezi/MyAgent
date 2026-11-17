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

    public int Threshold => (int)Math.Min(TriggerRatio * contextWindow, contextWindow - maxTokens - Headroom);

    public async Task MaybeCompactAsync(Session session, CancellationToken cancellationToken)
    {
        var (_, messages) = session.DeriveMessages();
        var estimated = EstimateTokens(messages);
        if (estimated < Threshold) return;

        var boundarySeq = FindBoundarySeq(session, (int)(Threshold * TailRatio));
        if (boundarySeq is null) return;

        var transcript = RenderTranscript(session, boundarySeq.Value);
        var summary = await SummarizeAsync(transcript, cancellationToken);
        if (string.IsNullOrWhiteSpace(summary)) return;

        session.Append(SessionEventTypes.Compaction, new CompactionData(summary, boundarySeq.Value));
    }

    private static int EstimateTokens(IEnumerable<LlmMessage> messages) =>
        messages.Sum(m => m.Blocks.Sum(b => b switch
        {
            TextBlock t => t.Text.Length / 4,
            ThinkingBlock th => th.Text.Length / 4,
            ToolUseBlock tu => tu.Input.GetRawText().Length / 4 + tu.Name.Length,
            ToolResultBlock tr => tr.Content.Length / 4,
            _ => 0,
        }));

    private static long? FindBoundarySeq(Session session, int tailBudget)
    {
        var events = session.Events;
        var turnEnds = events.Where(e => e.Type == SessionEventTypes.TurnEnd).Select(e => e.Seq).ToList();
        for (var i = turnEnds.Count - 1; i >= 0; i--)
        {
            var seq = turnEnds[i];
            var tailTokens = events.Where(e => e.Seq > seq).Sum(e => e.Data.GetRawText().Length / 4);
            if (tailTokens > tailBudget) break;
            var headTokens = events.Where(e => e.Seq <= seq).Sum(e => e.Data.GetRawText().Length / 4);
            if (headTokens > 0) return seq;
        }
        return null;
    }

    private static string RenderTranscript(Session session, long throughSeq)
    {
        var transcript = new StringBuilder();
        foreach (var evt in session.Events.Where(e => e.Seq <= throughSeq))
        {
            switch (evt.Type)
            {
                case SessionEventTypes.UserMessage:
                    transcript.AppendLine($"[user] {evt.Data.GetProperty("text").GetString()}");
                    break;
                case SessionEventTypes.AssistantMessage:
                    var data = evt.Data.Deserialize<AssistantMessageData>(Session.JsonOptions)!;
                    foreach (var block in data.Blocks)
                    {
                        switch (block)
                        {
                            case TextBlock t:
                                transcript.AppendLine($"[assistant] {t.Text}");
                                break;
                            case ToolUseBlock tu:
                                transcript.AppendLine($"[tool call] {tu.Name} {tu.Input.GetRawText()}");
                                break;
                        }
                    }
                    break;
                case SessionEventTypes.ToolResult:
                    var result = evt.Data.Deserialize<ToolResultData>(Session.JsonOptions)!;
                    var content = result.Content.Length > 2000
                        ? result.Content[..1000] + "\n…\n" + result.Content[^500..]
                        : result.Content;
                    transcript.AppendLine($"[tool result{(result.IsError ? " error" : "")}] {content}");
                    break;
            }
        }
        return transcript.ToString();
    }

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