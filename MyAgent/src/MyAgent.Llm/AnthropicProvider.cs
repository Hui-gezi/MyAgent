using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MyAgent.Core;

namespace MyAgent.Llm;

public sealed class AnthropicOptions
{
    public string BaseUrl { get; set; } = "https://api.deepseek.com/anthropic";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = ModelIds.Flash;
    public string Effort { get; set; } = "high";
    public int MaxRetries { get; set; } = LlmRetryPolicy.MaxRetries;
}

public static class ModelIds
{
    public const string Flash = "deepseek-flash";
    public const string V4Pro = "deepseek-v4-pro";
    public const int ContextWindow = 1_048_576;
}

public sealed class AnthropicProvider(AnthropicOptions options, HttpClient? httpClient = null) : ILlmProvider, IDisposable
{
    private readonly HttpClient _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    private readonly bool _ownsHttp = httpClient is null;

    public string Name => "anthropic";

    public async IAsyncEnumerable<StreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new LlmException(LlmErrorCodes.Auth, "API key is not configured. Set it in Settings or via MYAGENT_API_KEY.");

        var payload = SerializeRequest(request);
        var url = options.BaseUrl.TrimEnd('/') + "/v1/messages";

        var response = await SendWithRetryAsync(url, payload, cancellationToken);
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line["data:".Length..].Trim();
                if (data.Length == 0 || data == "[DONE]") continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(data); }
                catch (JsonException) { continue; }
                using (doc)
                {
                    foreach (var chunk in TranslateEvent(doc.RootElement))
                        yield return chunk;
                }
            }
        }
        finally
        {
            response.Dispose();
        }
    }

    private static IEnumerable<StreamChunk> TranslateEvent(JsonElement root)
    {
        var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
        switch (type)
        {
            case "error":
                var errorText = root.TryGetProperty("error", out var err) ? err.GetRawText() : root.GetRawText();
                throw new LlmException(LlmErrorCodes.Server, $"Provider stream error: {errorText}");

            case "message_start":
                if (root.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("usage", out var startUsage) &&
                    startUsage.TryGetProperty("input_tokens", out var inputTokens))
                    yield return new UsageChunk(inputTokens.GetInt32(), 0);
                break;

            case "content_block_start":
                var startIndex = root.GetProperty("index").GetInt32();
                var block = root.GetProperty("content_block");
                var blockType = block.GetProperty("type").GetString();
                if (blockType == "tool_use")
                {
                    yield return new ToolCallDelta(
                        startIndex,
                        block.TryGetProperty("id", out var id) ? id.GetString() : null,
                        block.TryGetProperty("name", out var name) ? name.GetString() : null,
                        null);
                }
                break;

            case "content_block_delta":
                var index = root.GetProperty("index").GetInt32();
                var delta = root.GetProperty("delta");
                var deltaType = delta.GetProperty("type").GetString();
                switch (deltaType)
                {
                    case "text_delta":
                        if (delta.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 } t)
                            yield return new TextDelta(t);
                        break;
                    case "thinking_delta":
                        if (delta.TryGetProperty("thinking", out var thinking) && thinking.GetString() is { Length: > 0 } th)
                            yield return new ReasoningDelta(th);
                        break;
                    case "signature_delta":
                        if (delta.TryGetProperty("signature", out var signature) && signature.GetString() is { Length: > 0 } sig)
                            yield return new SignatureDelta(sig);
                        break;
                    case "input_json_delta":
                        if (delta.TryGetProperty("partial_json", out var partial) && partial.GetString() is { Length: > 0 } pj)
                            yield return new ToolCallDelta(index, null, null, pj);
                        break;
                }
                break;

            case "message_delta":
                if (root.TryGetProperty("usage", out var deltaUsage) &&
                    deltaUsage.TryGetProperty("output_tokens", out var outputTokens))
                    yield return new UsageChunk(0, outputTokens.GetInt32());
                if (root.TryGetProperty("delta", out var messageDelta) &&
                    messageDelta.TryGetProperty("stop_reason", out var stopReason) &&
                    stopReason.ValueKind == JsonValueKind.String)
                    yield return new FinishChunk(MapStopReason(stopReason.GetString()));
                break;

            case "message_stop":
                yield break;
        }
    }

    private static string MapStopReason(string? reason) => reason switch
    {
        "tool_use" => "tool-calls",
        "max_tokens" => "max-tokens",
        _ => "stop",
    };

    private string SerializeRequest(LlmRequest request)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", request.Model);
            writer.WriteBoolean("stream", true);
            writer.WriteNumber("max_tokens", request.MaxTokens);
            writer.WritePropertyName("thinking");
            writer.WriteStartObject();
            writer.WriteString("type", request.ThinkingEnabled ? "enabled" : "disabled");
            writer.WriteEndObject();
            writer.WritePropertyName("output_config");
            writer.WriteStartObject();
            writer.WriteString("effort", options.Effort);
            writer.WriteEndObject();

            if (!string.IsNullOrEmpty(request.System))
                writer.WriteString("system", request.System);

            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            foreach (var message in request.Messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", message.Role);
                writer.WritePropertyName("content");
                writer.WriteStartArray();
                foreach (var block in message.Blocks)
                {
                    switch (block)
                    {
                        case TextBlock t:
                            writer.WriteStartObject();
                            writer.WriteString("type", "text");
                            writer.WriteString("text", t.Text);
                            writer.WriteEndObject();
                            break;
                        case ThinkingBlock th:
                            writer.WriteStartObject();
                            writer.WriteString("type", "thinking");
                            writer.WriteString("thinking", th.Text);
                            if (th.Signature is not null) writer.WriteString("signature", th.Signature);
                            writer.WriteEndObject();
                            break;
                        case ToolUseBlock tu:
                            writer.WriteStartObject();
                            writer.WriteString("type", "tool_use");
                            writer.WriteString("id", tu.Id);
                            writer.WriteString("name", tu.Name);
                            writer.WritePropertyName("input");
                            tu.Input.WriteTo(writer);
                            writer.WriteEndObject();
                            break;
                        case ToolResultBlock tr:
                            writer.WriteStartObject();
                            writer.WriteString("type", "tool_result");
                            writer.WriteString("tool_use_id", tr.ToolUseId);
                            writer.WriteString("content", tr.Content);
                            if (tr.IsError) writer.WriteBoolean("is_error", true);
                            writer.WriteEndObject();
                            break;
                    }
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            if (request.Tools is { Count: > 0 })
            {
                writer.WritePropertyName("tools");
                writer.WriteStartArray();
                foreach (var tool in request.Tools)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", tool.Name);
                    writer.WriteString("description", tool.Description);
                    writer.WritePropertyName("input_schema");
                    tool.InputSchema.WriteTo(writer);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        string url, string payload, CancellationToken cancellationToken)
    {
        LlmException? last = null;
        for (var attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(LlmRetryPolicy.DelayFor(attempt - 1, last?.RetryAfter), cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("x-api-key", options.ApiKey);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {options.ApiKey}");
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                last = new LlmException(LlmErrorCodes.Transport, $"Network error contacting provider: {ex.Message}", ex);
                continue;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                last = new LlmException(LlmErrorCodes.Timeout, "Provider request timed out.");
                continue;
            }

            if (response.IsSuccessStatusCode) return response;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = (int)response.StatusCode;
            TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
            response.Dispose();
            var error = LlmException.FromStatus(status, body);
            var coded = new LlmException(error.Code, error.Message) { RetryAfter = retryAfter };
            if (LlmErrorCodes.IsRetryable(coded.Code))
            {
                last = coded;
                continue;
            }
            throw coded;
        }
        throw last ?? new LlmException(LlmErrorCodes.Transport, "Provider request failed.");
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}