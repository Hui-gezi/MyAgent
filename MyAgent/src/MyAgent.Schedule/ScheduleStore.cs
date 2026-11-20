using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyAgent.Schedule;

public enum ScheduleStatus { Active, Paused, Ended }

/// <summary>Durable timing rule. Fields mirror dsh-schedule's ScheduleRecord kinds.</summary>
public sealed class ScheduleRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "after";

    [JsonPropertyName("afterSeconds")] public int? AfterSeconds { get; set; }
    [JsonPropertyName("everySeconds")] public int? EverySeconds { get; set; }
    [JsonPropertyName("time")] public string? Time { get; set; }
    [JsonPropertyName("timeZone")] public string? TimeZone { get; set; }
    [JsonPropertyName("weekdays")] public List<int>? Weekdays { get; set; }
    [JsonPropertyName("expression")] public string? Expression { get; set; }

    [JsonPropertyName("scheduledAt")] public DateTimeOffset ScheduledAt { get; set; }
    [JsonPropertyName("status")] public ScheduleStatus Status { get; set; } = ScheduleStatus.Active;
    [JsonPropertyName("targetSessionId")] public string? TargetSessionId { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
    [JsonPropertyName("lastDeliveredAt")] public DateTimeOffset? LastDeliveredAt { get; set; }
    [JsonPropertyName("deliveryCount")] public int DeliveryCount { get; set; }
}

/// <summary>
/// JSON-file persistence for scheduled tasks, stored at ~/.myagent/schedules.json (or MYAGENT_HOME).
/// </summary>
public sealed class ScheduleStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<ScheduleRecord> _records = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
    };

    public ScheduleStore(string? home = null)
    {
        var root = home
            ?? Environment.GetEnvironmentVariable("MYAGENT_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".myagent");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "schedules.json");
        Load();
    }

    public string StoragePath => _path;

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var text = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(text)) return;
            var loaded = JsonSerializer.Deserialize<List<ScheduleRecord>>(text, Json);
            if (loaded is not null) lock (_gate) _records.AddRange(loaded);
        }
        catch (JsonException) { /* corrupt file: start empty rather than crash the host */ }
        catch (IOException) { }
    }

    public IReadOnlyList<ScheduleRecord> List(bool? active = null, int limit = 100, int offset = 0)
    {
        lock (_gate)
        {
            var query = _records.AsEnumerable();
            if (active is true) query = query.Where(r => r.Status == ScheduleStatus.Active);
            if (active is false) query = query.Where(r => r.Status != ScheduleStatus.Active);
            var ordered = query
                .OrderBy(r => r.Status == ScheduleStatus.Active ? 0 : 1)
                .ThenBy(r => r.ScheduledAt)
                .ToList();
            return ordered.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 500)).ToList();
        }
    }

    public ScheduleRecord? Get(string id)
    {
        lock (_gate) return _records.FirstOrDefault(r => r.Id == id);
    }

    public void Add(ScheduleRecord record)
    {
        lock (_gate) _records.Add(record);
        Save();
    }

    public bool Update(string id, Action<ScheduleRecord> mutate)
    {
        lock (_gate)
        {
            var record = _records.FirstOrDefault(r => r.Id == id);
            if (record is null) return false;
            mutate(record);
        }
        Save();
        return true;
    }

    public bool Delete(string id)
    {
        bool removed;
        lock (_gate) removed = _records.RemoveAll(r => r.Id == id) > 0;
        if (removed) Save();
        return removed;
    }

    private void Save()
    {
        lock (_gate)
        {
            var text = JsonSerializer.Serialize(_records, Json);
            File.WriteAllText(_path, text);
        }
    }
}
