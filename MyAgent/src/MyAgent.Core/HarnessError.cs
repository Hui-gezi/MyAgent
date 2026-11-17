namespace MyAgent.Core;

public sealed class HarnessError(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}