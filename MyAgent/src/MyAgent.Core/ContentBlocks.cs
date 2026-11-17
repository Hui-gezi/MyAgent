using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyAgent.Core;

public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

public sealed record ThinkingBlock(string Text, string? Signature = null) : ContentBlock;

public sealed record ToolUseBlock(string Id, string Name, JsonElement Input) : ContentBlock;

public sealed record ToolResultBlock(string ToolUseId, string Content, bool IsError = false) : ContentBlock;

public sealed class LlmMessage
{
    public string Role { get; set; } = "user";
    public List<ContentBlock> Blocks { get; set; } = [];

    public static LlmMessage User(string text) => new() { Role = "user", Blocks = [new TextBlock(text)] };
    public static LlmMessage Assistant(List<ContentBlock> blocks) => new() { Role = "assistant", Blocks = blocks };
}

public sealed class ContentBlockConverter : JsonConverter<ContentBlock>
{
    public override ContentBlock Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();
        return type switch
        {
            "text" => new TextBlock(root.GetProperty("text").GetString() ?? ""),
            "thinking" => new ThinkingBlock(
                root.GetProperty("thinking").GetString() ?? "",
                root.TryGetProperty("signature", out var sig) && sig.ValueKind == JsonValueKind.String ? sig.GetString() : null),
            "tool_use" => new ToolUseBlock(
                root.GetProperty("id").GetString() ?? "",
                root.GetProperty("name").GetString() ?? "",
                root.GetProperty("input").Clone()),
            "tool_result" => new ToolResultBlock(
                root.GetProperty("tool_use_id").GetString() ?? "",
                root.GetProperty("content").GetString() ?? "",
                root.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True),
            _ => throw new JsonException($"Unknown content block type '{type}'."),
        };
    }

    public override void Write(Utf8JsonWriter writer, ContentBlock value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case TextBlock t:
                writer.WriteString("type", "text");
                writer.WriteString("text", t.Text);
                break;
            case ThinkingBlock th:
                writer.WriteString("type", "thinking");
                writer.WriteString("thinking", th.Text);
                if (th.Signature is not null) writer.WriteString("signature", th.Signature);
                break;
            case ToolUseBlock tu:
                writer.WriteString("type", "tool_use");
                writer.WriteString("id", tu.Id);
                writer.WriteString("name", tu.Name);
                writer.WritePropertyName("input");
                tu.Input.WriteTo(writer);
                break;
            case ToolResultBlock tr:
                writer.WriteString("type", "tool_result");
                writer.WriteString("tool_use_id", tr.ToolUseId);
                writer.WriteString("content", tr.Content);
                if (tr.IsError) writer.WriteBoolean("is_error", true);
                break;
        }
        writer.WriteEndObject();
    }
}