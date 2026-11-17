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

    [JsonPropertyName("effort")]
    public string Effort { get; set; } = "high";

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    [JsonPropertyName("modelCatalog")]
    public List<string> ModelCatalog { get; set; } = [];

    [JsonPropertyName("modelCatalogProvider")]
    public string ModelCatalogProvider { get; set; } = "";
}

public sealed class SettingsService
{
    private readonly string _path;
    private readonly object _gate = new();

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
                    case "effort": updated.Effort = property.Value.GetString() ?? updated.Effort; break;
                    case "theme": updated.Theme = property.Value.GetString() ?? updated.Theme; break;
                    case "modelCatalog":
                        updated.ModelCatalog = property.Value.EnumerateArray()
                            .Select(v => v.GetString() ?? "")
                            .Where(s => s.Length > 0)
                            .ToList();
                        break;
                    case "modelCatalogProvider": updated.ModelCatalogProvider = property.Value.GetString() ?? ""; break;
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