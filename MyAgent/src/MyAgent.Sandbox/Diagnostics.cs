namespace MyAgent.Sandbox;

/// <summary>
/// Evidence identifying a sandbox runner failing BEFORE it executes the wrapped command
/// (mirrors dsh-sandbox RunnerFailureRule). A consumer removes informational lines by
/// case-insensitive exact equality, then matches fatal signatures within each remaining
/// stderr line. Exit status alone never proves runner failure.
/// </summary>
public sealed record RunnerFailureRule(
    IReadOnlyList<string> FatalSignatures,
    IReadOnlyList<int>? AllowedExitCodes = null,
    IReadOnlyList<string>? InformationalLines = null);

/// <summary>Classify observed sandbox runner failures and file-effect denials (mirrors dsh-sandbox/diagnostics).</summary>
public static class SandboxDiagnostics
{
    /// <summary>
    /// Classify one settled process against the backend's structured runner-failure rules.
    /// Each rule requires a nonzero exit, its optional exit-code gate, and a fatal signature
    /// on one stderr line after exact informational lines are excluded. Returns the matching
    /// fatal line, or null when evidence is insufficient.
    /// </summary>
    public static string? ClassifyRunnerFailure(int? exitCode, string stderr, IReadOnlyList<RunnerFailureRule> rules)
    {
        if (exitCode is null or 0) return null;
        var lines = stderr.Replace("\r\n", "\n").Split('\n');
        foreach (var rule in rules)
        {
            if (rule.AllowedExitCodes is not null && !rule.AllowedExitCodes.Contains(exitCode.Value)) continue;
            var informational = new HashSet<string>(
                (rule.InformationalLines ?? []).Select(l => l.ToLowerInvariant()), StringComparer.Ordinal);
            var fatal = rule.FatalSignatures
                .Where(s => s.Trim().Length > 0)
                .Select(s => s.ToLowerInvariant())
                .ToList();
            foreach (var line in lines)
            {
                var lowered = line.ToLowerInvariant();
                if (informational.Contains(lowered)) continue;
                if (fatal.Any(lowered.Contains)) return line;
            }
        }
        return null;
    }

    /// <summary>
    /// Match a nonzero exit against case-insensitive stderr signatures identifying the
    /// backend's denial dialect. Returns whether this is a denied file effect.
    /// </summary>
    public static bool MatchesSignature(int? exitCode, string stderr, IReadOnlyList<string> signatures)
    {
        if (exitCode is null or 0) return false;
        var lowered = stderr.ToLowerInvariant();
        return signatures.Any(s => lowered.Contains(s.ToLowerInvariant()));
    }
}
