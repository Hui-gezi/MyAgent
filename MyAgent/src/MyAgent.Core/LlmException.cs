namespace MyAgent.Core;

public static class LlmErrorCodes
{
    public const string Auth = "auth";
    public const string Quota = "quota";
    public const string RateLimit = "rate_limit";
    public const string ContextWindowExceeded = "context_window_exceeded";
    public const string InvalidRequest = "invalid_request";
    public const string Server = "server";
    public const string Timeout = "timeout";
    public const string Transport = "transport";
    public const string EmptyResponse = "empty_response";

    public static bool IsRetryable(string code) => code is
        RateLimit or Server or Timeout or Transport or EmptyResponse;
}

public sealed class LlmException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public TimeSpan? RetryAfter { get; init; }

    public static LlmException FromStatus(int status, string body)
    {
        if (body.Contains("context", StringComparison.OrdinalIgnoreCase) &&
            (body.Contains("overflow", StringComparison.OrdinalIgnoreCase) ||
             body.Contains("exceed", StringComparison.OrdinalIgnoreCase) ||
             body.Contains("too long", StringComparison.OrdinalIgnoreCase)))
            return new LlmException(LlmErrorCodes.ContextWindowExceeded, $"Context window exceeded ({status}): {body}");

        return status switch
        {
            401 or 403 => new LlmException(LlmErrorCodes.Auth, $"Authentication failed ({status}): {body}"),
            402 => new LlmException(LlmErrorCodes.Quota, $"Insufficient quota ({status}): {body}"),
            429 => new LlmException(LlmErrorCodes.RateLimit, $"Rate limited (429): {body}"),
            400 or 413 or 422 => new LlmException(LlmErrorCodes.InvalidRequest, $"Request rejected ({status}): {body}"),
            >= 500 => new LlmException(LlmErrorCodes.Server, $"Provider server error ({status}): {body}"),
            _ => new LlmException(LlmErrorCodes.Transport, $"Unexpected HTTP {status}: {body}"),
        };
    }
}

public static class LlmRetryPolicy
{
    public const int MaxRetries = 5;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

    public static TimeSpan DelayFor(int attempt, TimeSpan? retryAfter = null)
    {
        if (retryAfter is { } after && after > TimeSpan.Zero) return after;
        var ms = Math.Min(BaseDelay.TotalMilliseconds * Math.Pow(2, attempt), MaxDelay.TotalMilliseconds);
        var jitter = 1 + (Random.Shared.NextDouble() * 0.2 - 0.1);
        return TimeSpan.FromMilliseconds(ms * jitter);
    }
}