using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyAgent.App;

public sealed class HarnessSettings
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "mock";

    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = "";

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "deepseek-flash";

    [JsonPropertyName("thinking")]
    public bool Thinking { get; set; } = true;

    /// <summary>Real context window of the configured model (tokens). Compaction and the
    /// occupancy meter use this; a wrong (too large) value makes the provider reject the
    /// request before local compaction ever fires.</summary>
    [JsonPropertyName("contextWindow")]
    public int ContextWindow { get; set; } = 1_048_576;

    [JsonPropertyName("effort")]
    public string Effort { get; set; } = "high";

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    [JsonPropertyName("modelCatalog")]
    public List<string> ModelCatalog { get; set; } = [];

    [JsonPropertyName("modelCatalogProvider")]
    public string ModelCatalogProvider { get; set; } = "";

    // ---- permission & sandbox (mirrors dsh-permission-presets / dsh-sandbox-policy settings) ----

    /// <summary>Preset pinned into a newly created session (workspace-write / danger-full-access).</summary>
    [JsonPropertyName("permissionPreset")]
    public string PermissionPreset { get; set; } = "workspace-write";

    /// <summary>Deployment default file-sandbox mode beneath a session override.</summary>
    [JsonPropertyName("sandboxMode")]
    public string SandboxMode { get; set; } = "workspace-write";

    /// <summary>Extra absolute directories workspace-write may also write under (file tools only on Windows).</summary>
    [JsonPropertyName("sandboxWritableRoots")]
    public List<string> SandboxWritableRoots { get; set; } = [];

    /// <summary>Composer statistics detail: "detailed" (counts + dialogs) or "compact" (speed + cache hit only).
    /// Mirrors the harness performanceUsage chat setting (default detailed).</summary>
    [JsonPropertyName("performanceUsage")]
    public string PerformanceUsage { get; set; } = "detailed";

    /// <summary>Composer send shortcut: "enter" (Enter sends, Shift+Enter newline),
    /// "ctrl-enter" (Ctrl+Enter sends, Enter newline) or "alt-enter" (Alt+Enter sends,
    /// Enter newline). Default keeps the historical Enter-to-send behavior.</summary>
    [JsonPropertyName("sendKey")]
    public string SendKey { get; set; } = "enter";

    /// <summary>会话/定时任务/日志的数据目录。空串 = 跟随配置锚点（~/.myagent）。
    /// settings.json 本身永远住在锚点，不随此项移动；重启后生效。
    /// MYAGENT_HOME 环境变量在运行时覆盖此项（测试隔离等场景）。</summary>
    [JsonPropertyName("dataHome")]
    public string DataHome { get; set; } = "";

    /// <summary>待迁移的旧数据目录。设置页更改 dataHome 时写入（此时无会话在运行），
    /// 下次启动解析数据目录前把它的 sessions/、schedules.json、logs/ 合并进新目录，
    /// 完成后清空。旧目录文件保留不删除。</summary>
    [JsonPropertyName("dataHomeMigrateFrom")]
    public string DataHomeMigrateFrom { get; set; } = "";
}

public sealed class SettingsService
{
    private readonly string _path;
    private readonly object _gate = new();

    /// <summary>数据目录可写校验：建目录 + 试写临时文件 + 删除。
    /// 切换目录时当场调用，不可写立即报错，而不是等下次启动才发现。</summary>
    public static bool TryProbeWritable(string directory, out string? error)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public HarnessSettings Current { get; private set; }
    public event Action? Changed;

    public SettingsService(string home)
    {
        _path = Path.Combine(home, "settings.json");
        Current = Load();
    }

