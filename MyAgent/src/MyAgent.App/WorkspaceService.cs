using System.Text.Json;

namespace MyAgent.App;

public sealed record WorkspaceEntry(string Id, string Path, string Name, DateTimeOffset CreatedAt);

public sealed class WorkspaceService
{
    private readonly string _path;
    private readonly object _gate = new();

    public IReadOnlyList<WorkspaceEntry> Current { get; private set; }

    public WorkspaceService(string home)
    {
        _path = Path.Combine(home, "workspaces.json");
        Current = Load();
        MigrateLegacyDefaultWorkspace();
        if (Current.Count == 0)
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var def = Path.Combine(docs, "myagent", "default-workspace");
            Directory.CreateDirectory(def);
            Current = [Add(def)];
        }
    }

    private void MigrateLegacyDefaultWorkspace()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var oldDef = Path.Combine(docs, "deepseek-harness", "default-workspace");
        var newDef = Path.Combine(docs, "myagent", "default-workspace");
        if (!Current.Any(w => string.Equals(w.Path, oldDef, StringComparison.OrdinalIgnoreCase))) return;
        if (!Directory.Exists(newDef) && Directory.Exists(oldDef)) Directory.Move(oldDef, newDef);
        Current = [.. Current.Select(w => string.Equals(w.Path, oldDef, StringComparison.OrdinalIgnoreCase) ? w with { Path = newDef } : w)];
        File.WriteAllText(_path, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
    }

    private List<WorkspaceEntry> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<WorkspaceEntry>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            Current = [.. Current.Where(w => w.Id != id)];
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public WorkspaceEntry Add(string path)
    {
        var full = Path.GetFullPath(path);
        lock (_gate)
        {
            var existing = Current.FirstOrDefault(w => string.Equals(w.Path, full, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;
            var trimmed = full.TrimEnd('\\', '/');
            var name = Path.GetFileName(trimmed);
            var entry = new WorkspaceEntry(
                $"w_{Guid.NewGuid():N}",
                full,
                string.IsNullOrEmpty(name) ? full : name,
                DateTimeOffset.UtcNow);
            Current = [.. Current, entry];
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
            return entry;
        }
    }
}