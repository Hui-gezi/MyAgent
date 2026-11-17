using System.Net.Http.Headers;
using System.Text.Json;
using MyAgent.Core;

namespace MyAgent.Llm;

public sealed record RemoteModel(string Id, string Name);

public static class RemoteModelList
{
    public static async Task<IReadOnlyList<RemoteModel>> FetchAsync(
        string provider, string baseUrl, string apiKey, CancellationToken cancellationToken = default)
    {
        var root = baseUrl.Trim().TrimEnd('/');
        if (root.Length == 0)
            root = provider == "anthropic" ? "https://api.deepseek.com/anthropic" : "https://api.deepseek.com";
        if (provider == "anthropic" && root.EndsWith("/anthropic", StringComparison.OrdinalIgnoreCase))
            root = root[..^"/anthropic".Length];

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Get, root + "/models");
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException(LlmErrorCodes.Transport, $"无法连接模型服务：{ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmException(LlmErrorCodes.Timeout, "获取模型列表超时（15 秒）。");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                var hint = code is 401 or 403 ? "请检查 API Key。" : "请检查 Base URL 与服务状态。";
                throw new LlmException(
                    code is 401 or 403 ? LlmErrorCodes.Auth : LlmErrorCodes.Server,
                    $"获取模型列表失败（HTTP {code}）：{hint}");
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                throw new LlmException(LlmErrorCodes.EmptyResponse, "接口返回不是 JSON，无法解析模型列表。");
            }
            using (doc)
            {
                var array = doc.RootElement.ValueKind switch
                {
                    JsonValueKind.Array => doc.RootElement,
                    JsonValueKind.Object when doc.RootElement.TryGetProperty("data", out var data)
                        && data.ValueKind == JsonValueKind.Array => data,
                    _ => throw new LlmException(LlmErrorCodes.EmptyResponse, "接口返回格式无法识别（缺少 data 数组）。"),
                };

                var models = new List<RemoteModel>();
                foreach (var entry in array.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object &&
                        entry.TryGetProperty("id", out var id) &&
                        id.GetString() is { Length: > 0 } modelId)
                        models.Add(new RemoteModel(modelId, modelId));
                }
                if (models.Count == 0)
                    throw new LlmException(LlmErrorCodes.EmptyResponse, "接口未返回任何模型。");
                return models;
            }
        }
    }
}