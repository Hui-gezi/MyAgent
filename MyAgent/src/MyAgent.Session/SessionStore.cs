using System.Text;
using System.Text.Json;
using MyAgent.Core;

namespace MyAgent.Sessions;

public sealed record SessionSummary(
    string Id,
    string? Title,
    string Cwd,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Archived);

public sealed class SessionStore
{
    public const int SessionFormatVersion = 4;
    public const string SessionFileName = "session.v4.jsonl";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public string Home { get; }
    public string Root { get; }

    public SessionStore(string? home = null)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var newHome = Path.Combine(profile, ".myagent");
        var oldHome = Path.Combine(profile, ".dsh");
        if (!Directory.Exists(newHome) && Directory.Exists(oldHome))
            Directory.Move(oldHome, newHome);
        Home = home
            ?? Environment.GetEnvironmentVariable("MYAGENT_HOME")
            ?? newHome;
        Root = Path.Combine(Home, "sessions");
        Directory.CreateDirectory(Root);
    }

    public Session CreateSession(string cwd)
    {
        var session = new Core.Session { Id = $"s_{Guid.NewGuid():N}", Cwd = cwd };
        var dir = SessionDirectory(session.Cwd, session.Id);
        Directory.CreateDirectory(dir);
        var header = new
        {
            type = "session",
            version = SessionFormatVersion,
            id = session.Id,
            createdAt = session.CreatedAt,
            cwd = session.Cwd,
        };
        File.WriteAllText(Path.Combine(dir, SessionFileName),
            JsonSerializer.Serialize(header, Core.Session.JsonOptions) + "\n", Utf8NoBom);
        AttachPersistence(session);
        return session;
    }

    public Core.Session? OpenSession(string id)
    {
        var dir = FindSessionDirectory(id);
        if (dir is null) return null;
        var path = Path.Combine(dir, SessionFileName);
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0) return null;

        using var headerDoc = JsonDocument.Parse(lines[0]);
        var header = headerDoc.RootElement;
        if (header.GetProperty("version").GetInt32() != SessionFormatVersion)
            throw new HarnessError("session_format", $"Unsupported session format version in '{path}'.");

        var session = new Core.Session
        {
            Id = header.GetProperty("id").GetString()!,
            Cwd = header.GetProperty("cwd").GetString()!,
            CreatedAt = header.GetProperty("createdAt").GetDateTimeOffset(),
        };

        var events = new List<SessionEvent>();
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            events.Add(JsonSerializer.Deserialize<SessionEvent>(line, Core.Session.JsonOptions)!);
        }
        session.Restore(events);
        CloseInterruptedTurn(session);
        AttachPersistence(session);
        return session;
    }

    public IReadOnlyList<SessionSummary> ListSessions(bool includeArchived = false)
    {
        var summaries = new List<SessionSummary>();
        foreach (var workspaceDir in Directory.EnumerateDirectories(Root))
        {
            foreach (var sessionDir in Directory.EnumerateDirectories(workspaceDir))
            {
                var path = Path.Combine(sessionDir, SessionFileName);
                if (!File.Exists(path)) continue;
                var archived = File.Exists(Path.Combine(sessionDir, "archived"));
                if (archived && !includeArchived) continue;
                try
                {
                    summaries.Add(ReadSummary(path, archived));
                }
                catch (Exception ex) when (ex is JsonException or IOException or KeyNotFoundException)
                {
                    continue;
                }
            }
        }
        return [.. summaries.OrderByDescending(s => s.UpdatedAt)];
    }

    public void RenameSession(string id, string title)
    {
        var session = OpenSession(id) ?? throw new HarnessError("session_not_found", $"Session '{id}' not found.");
        session.Append(SessionEventTypes.Rename, new { title });
    }

    public void DeleteSession(string id)
    {
        var dir = FindSessionDirectory(id) ?? throw new HarnessError("session_not_found", $"Session '{id}' not found.");
        Directory.Delete(dir, true);
    }

    public void ArchiveSession(string id, bool archived = true)
    {
        var dir = FindSessionDirectory(id) ?? throw new HarnessError("session_not_found", $"Session '{id}' not found.");
        var marker = Path.Combine(dir, "archived");
        if (archived) File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
        else if (File.Exists(marker)) File.Delete(marker);
    }

    private SessionSummary ReadSummary(string path, bool archived)
    {
        string? id = null, cwd = null, title = null;
        DateTimeOffset createdAt = default, updatedAt = default;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "session")
            {
                id = root.GetProperty("id").GetString();
                cwd = root.GetProperty("cwd").GetString();
                createdAt = root.GetProperty("createdAt").GetDateTimeOffset();
                updatedAt = createdAt;
            }
            else
            {
                if (root.TryGetProperty("time", out var time)) updatedAt = time.GetDateTimeOffset();
                if (type == SessionEventTypes.UserMessage && title is null &&
                    root.TryGetProperty("data", out var userData) &&
                    userData.TryGetProperty("text", out var text))
                {
                    var first = text.GetString() ?? "";
                    title = first.Length > 60 ? first[..60] + "…" : first;
                }
                if (type == SessionEventTypes.Rename &&
                    root.TryGetProperty("data", out var renameData) &&
                    renameData.TryGetProperty("title", out var renameTitle))
                    title = renameTitle.GetString();
            }
        }
        return new SessionSummary(id ?? "", title, cwd ?? "", createdAt, updatedAt, archived);
    }

    private void CloseInterruptedTurn(Core.Session session)
    {
        var events = session.Events;
        var lastTurnStart = events.LastOrDefault(e => e.Type == SessionEventTypes.TurnStart);
        if (lastTurnStart is null) return;
        var hasTurnEnd = events.Any(e => e.Seq > lastTurnStart.Seq && e.Type == SessionEventTypes.TurnEnd);
        if (hasTurnEnd) return;

        var turnId = lastTurnStart.Data.GetProperty("turnId").GetString();
        var answered = events
            .Where(e => e.Seq > lastTurnStart.Seq && e.Type == SessionEventTypes.ToolResult)
            .Select(e => e.Data.GetProperty("toolCallId").GetString())
            .ToHashSet();
        foreach (var evt in events.Where(e => e.Seq > lastTurnStart.Seq && e.Type == SessionEventTypes.ToolCall))
        {
            var toolCallId = evt.Data.GetProperty("toolCallId").GetString()!;
            if (answered.Contains(toolCallId)) continue;
            session.Append(SessionEventTypes.ToolResult,
                new ToolResultData(toolCallId, "Error: Tool call interrupted by host restart.", true, "interrupted"));
        }
        var lastStep = events.LastOrDefault(e => e.Seq > lastTurnStart.Seq && e.Type == SessionEventTypes.StepStart);
        var stepIndex = lastStep?.Data.GetProperty("stepIndex").GetInt32() ?? 0;
        session.Append(SessionEventTypes.StepEnd, new { turnId, stepIndex });
        session.Append(SessionEventTypes.TurnEnd, new { turnId, stopReason = "interrupted" });
    }

    private void AttachPersistence(Core.Session session)
    {
        var path = Path.Combine(SessionDirectory(session.Cwd, session.Id), SessionFileName);
        var gate = new object();
        session.EventAppended += evt =>
        {
            var line = JsonSerializer.Serialize(evt, Core.Session.JsonOptions) + "\n";
            lock (gate) File.AppendAllText(path, line, Utf8NoBom);
        };
    }

    private string SessionDirectory(string cwd, string id) =>
        Path.Combine(Root, WorkspaceSlug(cwd), EscapeSegment(id));

    private string? FindSessionDirectory(string id)
    {
        var escaped = EscapeSegment(id);
        foreach (var workspaceDir in Directory.EnumerateDirectories(Root))
        {
            var candidate = Path.Combine(workspaceDir, escaped);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string WorkspaceSlug(string cwd)
    {
        var slug = new StringBuilder();
        foreach (var ch in cwd)
            slug.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        return $"--{slug.ToString().Trim('-')}--";
    }

    private static string EscapeSegment(string value)
    {
        var slug = new StringBuilder();
        foreach (var ch in value)
            slug.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '-');
        return slug.ToString();
    }
}