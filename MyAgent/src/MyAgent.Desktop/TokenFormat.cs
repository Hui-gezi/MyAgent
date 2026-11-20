namespace MyAgent.Desktop;

/// <summary>
/// Token display math, ported 1:1 from the harness ui-chat token-format.ts /
/// message-chrome.ts / context-occupancy.ts. Pure functions so probes can pin
/// the exact display semantics.
/// </summary>
public static class TokenFormat
{
    // JS Math.round rounds half UP (2.5→3); C#'s default is banker's rounding — pin
    // AwayFromZero everywhere so the display math matches the harness exactly.
    private static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>Compact token count: 517 / 12.2K / 517K / 1.2M (formatTokens).</summary>
    public static string Tokens(long value)
    {
        string Scaled(double candidate) =>
            candidate >= 100 ? ((long)Round(candidate)).ToString() : (Round(candidate * 10) / 10).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        if (value < 1_000) return value.ToString();
        if (value < 1_000_000) return Scaled(value / 1_000.0) + "K";
        return Scaled(value / 1_000_000.0) + "M";
    }

    /// <summary>Exact integer token count with comma digit grouping (formatExactTokens).</summary>
    public static string Exact(long value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Decode-throughput figure: whole tokens from ten up, one decimal below
    /// (formatTokensPerSecond). Negative input clamps to zero.
    /// </summary>
    public static string Tps(double tps)
    {
        var clamped = Math.Max(0, tps);
        return clamped >= 10 ? ((long)Round(clamped)).ToString() : (Round(clamped * 10) / 10).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Display-ready cache-hit share WITHOUT rounding a partial hit to 100%
    /// (formatCacheHitPercent, default 0 decimal places). Returns null when there
    /// was no prompt input; a full hit returns "100"; a partial hit that would
    /// round to 100 gains extra precision (99.9, 99.99, …) to stay honest.
    /// </summary>
    public static string? CacheHitPercent(long cacheReadTokens, long promptTokens)
    {
        if (promptTokens == 0) return null;
        var missed = promptTokens - cacheReadTokens;
        if (missed <= 0) return "100"; // full hit (defensive: read cannot exceed billed input)

        // Rounded percentage units at 0 decimals (binary search equivalent of the
        // harness integer-only rounding: largest u with cacheRead >= ceil((2u-1)*D/200)).
        var units = RoundedUnits(cacheReadTokens, promptTokens);
        if (units < 100) return units.ToString();

        // Would round to 100 — find the minimum precision that still rounds below it.
        // Mirrors the harness scaled-double-gap loop: find distinguishingPlaces such that
        // missed*200*10^(places-1) > floor(D/10), then the last digit from the loss factor.
        var distinguishingPlaces = 1;
        var scaledDoubleGap = missed * 200;
        var denominatorTens = promptTokens / 10;
        while (scaledDoubleGap <= denominatorTens)
        {
            scaledDoubleGap *= 10;
            distinguishingPlaces += 1;
        }
        var denominatorOnes = promptTokens % 10;
        var roundedLoss = 5;
        for (var loss = 1; loss < 5; loss += 1)
        {
            var factor = loss * 2 + 1;
            var threshold = factor * denominatorTens + factor * denominatorOnes / 10;
            if (scaledDoubleGap <= threshold)
            {
                roundedLoss = loss;
                break;
            }
        }
        return "99." + new string('9', distinguishingPlaces - 1) + (10 - roundedLoss);
    }

    private static long RoundedUnits(long cacheRead, long denominator)
    {
        // Largest u in [0,100] with cacheRead*200 >= (2u-1)*denominator (positive ties up).
        long lower = 0, upper = 100;
        while (lower < upper)
        {
            var candidate = (lower + upper + 1) / 2;
            var factor = candidate * 2 - 1;
            // threshold = ceil(factor*D/200), computed without overflow for long ranges.
            var threshold = factor * (denominator / 200) + (factor * (denominator % 200) + 199) / 200;
            if (cacheRead >= threshold) lower = candidate;
            else upper = candidate - 1;
        }
        return lower;
    }

    /// <summary>Compact duration: 45.2s under a minute → "45.2秒"; from there "2分42秒" (formatDuration).</summary>
    public static string Duration(double ms)
    {
        var s = ms / 1_000;
        if (s < 60) return (Round(s * 10) / 10).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "秒";
        var whole = (long)Round(s);
        return $"{whole / 60}分{whole % 60}秒";
    }

    /// <summary>
    /// Bounded display occupancy (contextOccupancy): percent = min(100, round(used/window*100)).
    /// Returns null until both numerator and capacity are known.
    /// </summary>
    public static ContextOccupancy? Occupancy(long? usedTokens, long? contextWindow)
    {
        if (usedTokens is not { } used || contextWindow is not { } window || window <= 0) return null;
        return new ContextOccupancy(
            (int)Math.Min(100, Round(used / (double)window * 100)), used, window);
    }
}

public sealed record ContextOccupancy(int Percent, long UsedTokens, long ContextWindow);
