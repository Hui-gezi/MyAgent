using System.Windows;
using System.Windows.Media;
using MyAgent.Core;
using MyAgent.Llm;

namespace MyAgent.Desktop;

/// <summary>
/// Composer statistics dock below the input card (mirrors harness StatsPills +
/// ContextMeter): an activity pill (turn/step counts · decode speed), a usage pill
/// (whole-log token total · cache hit), and a ring context-occupancy meter. Each
/// opens its own click dialog; at most one dialog is open. Compact mode keeps only
/// the speed and cache-hit readings; Detailed (default) adds counts and dialogs.
/// Every figure folds from the durable log, so paging/compaction cannot change it.
/// </summary>
public sealed class ComposerStatsVm : ViewModelBase
{
    private const double RingRadius = 5.5;
    private const double RingCircumference = 2 * Math.PI * RingRadius;
    private const double BarWidth = 240; // context panel inner width (264 - 2*12 padding)

    private bool _detailed = true;

    // ---- activity pill ----
    public bool HasActivity { get => _hasActivity; private set => Set(ref _hasActivity, value); }
    private bool _hasActivity;
    public string ActivityLabel { get => _activityLabel; private set => Set(ref _activityLabel, value); }
    private string _activityLabel = "";
    public bool HasActivityDialog { get => _hasActivityDialog; private set => Set(ref _hasActivityDialog, value); }
    private bool _hasActivityDialog;
    public string ActivityCounts { get => _activityCounts; private set => Set(ref _activityCounts, value); }
    private string _activityCounts = "";
    public bool HasLlmTime { get => _hasLlmTime; private set => Set(ref _hasLlmTime, value); }
    private bool _hasLlmTime;
    public string LlmTimeText { get => _llmTimeText; private set => Set(ref _llmTimeText, value); }
    private string _llmTimeText = "";
    public bool HasToolTime { get => _hasToolTime; private set => Set(ref _hasToolTime, value); }
    private bool _hasToolTime;
    public string ToolTimeText { get => _toolTimeText; private set => Set(ref _toolTimeText, value); }
    private string _toolTimeText = "";
    public bool HasTtft { get => _hasTtft; private set => Set(ref _hasTtft, value); }
    private bool _hasTtft;
    public string TtftText { get => _ttftText; private set => Set(ref _ttftText, value); }
    private string _ttftText = "";
    public bool HasSpeed { get => _hasSpeed; private set => Set(ref _hasSpeed, value); }
    private bool _hasSpeed;
    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }
    private string _speedText = "";

    // ---- usage pill ----
    public bool HasUsage { get => _hasUsage; private set => Set(ref _hasUsage, value); }
    private bool _hasUsage;
    public string UsageLabel { get => _usageLabel; private set => Set(ref _usageLabel, value); }
    private string _usageLabel = "";
    public string UsageTotalExact { get => _usageTotalExact; private set => Set(ref _usageTotalExact, value); }
    private string _usageTotalExact = "";
    public bool HasCacheHit { get => _hasCacheHit; private set => Set(ref _hasCacheHit, value); }
    private bool _hasCacheHit;
    public string CacheHitValue { get => _cacheHitValue; private set => Set(ref _cacheHitValue, value); }
    private string _cacheHitValue = "";
    public string InputExact { get => _inputExact; private set => Set(ref _inputExact, value); }
    private string _inputExact = "";
    public string CacheReadExact { get => _cacheReadExact; private set => Set(ref _cacheReadExact, value); }
    private string _cacheReadExact = "";
    public bool HasCacheWrite { get => _hasCacheWrite; private set => Set(ref _hasCacheWrite, value); }
    private bool _hasCacheWrite;
    public string CacheWriteExact { get => _cacheWriteExact; private set => Set(ref _cacheWriteExact, value); }
    private string _cacheWriteExact = "";
    public string OutputExact { get => _outputExact; private set => Set(ref _outputExact, value); }
    private string _outputExact = "";
    /// <summary>provider · model footer inside the usage dialog (harness TurnUsagePanel header).</summary>
    public string ProviderMeta { get => _providerMeta; set => Set(ref _providerMeta, value); }
    private string _providerMeta = "";

    // ---- context meter ----
    public bool HasContext { get => _hasContext; private set => Set(ref _hasContext, value); }
    private bool _hasContext;
    public string ContextPercentText { get => _contextPercentText; private set => Set(ref _contextPercentText, value); }
    private string _contextPercentText = "";
    public string ContextTooltip { get => _contextTooltip; private set => Set(ref _contextTooltip, value); }
    private string _contextTooltip = "";
    public DoubleCollection RingDashArray { get => _ringDashArray; private set => Set(ref _ringDashArray, value); }
    private DoubleCollection _ringDashArray = new() { 0, RingCircumference };
    public string ContextFigures { get => _contextFigures; private set => Set(ref _contextFigures, value); }
    private string _contextFigures = "";
    public bool HasBreakdown { get => _hasBreakdown; private set => Set(ref _hasBreakdown, value); }
    private bool _hasBreakdown;
    public string SystemTokensText { get => _systemTokensText; private set => Set(ref _systemTokensText, value); }
    private string _systemTokensText = "";
    public string ToolsTokensText { get => _toolsTokensText; private set => Set(ref _toolsTokensText, value); }
    private string _toolsTokensText = "";
    public string MessagesTokensText { get => _messagesTokensText; private set => Set(ref _messagesTokensText, value); }
    private string _messagesTokensText = "";
    public double SystemBarWidth { get => _systemBarWidth; private set => Set(ref _systemBarWidth, value); }
    private double _systemBarWidth;
    public double ToolsBarWidth { get => _toolsBarWidth; private set => Set(ref _toolsBarWidth, value); }
    private double _toolsBarWidth;
    public double MessagesBarWidth { get => _messagesBarWidth; private set => Set(ref _messagesBarWidth, value); }
    private double _messagesBarWidth;
    public double PlainBarWidth { get => _plainBarWidth; private set => Set(ref _plainBarWidth, value); }
    private double _plainBarWidth;

    // ---- dialogs (at most one open) ----
    public bool IsActivityOpen
    {
        get => _isActivityOpen;
        set { if (Set(ref _isActivityOpen, value) && value) { IsUsageOpen = false; IsContextOpen = false; } }
    }
    private bool _isActivityOpen;
    public bool IsUsageOpen
    {
        get => _isUsageOpen;
        set { if (Set(ref _isUsageOpen, value) && value) { IsActivityOpen = false; IsContextOpen = false; } }
    }
    private bool _isUsageOpen;
    public bool IsContextOpen
    {
        get => _isContextOpen;
        set { if (Set(ref _isContextOpen, value) && value) { IsActivityOpen = false; IsUsageOpen = false; } }
    }
    private bool _isContextOpen;

    public void SetDetailed(bool detailed)
    {
        _detailed = detailed;
        if (!detailed)
        {
            // Compact readings are static spans: no dialogs stay open.
            IsActivityOpen = IsUsageOpen = false;
        }
    }

    /// <summary>Compact keeps a static speed reading; only Detailed opens the stats dialog.</summary>
    public void ToggleActivity()
    {
        if (_detailed && HasActivityDialog) IsActivityOpen = !IsActivityOpen;
    }

    /// <summary>Compact keeps a static cache-hit reading; only Detailed opens the usage dialog.</summary>
    public void ToggleUsage()
    {
        if (_detailed) IsUsageOpen = !IsUsageOpen;
    }

    /// <summary>The context meter carries its panel in both display modes.</summary>
    public void ToggleContext() => IsContextOpen = !IsContextOpen;

    /// <summary>Re-fold every figure from the durable log (called on session events / selection).
    /// The occupancy meter uses the configured model window, not a hardcoded constant.</summary>
    public void Refresh(Session session, int contextWindow)
    {
        var events = session.Events; // one stable snapshot
        RefreshActivity(SessionStats.Fold(events));
        RefreshUsage(TokenUsageTotals.Fold(events));
        RefreshContext(ContextPressure.Fold(events, contextWindow));
    }

    public void Clear()
    {
        HasActivity = HasUsage = HasContext = false;
        IsActivityOpen = IsUsageOpen = IsContextOpen = false;
    }

    private void RefreshActivity(SessionStats stats)
    {
        var speed = stats.DecodeMs > 0
            ? TokenFormat.Tps(stats.DecodeTokens / (stats.DecodeMs / 1000.0)) + " tok/s"
            : null;
        var counts = $"{stats.Turns} 轮 {stats.Steps} 步";
        ActivityCounts = counts;
        HasSpeed = speed is not null;
        SpeedText = speed ?? "";
        HasLlmTime = stats.LlmMs > 0;
        LlmTimeText = TokenFormat.Duration(stats.LlmMs);
        HasToolTime = stats.ToolMs > 0;
        ToolTimeText = TokenFormat.Duration(stats.ToolMs);
        HasTtft = stats.TtftSteps > 0;
        TtftText = stats.TtftSteps > 0 ? TokenFormat.Duration(stats.TtftMs / (double)stats.TtftSteps) : "";
        HasActivityDialog = HasLlmTime || HasToolTime || HasTtft || HasSpeed;

        if (!_detailed)
        {
            // Compact: speed reading only; hidden without a timed decode.
            HasActivity = speed is not null;
            ActivityLabel = speed ?? "";
            return;
        }
        HasActivity = stats.Steps > 0;
        ActivityLabel = speed is null ? counts : $"{counts} · {speed}";
    }

    private void RefreshUsage(TokenUsageTotals usage)
    {
        var billed = usage.BilledInput;
        // Gated on actual token activity: a session whose steps all settled without
        // billing shows no usage pill.
        if (billed == 0 && usage.OutputTokens == 0)
        {
            HasUsage = false;
            HasCacheHit = false;
            return;
        }
        var hit = TokenFormat.CacheHitPercent(usage.CacheReadTokens, billed);
        HasCacheHit = hit is not null;
        CacheHitValue = hit is null ? "" : $"{hit}%";
        var hitText = hit is null ? null : $"缓存命中 {hit}%";
        var total = usage.Total;
        UsageTotalExact = $"{TokenFormat.Exact(total)} tok";
        InputExact = $"{TokenFormat.Exact(usage.UncachedInputTokens)} tok";
        CacheReadExact = $"{TokenFormat.Exact(usage.CacheReadTokens)} tok";
        HasCacheWrite = usage.CacheWriteTokens != 0;
        CacheWriteExact = $"{TokenFormat.Exact(usage.CacheWriteTokens)} tok";
        OutputExact = $"{TokenFormat.Exact(usage.OutputTokens)} tok";

        HasUsage = true;
        if (!_detailed)
        {
            // Compact: cache-hit reading only.
            HasUsage = hitText is not null;
            UsageLabel = hitText ?? "";
            return;
        }
        var totalText = $"{TokenFormat.Tokens(total)} tok";
        UsageLabel = hitText is null ? totalText : $"{totalText} · {hitText}";
    }

    private void RefreshContext(ContextPressure? pressure)
    {
        var occupancy = TokenFormat.Occupancy(pressure?.UsedTokens, pressure?.ContextWindow);
        if (occupancy is null)
        {
            HasContext = false;
            IsContextOpen = false;
            return;
        }
        HasContext = true;
        var percent = occupancy.Percent;
        ContextPercentText = $"{percent}%";
        ContextTooltip = $"上下文已用 {percent}%";
        // WPF StrokeDashArray units are pen-thickness multiples (stroke = 2px).
        RingDashArray = new DoubleCollection { RingCircumference * percent / 100 / 2, RingCircumference };
        ContextFigures = $"~{TokenFormat.Tokens(occupancy.UsedTokens)} / {TokenFormat.Tokens(occupancy.ContextWindow)}";

        var breakdownTotal = pressure!.SystemTokens + pressure.ToolsTokens + pressure.MessageTokens;
        HasBreakdown = breakdownTotal > 0;
        if (HasBreakdown)
        {
            SystemTokensText = $"~{TokenFormat.Tokens(pressure.SystemTokens)}";
            ToolsTokensText = $"~{TokenFormat.Tokens(pressure.ToolsTokens)}";
            MessagesTokensText = $"~{TokenFormat.Tokens(pressure.MessageTokens)}";
            // The bar's overall length stays the provider-exact percent; the heuristic
            // breakdown only proportions its colored parts. Zero-width parts drop out.
            double Part(long tokens) => tokens <= 0 ? 0 : Math.Max(2, BarWidth * percent / 100 * tokens / breakdownTotal);
            SystemBarWidth = Part(pressure.SystemTokens);
            ToolsBarWidth = Part(pressure.ToolsTokens);
            MessagesBarWidth = Part(pressure.MessageTokens);
            PlainBarWidth = 0;
        }
        else
        {
            SystemBarWidth = ToolsBarWidth = MessagesBarWidth = 0;
            PlainBarWidth = percent <= 0 ? 0 : Math.Max(2, BarWidth * percent / 100);
        }
    }
}
