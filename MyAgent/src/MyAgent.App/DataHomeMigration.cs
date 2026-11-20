using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyAgent.App;

/// <summary>数据目录迁移：把旧数据目录里的 sessions/、schedules.json、logs/ 合并进新目录。
/// 在应用启动、SessionStore/ScheduleStore 读取数据之前执行（此时没有任何会话在写文件）。
///
/// 规则：
/// - 复制而非移动：旧目录文件原样保留，迁移失败也不会丢数据。
/// - 会话文件按"新者胜"合并：目标缺失或源文件更新时间更晚才覆盖，
///   这样"改完设置到重启之间"继续产生的对话内容也不会丢。
/// - schedules.json 按记录 id 合并，目标已有的记录优先。
/// - 日志只补缺失文件，不覆盖。
/// - 每个来源目录只合并一次：目标目录下的 data-migration.json 记录已合并来源，
///   避免用户在新目录里删除的旧会话在下次启动时被"复活"。
/// - 旧版本只改配置不迁移的兜底：目标目录没有迁移标记且从未记录过来源时，
///   把配置锚点（~/.myagent）的历史数据合并进来一次。</summary>
public static class DataHomeMigration
{
    public const string MarkerFileName = "data-migration.json";

    /// <summary>执行启动迁移。anchor = 配置锚点；target = 本次启动生效的数据目录。</summary>
    public static void Run(string anchor, string target, SettingsService settings)
    {
        try
        {
            RunCore(anchor, target, settings);
        }
        catch (Exception ex)
        {
            // 迁移失败不能挡住启动：旧数据仍在旧目录，把错误记进目标日志便于排查。
            try
            {
                var logs = Path.Combine(target, "logs");
                Directory.CreateDirectory(logs);
                File.AppendAllText(Path.Combine(logs, "migration.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] migration failed: {ex}\n\n");
            }
            catch { /* logging must never throw */ }
        }
    }

    private static void RunCore(string anchor, string target, SettingsService settings)
    {
        var pending = settings.Current.DataHomeMigrateFrom;
        var markerPath = Path.Combine(target, MarkerFileName);
        var merged = new HashSet<string>(ReadMarker(markerPath), StringComparer.OrdinalIgnoreCase);

        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(pending))
        {
            candidates.Add(pending);
        }
        else if (!File.Exists(markerPath) && !SamePath(anchor, target))
        {
            // 兜底：旧版本改了 dataHome 但没迁移，历史数据还躺在锚点里。
            candidates.Add(anchor);
        }

        var changed = false;
        foreach (var source in candidates)
        {
            if (string.IsNullOrEmpty(source) || SamePath(source, target) || !Directory.Exists(source))
                continue;
            var normalized = Normalize(source);
            if (merged.Contains(normalized)) continue;
            MergeInto(source, target);
            merged.Add(normalized);
            changed = true;
        }

        if (changed) WriteMarker(markerPath, merged);
        if (!string.IsNullOrEmpty(pending))
            settings.Update(JsonSerializer.SerializeToElement(new { dataHomeMigrateFrom = "" }));
    }

    /// <summary>把 source 数据目录合并进 target（调用方保证 source != target）。</summary>
    public static void MergeInto(string source, string target)
    {
        Directory.CreateDirectory(target);
        MergeSessions(Path.Combine(source, "sessions"), Path.Combine(target, "sessions"));
        MergeSchedules(Path.Combine(source, "schedules.json"), Path.Combine(target, "schedules.json"));
        MergeLogs(Path.Combine(source, "logs"), Path.Combine(target, "logs"));
    }

    private static void MergeSessions(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        foreach (var srcFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, srcFile);
            var dstFile = Path.Combine(target, relative);
            if (File.Exists(dstFile) &&
                File.GetLastWriteTimeUtc(srcFile) <= File.GetLastWriteTimeUtc(dstFile))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
            File.Copy(srcFile, dstFile, overwrite: true);
        }
        // 空目录（如刚建还没写文件的会话）也搬过去，保持结构完整。
        foreach (var srcDir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, srcDir)));
    }

    private static void MergeSchedules(string source, string target)
    {
        if (!File.Exists(source)) return;
        if (!File.Exists(target))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
            return;
        }
        if (JsonNode.Parse(File.ReadAllText(source)) is not JsonArray srcRecords) return;
        var dstRecords = JsonNode.Parse(File.ReadAllText(target)) as JsonArray ?? [];
        var ids = dstRecords
            .Select(n => n?["id"]?.GetValue<string>())
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.Ordinal);
        var appended = false;
        foreach (var record in srcRecords)
        {
            var id = record?["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id) || ids.Contains(id)) continue;
            dstRecords.Add(JsonNode.Parse(record!.ToJsonString()));
            ids.Add(id);
            appended = true;
        }
        if (appended)
            File.WriteAllText(target, dstRecords.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void MergeLogs(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        foreach (var srcFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dstFile = Path.Combine(target, Path.GetRelativePath(source, srcFile));
            if (File.Exists(dstFile)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
            File.Copy(srcFile, dstFile);
        }
    }

    private static IEnumerable<string> ReadMarker(string markerPath)
    {
        if (!File.Exists(markerPath)) yield break;
        JsonArray? sources = null;
        try { sources = JsonNode.Parse(File.ReadAllText(markerPath))?["mergedFrom"] as JsonArray; }
        catch (JsonException) { /* 标记损坏当作空，重新合并一次是幂等的 */ }
        if (sources is null) yield break;
        foreach (var node in sources)
            if (node?.GetValue<string>() is { Length: > 0 } value)
                yield return value;
    }

    private static void WriteMarker(string markerPath, HashSet<string> merged)
    {
        var marker = new JsonObject
        {
            ["mergedFrom"] = new JsonArray(merged.Select(m => (JsonNode)m).ToArray()),
            ["migratedAt"] = DateTimeOffset.Now.ToString("O"),
        };
        File.WriteAllText(markerPath, marker.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd('\\', '/');
}
