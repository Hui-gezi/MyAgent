using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MyAgent.Core;

namespace MyAgent.Llm;

public sealed class OpenAiCompatOptions
{
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "deepseek-chat";
    public bool Thinking { get; set; } = true;
    public string Effort { get; set; } = "high";
    public int MaxRetries { get; set; } = LlmRetryPolicy.MaxRetries;
}

public sealed class OpenAiCompatProvider(OpenAiCompatOptions options, HttpClient? httpClient = null) : ILlmProvider, IDisposable
{
    private readonly HttpClient _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    private readonly bool _ownsHttp = httpClient is null;

    public string Name => "openai-compat";

    public async IAsyncEnumerable<StreamChunk> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new LlmException(LlmErrorCodes.Auth, "API key is not configured. Set it in Settings or via MYAGENT_API_KEY.");

        var payload = SerializeRequest(request);
        var url = options.BaseUrl.TrimEnd('/') + "/chat/completions";

        var response = await SendWithRetryAsync(url, payload, cancellationToken);
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line["data:".Length..].Trim();
                if (data.Length == 0) continue;
                if (data == "[DONE]") yield break;

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
        if (root.TryGetProperty("error", out var error))
            throw new LlmException(LlmErrorCodes.Server, $"Provider stream error: {error.GetRawText()}");

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            yield return new UsageChunk(
                usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0,
                usage.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : 0);
        }

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            yield break;

        var choice = choices[0];
        if (choice.TryGetProperty("delta", out var delta))
        {
            if (delta.TryGetProperty("reasoning_content", out var reasoning) &&
                reasoning.ValueKind == JsonValueKind.String && reasoning.GetString() is { Length: > 0 } r)
                yield return new ReasoningDelta(r);
            else if (delta.TryGetProperty("reasoning", out var reasoning2) &&
                reasoning2.ValueKind == JsonValueKind.String && reasoning2.GetString() is { Length: > 0 } r2)
                yield return new ReasoningDelta(r2);
            else if (delta.TryGetProperty("reasoning_text", out var reasoning3) &&
                reasoning3.ValueKind == JsonValueKind.String && reasoning3.GetString() is { Length: > 0 } r3)
                yield return new ReasoningDelta(r3);
            if (delta.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } t)
                yield return new TextDelta(t);
            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in toolCalls.EnumerateArray())
                {
                    var index = tc.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;
                    string? id = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                    string? name = null;
                    string? args = null;
                    if (tc.TryGetProperty("function", out var fn))
                    {
                        if (fn.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String &&
                            nameEl.GetString() is { Length: > 0 } n)
                            name = n;
                        if (fn.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String &&
                            argsEl.GetString() is { Length: > 0 } a)
                            args = a;
                    }
                    yield return new ToolCallDelta(index, id, name, args);
                }
            }
        }

        if (choice.TryGetProperty("finish_reason", out var finish) &&
            finish.ValueKind == JsonValueKind.String && finish.GetString() is { Length: > 0 } finishReason)
        {
            yield return new FinishChunk(finishReason switch
            {
                "tool_calls" => "tool-calls",
                "length" => "max-tokens",
                _ => "stop",
            });
        }
    }

    private string SerializeRequest(LlmRequest request)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", request.Model);
            writer.WriteBoolean("stream", true);
            writer.WriteNumber("max_tokens", Math.Min(request.MaxTokens, 8192));
            writer.WriteBoolean("enable_thinking", options.Thinking);
            if (options.Thinking) writer.WriteString("reasoning_effort", options.Effort);

            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            if (!string.IsNullOrEmpty(request.System))
            {
                writer.WriteStartObject();
                writer.WriteString("role", "system");
                writer.WriteString("content", request.System);
                writer.WriteEndObject();
            }
            foreach (var message in request.Messages)
            {
                var textParts = new StringBuilder();
                var reasoningParts = new StringBuilder();
                var toolUses = new List<ToolUseBlock>();
                var toolResults = new List<ToolResultBlock>();
                foreach (var block in message.Blocks)
                {
                    switch (block)
                    {
                        case TextBlock t: textParts.Append(t.Text); break;
                        case ThinkingBlock th: reasoningParts.Append(th.Text); break;
                        case ToolUseBlock tu: toolUses.Add(tu); break;
                        case ToolResultBlock tr: toolResults.Add(tr); break;
                    }
                }

                foreach (var tr in toolResults)
                {
                    writer.WriteStartObject();
                    writer.WriteString("role", "tool");
                    writer.WriteString("tool_call_id", tr.ToolUseId);
                    writer.WriteString("content", tr.Content);
                    writer.WriteEndObject();
                }

                if (message.Role == "assistant")
                {
                    writer.WriteStartObject();
                    writer.WriteString("role", "assistant");
                    writer.WriteString("content", textParts.ToString());
                    if (reasoningParts.Length > 0) writer.WriteString("reasoning_content", reasoningParts.ToString());
                    if (toolUses.Count > 0)
                    {
                        writer.WritePropertyName("tool_calls");
                        writer.WriteStartArray();
                        foreach (var tu in toolUses)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("id", tu.Id);
                            writer.WriteString("type", "function");
                            writer.WritePropertyName("function");
                            writer.WriteStartObject();
                            writer.WriteString("name", tu.Name);
                            writer.WriteString("arguments", tu.Input.GetRawText());
                            writer.WriteEndObject();
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                    writer.WriteEndObject();
                }
                else if (textParts.Length > 0)
                {
                    writer.WriteStartObject();
                    writer.WriteString("role", "user");
                    writer.WriteString("content", textParts.ToString());
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();

            if (request.Tools is { Count: > 0 })
            {
                writer.WritePropertyName("tools");
                writer.WriteStartArray();
                foreach (var tool in request.Tools)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function");
                    writer.WritePropertyName("function");
                    writer.WriteStartObject();
                    writer.WriteString("name", tool.Name);
                    writer.WriteString("description", tool.Description);
                    writer.WritePropertyName("parameters");
                    tool.InputSchema.WriteTo(writer);
                    writer.WriteEndObject();
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
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
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