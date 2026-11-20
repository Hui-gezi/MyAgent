namespace MyAgent.Core;

/// <summary>
/// Delivery seam used by host-side features (schedule, workflow) to inject a prompt into a
/// session and drive a turn. Implemented by the application layer (SessionManager) so that
/// feature projects only depend on Core.
/// </summary>
public interface ISessionDispatcher
{
    /// <summary>All currently known session ids (including persisted but inactive ones).</summary>
    IReadOnlyList<string> ListSessionIds();

    /// <summary>Append a user message to the target session and start a turn.</summary>
    void Deliver(string sessionId, string prompt, string source);

    /// <summary>Create a session in the given working directory; returns its id.</summary>
    string CreateSession(string? cwd);
}

/// <summary>
/// A no-op dispatcher used when a feature is exercised without a host (tests, tooling).
/// </summary>
public sealed class NullSessionDispatcher : ISessionDispatcher
{
    public IReadOnlyList<string> ListSessionIds() => [];
    public void Deliver(string sessionId, string prompt, string source) { }
    public string CreateSession(string? cwd) => $"s_{Guid.NewGuid():N}";
}

/// <summary>
/// Result of an after-turn hook. Features return a continuation prompt to admit another turn,
/// or null to let the run idle.
/// </summary>
public sealed record AfterTurnAction(string? Prompt, string? Source = null);