    private HarnessSettings Load()
    {
        HarnessSettings settings;
        try
        {
            settings = File.Exists(_path)
                ? JsonSerializer.Deserialize<HarnessSettings>(File.ReadAllText(_path)) ?? new HarnessSettings()
                : new HarnessSettings();
        }
        catch (JsonException)
        {
            settings = new HarnessSettings();
        }

        if (settings.Provider == "deepseek") settings.Provider = "anthropic";
        if (settings.ModelCatalogProvider == "deepseek") settings.ModelCatalogProvider = "anthropic";
        var envKey = Environment.GetEnvironmentVariable("MYAGENT_API_KEY");
        if (!string.IsNullOrEmpty(envKey) && string.IsNullOrEmpty(settings.ApiKey))
        {
            settings.ApiKey = envKey;
            if (settings.Provider == "mock") settings.Provider = "anthropic";
        }
        var envBase = Environment.GetEnvironmentVariable("MYAGENT_BASE_URL");
        if (!string.IsNullOrEmpty(envBase) && string.IsNullOrEmpty(settings.BaseUrl))
            settings.BaseUrl = envBase;
        return settings;
    }

    public HarnessSettings Update(JsonElement patch)
    {
        lock (_gate)
        {
            var merged = JsonSerializer.SerializeToElement(Current);
            var updated = JsonSerializer.Deserialize<HarnessSettings>(merged.GetRawText())!;
            foreach (var property in patch.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "provider": updated.Provider = property.Value.GetString() ?? updated.Provider; break;
                    case "apiKey": updated.ApiKey = property.Value.GetString() ?? ""; break;
                    case "baseUrl": updated.BaseUrl = property.Value.GetString() ?? ""; break;
                    case "model": updated.Model = property.Value.GetString() ?? updated.Model; break;
                    case "thinking": updated.Thinking = property.Value.GetBoolean(); break;
                    case "contextWindow":
                        if (property.Value.TryGetInt32(out var window) && window >= 8_192)
                            updated.ContextWindow = window;
                        break;
                    case "effort": updated.Effort = property.Value.GetString() ?? updated.Effort; break;
                    case "theme": updated.Theme = property.Value.GetString() ?? updated.Theme; break;
                    case "modelCatalog":
                        updated.ModelCatalog = property.Value.EnumerateArray()
                            .Select(v => v.GetString() ?? "")
                            .Where(s => s.Length > 0)
                            .ToList();
                        break;
                    case "modelCatalogProvider": updated.ModelCatalogProvider = property.Value.GetString() ?? ""; break;
                    case "permissionPreset": updated.PermissionPreset = property.Value.GetString() ?? updated.PermissionPreset; break;
                    case "sandboxMode": updated.SandboxMode = property.Value.GetString() ?? updated.SandboxMode; break;
                    case "performanceUsage":
                        var mode = property.Value.GetString();
                        if (mode is "compact" or "detailed") updated.PerformanceUsage = mode;
                        break;
                    case "sendKey":
                        var sendKey = property.Value.GetString();
                        if (sendKey is "enter" or "ctrl-enter" or "alt-enter") updated.SendKey = sendKey;
                        break;
                    case "dataHome":
                        // 空串合法（回到默认锚点）；非空必须是绝对路径，相对路径拒绝写入。
                        var dataHome = property.Value.GetString() ?? "";
                        if (dataHome.Length == 0 || Path.IsPathFullyQualified(dataHome))
                            updated.DataHome = dataHome.TrimEnd('\\', '/');
                        break;
                    case "dataHomeMigrateFrom":
                        var migrateFrom = property.Value.GetString() ?? "";
                        if (migrateFrom.Length == 0 || Path.IsPathFullyQualified(migrateFrom))
                            updated.DataHomeMigrateFrom = migrateFrom.TrimEnd('\\', '/');
                        break;
                    case "sandboxWritableRoots":
                        updated.SandboxWritableRoots = property.Value.EnumerateArray()
                            .Select(v => v.GetString() ?? "")
                            .Where(s => s.Length > 0)
                            .ToList();
                        break;
                }
            }
            Current = updated;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
        }
        Changed?.Invoke();
        return Current;
    }
}