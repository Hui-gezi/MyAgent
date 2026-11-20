using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using MyAgent.App;
using MyAgent.Approval;
using MyAgent.Core;
using MyAgent.Jobs;
using MyAgent.Permissions;
using MyAgent.Sandbox;
using MyAgent.Sessions;

namespace MyAgent.Desktop;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/* ===== chat items ===== */
public abstract class ChatItem : ViewModelBase;

public sealed class UserItem : ChatItem
{
    public required string Text { get; init; }
}

public sealed class CompactionItem : ChatItem;

/// <summary>A durable-looking error notice inside the transcript (agent/compaction failures).</summary>
public sealed class ErrorItem : ChatItem
{
    public required string Text { get; init; }
}

public sealed class ProcessGroupItem : ChatItem
{
    private bool _isEnded;
    private bool _isCollapsed;
    private bool _isUserCollapsed;
    private bool _hasLiveAssistant;
    private DateTimeOffset? _endTime;
    private string? _stopReason;

    public string? TurnId { get; init; }
    public DateTimeOffset StartTime { get; init; } = DateTimeOffset.UtcNow;
    public ObservableCollection<ChatItem> Children { get; } = [];
    public ObservableCollection<ChatItem> ReplyChildren { get; } = [];

    public bool IsEnded { get => _isEnded; set { if (Set(ref _isEnded, value)) { Raise(nameof(ShowHeader)); Raise(nameof(ShowRunning)); Raise(nameof(HeaderLabel)); } } }
    public bool IsCollapsed { get => _isCollapsed; set { if (Set(ref _isCollapsed, value)) Raise(nameof(ShowBody)); } }
    public bool IsUserCollapsed { get => _isUserCollapsed; set { if (Set(ref _isUserCollapsed, value)) { Raise(nameof(ShowBody)); Raise(nameof(ShowReplies)); } } }
    public bool HasLiveAssistant { get => _hasLiveAssistant; set { if (Set(ref _hasLiveAssistant, value)) Raise(nameof(ShowRunning)); } }
    public DateTimeOffset? EndTime { get => _endTime; set { if (Set(ref _endTime, value)) Raise(nameof(HeaderLabel)); } }
    public string? StopReason { get => _stopReason; set { if (Set(ref _stopReason, value)) Raise(nameof(HeaderLabel)); } }

    public bool ShowHeader => IsEnded;
    public bool ShowBody => !IsCollapsed && !IsUserCollapsed;
    public bool ShowReplies => !IsUserCollapsed;
    public bool ShowRunning => !IsEnded && !HasLiveAssistant;

    public string HeaderLabel
    {
        get
        {
            if (StopReason == "cancelled") return "已停止";
            if (StopReason == "error") return "执行出错";
            if (StopReason == "interrupted") return "已中断";
            var seconds = EndTime is { } end ? Math.Max(1, (int)Math.Round((end - StartTime).TotalSeconds)) : 0;
            return $"Worked for {seconds}s";
        }
    }
}

public sealed class AssistantItem : ChatItem
{
    public ObservableCollection<ChatItem> Blocks { get; } = [];
    public bool IsStreaming { get; init; }
}

public sealed class ReasoningItem : ChatItem
{
    private string _text = "";
    private bool _isOpen;

    public bool IsStreaming { get; init; }

    public string Text
    {
        get => _text;
        set { if (Set(ref _text, value)) Raise(nameof(Summary)); }
    }

    public bool IsOpen { get => _isOpen; set => Set(ref _isOpen, value); }

    public string Summary
    {
        get
        {
            var firstLine = Text.Split('\n')[0];
            if (firstLine.Length == 0) firstLine = "思考中…";
            return IsStreaming ? "思考中… " + firstLine : firstLine;
        }
    }
}

public sealed class MarkdownItem : ChatItem
{
    private string _text = "";
    public string Text { get => _text; set => Set(ref _text, value); }
}

public sealed class ToolItem : ChatItem
{
    private ToolResultData? _result;
    private bool _isOpen;
    private string _argsJson = "{}";
    private string _brief = "";

    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsStreaming { get; init; }
    public string RawArgsBuffer { get; set; } = "";

    public string ArgsJson { get => _argsJson; set => Set(ref _argsJson, value); }
    public string Brief { get => _brief; set => Set(ref _brief, value); }
    public bool IsOpen { get => _isOpen; set => Set(ref _isOpen, value); }

    public ToolResultData? Result
    {
        get => _result;
        set
        {
            if (Set(ref _result, value))
            {
                Raise(nameof(State));
                Raise(nameof(HasResult));
                Raise(nameof(ResultLabel));
                Raise(nameof(ResultContent));
                Raise(nameof(ResultIsError));
            }
        }
    }

    public string State => Result is null ? "ongoing" : Result.IsError ? "error" : "done";
    public bool HasResult => Result is not null;
    public string ResultLabel => Result?.IsError == true ? "错误" : "结果";
    public string ResultContent => Result?.Content ?? "";
    public bool ResultIsError => Result?.IsError == true;

    public void SetArgs(JsonElement args)
    {
        ArgsJson = args.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? JsonSerializer.Serialize(args, ArgsJsonOptions)
            : args.GetRawText();
        Brief = ToolBrief(Name, args);
    }

    private static readonly JsonSerializerOptions ArgsJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void SetRawArgs(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            SetArgs(doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            ArgsJson = raw;
            Brief = raw.Length > 120 ? raw[..120] : raw;
        }
    }

    private static string ToolBrief(string name, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return "";
        string? Str(string key) => args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        switch (name)
        {
            case "bash": return Str("command") ?? "";
            case "read": case "write": case "edit": return Str("file_path") ?? "";
            case "glob": case "grep": return Str("pattern") ?? "";
            case "todo_write":
                return args.TryGetProperty("todos", out var todos) && todos.ValueKind == JsonValueKind.Array
                    ? todos.GetArrayLength() + " 项任务" : "";
            default:
                foreach (var property in args.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString() ?? "";
                    break;
                }
                var raw = args.GetRawText();
                return raw.Length > 120 ? raw[..120] : raw;
        }
    }
}

/* ===== sidebar / panels ===== */
public sealed class WorkspaceGroupVm : ViewModelBase
{
    private bool _isExpanded = true;

    public required string Name { get; init; }
    public string? Path { get; init; }
    public bool IsUngrouped => Path is null;
    public ObservableCollection<SessionRowVm> Children { get; } = [];
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
}

public sealed record WorkspacePickVm(WorkspaceEntry Entry, bool IsCurrent, string Display);

public sealed class SessionRowVm : ViewModelBase
{
    private bool _isActive;
    private bool _running;
    private string _title = "";

    public required string Id { get; init; }
    public required string Cwd { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public string Title { get => _title; set => Set(ref _title, value); }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public bool Running { get => _running; set => Set(ref _running, value); }
    public string TimeText => RelativeTime(UpdatedAt);

    public static string RelativeTime(DateTimeOffset time)
    {
        var diff = DateTimeOffset.UtcNow - time;
        if (diff.TotalSeconds < 60) return "刚刚";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} 分钟前";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} 小时前";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} 天前";
        return time.LocalDateTime.ToString("yyyy/M/d");
    }
}

public sealed class TodoVm
{
    public required string Content { get; init; }
    public required string Status { get; init; }
    public bool IsCompleted => Status == "completed";
    public string State => Status switch { "completed" => "done", "in_progress" => "ongoing", _ => "" };
}

/* ===== feature panels (goal / plan / team / workflow / schedule) ===== */
public sealed class GoalPanelVm : ViewModelBase
{
    private string _objective = "尚未设定目标";
    private string _phase = "";
    private string _progress = "";
    private bool _hasGoal;
    public bool HasGoal { get => _hasGoal; set => Set(ref _hasGoal, value); }
    public string Objective { get => _objective; set => Set(ref _objective, value); }
    public string Phase { get => _phase; set => Set(ref _phase, value); }
    public string Progress { get => _progress; set => Set(ref _progress, value); }
}

public sealed class TeamTaskRowVm : ViewModelBase
{
    public required string Id { get; init; }
    public required string Subject { get; init; }
    public required string Status { get; init; }
    public bool Ready { get; init; }
    public string Owner { get; init; } = "";
    public string StatusLabel => Status switch
    {
        "in_progress" => "进行中",
        "completed" => "已完成",
        _ => "待办",
    };
}

public sealed class ScheduleRowVm : ViewModelBase
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Kind { get; init; }
    public required string Next { get; init; }
    public required string Status { get; init; }
}

public sealed class WorkflowRowVm : ViewModelBase
{
    public required string RunId { get; init; }
    public required string Name { get; init; }
    public required string StopReason { get; init; }
    public required int AgentsStarted { get; init; }
    public required List<string> Phases { get; init; }
    public required int AgentCount { get; init; }
}

public sealed class JobRowVm : ViewModelBase
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Status { get; init; }
    public required long OutputBytes { get; init; }
    public string StatusLabel => Status switch
    {
        "running" => "运行中",
        "stopping" => "停止中",
        "completed" => "已完成",
        "killed" => "已终止",
        _ => "失败",
    };
}

public sealed class AgentRowVm : ViewModelBase
{
    public required string Name { get; init; }
    public required string Status { get; init; }
    public required string Description { get; init; }
}

/// <summary>One selectable permission preset (mirrors ui-permission-presets PresetOption).</summary>
public sealed class PresetOptionVm
{
    public required string Value { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public override string ToString() => Name;
}

public sealed class FileEntryVm : ViewModelBase
{
    private bool _isExpanded;
    private bool _loaded;

    public required string Name { get; init; }
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
    public required Action<FileEntryVm> Toggle { get; init; }
    public ObservableCollection<FileEntryVm> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value) && value && !_loaded)
            {
                _loaded = true;
                Toggle(this);
            }
        }
    }
}

public sealed class ToastVm : ViewModelBase
{
    public required string Message { get; init; }
    public required bool IsError { get; init; }
}

public sealed class ModelOptionVm
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public override string ToString() => Name;
}

/* ===== chat view model ===== */
public sealed class ChatViewModel : ViewModelBase
{
    private bool _isRunning;
    private bool _isCompacting;
    private string _title = "New Session";
    private string _cwd = "";
    private AssistantItem? _live;
    private ProcessGroupItem? _liveGroup;
    private readonly Dictionary<int, ToolItem> _liveTools = [];

    public ObservableCollection<ChatItem> Items { get; } = [];
    public ObservableCollection<TodoVm> Todos { get; } = [];

    public bool IsRunning { get => _isRunning; set { if (Set(ref _isRunning, value)) { Raise(nameof(IsEmpty)); if (!value) IsCompacting = false; } } }
    /// <summary>A checkpoint summarization call is in flight — the run is busy, not frozen.</summary>
    public bool IsCompacting { get => _isCompacting; set => Set(ref _isCompacting, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Cwd { get => _cwd; set => Set(ref _cwd, value); }
    public bool HasTodos => Todos.Count > 0;
    public bool IsEmpty => Items.Count == 0 && _live is null;

    /* ===== todo summary（输入区上方折叠为一行摘要，完整清单在右栏"任务"页） ===== */
    private bool _isTodoExpanded;
    /// <summary>对话区清单默认折叠；展开后限高内滚，长清单不再挤压对话区。</summary>
    public bool IsTodoExpanded { get => _isTodoExpanded; set => Set(ref _isTodoExpanded, value); }
    public int TodoDoneCount => Todos.Count(t => t.IsCompleted);
    /// <summary>进度徽标，如 "3/8"。</summary>
    public string TodoProgress => $"{TodoDoneCount}/{Todos.Count}";
    /// <summary>当前进行中的任务；没有则取第一个待办；全部完成时为空。</summary>
    public string CurrentTodoText =>
        (Todos.FirstOrDefault(t => t.Status == "in_progress")
         ?? Todos.FirstOrDefault(t => !t.IsCompleted))?.Content ?? "";
    public bool HasCurrentTodo => CurrentTodoText.Length > 0;

    private void RaiseTodoSummary()
    {
        Raise(nameof(HasTodos));
        Raise(nameof(TodoDoneCount));
        Raise(nameof(TodoProgress));
        Raise(nameof(CurrentTodoText));
        Raise(nameof(HasCurrentTodo));
    }

    public ChatViewModel()
    {
        Items.CollectionChanged += (_, _) => Raise(nameof(IsEmpty));
        Todos.CollectionChanged += (_, _) => RaiseTodoSummary();
    }

    public void LoadSnapshot(IEnumerable<SessionEvent> events, bool running, string? title, string cwd)
    {
        Items.Clear();
        Todos.Clear();
        ClearLive();
        _title = title ?? "New Session";
        _cwd = cwd;
        Raise(nameof(Title));
        Raise(nameof(Cwd));
        foreach (var evt in events) Fold(evt);
        IsRunning = running;
        // 旧版本持久化的会话可能在结束后残留 in_progress（当时没有回合收尾机制）。
        // 会话已不在运行，展示层把这些项收尾为 completed，避免任务清单永远显示“进行中”。
        if (!running && Todos.Any(t => t.Status == "in_progress"))
        {
            var settled = Todos
                .Select(t => t.Status == "in_progress"
                    ? new TodoVm { Content = t.Content, Status = "completed" }
                    : t)
                .ToList();
            Todos.Clear();
            foreach (var todo in settled) Todos.Add(todo);
        }
    }

    private ProcessGroupItem CurrentGroup()
    {
        for (var i = Items.Count - 1; i >= 0; i--)
            if (Items[i] is ProcessGroupItem { IsEnded: false } group) return group;
        var fresh = new ProcessGroupItem();
        Items.Add(fresh);
        return fresh;
    }

    public void Fold(SessionEvent evt)
    {
        var data = evt.Data;
        switch (evt.Type)
        {
            case SessionEventTypes.TurnStart:
                Items.Add(new ProcessGroupItem
                {
                    TurnId = data.TryGetProperty("turnId", out var tid) ? tid.GetString() : null,
                    StartTime = evt.Time,
                });
                break;
            case SessionEventTypes.TurnEnd:
            {
                var turnId = data.TryGetProperty("turnId", out var endTid) ? endTid.GetString() : null;
                var group = Items.OfType<ProcessGroupItem>().LastOrDefault(g => g.TurnId == turnId);
                if (group is not null)
                {
                    group.EndTime = evt.Time;
                    group.StopReason = data.TryGetProperty("stopReason", out var sr) ? sr.GetString() : null;
                    group.IsEnded = true;
                }
                ClearLive();
                break;
            }
            case SessionEventTypes.UserMessage:
            {
                // System notices (plan-mode narration, approval-policy notice, goal
                // continuation prompts) stay in the durable log for the MODEL, but the
                // transcript renders only user-authored bubbles (mirrors harness: the
                // conversation view filters non-user message sources).
                if (data.TryGetProperty("source", out var src) && Session.IsNoticeSource(src.GetString()))
                    break;
                var text = data.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                Items.Add(new UserItem { Text = text });
                if (Title == "New Session" && text.Length > 0)
                    Title = text.Length > 60 ? text[..60] + "…" : text;
                break;
            }
            case SessionEventTypes.AssistantMessage:
            {
                ClearLive();
                var message = data.Deserialize<AssistantMessageData>(Session.JsonOptions);
                var item = new AssistantItem();
                foreach (var block in message?.Blocks ?? [])
                {
                    switch (block)
                    {
                        case ThinkingBlock thinking:
                            item.Blocks.Add(new ReasoningItem { Text = thinking.Text });
                            break;
                        case TextBlock text:
                            item.Blocks.Add(new MarkdownItem { Text = text.Text });
                            break;
                    }
                }
                var isReply = item.Blocks.Count > 0 && !(message?.Blocks ?? []).OfType<ToolUseBlock>().Any();
                if (isReply) CurrentGroup().ReplyChildren.Add(item);
                else if (item.Blocks.Count > 0) CurrentGroup().Children.Add(item);
                break;
            }
            case SessionEventTypes.ToolCall:
            {
                var tool = new ToolItem
                {
                    Id = data.TryGetProperty("toolCallId", out var id) ? id.GetString() : null,
                    Name = data.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                };
                if (data.TryGetProperty("args", out var args)) tool.SetArgs(args);
                CurrentGroup().Children.Add(tool);
                break;
            }
            case SessionEventTypes.ToolResult:
            {
                var result = data.Deserialize<ToolResultData>(Session.JsonOptions);
                if (result is null) break;
                foreach (var group in Items.OfType<ProcessGroupItem>().Reverse())
                {
                    var tool = group.Children.OfType<ToolItem>().FirstOrDefault(t => t.Id == result.ToolCallId);
                    if (tool is not null) { tool.Result = result; break; }
                }
                break;
            }
            case SessionEventTypes.Compaction:
                Items.Add(new CompactionItem());
                break;
            case SessionEventTypes.TodoUpdate:
            {
                Todos.Clear();
                if (data.TryGetProperty("todos", out var todos) && todos.ValueKind == JsonValueKind.Array)
                {
                    foreach (var todo in todos.EnumerateArray())
                    {
                        Todos.Add(new TodoVm
                        {
                            Content = todo.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                            Status = todo.TryGetProperty("status", out var s) ? s.GetString() ?? "pending" : "pending",
                        });
                    }
                }
                break;
            }
            case SessionEventTypes.Rename:
                if (data.TryGetProperty("title", out var title))
                    Title = title.GetString() ?? "New Session";
                break;
        }
    }

    public void ApplyChunk(StreamChunk chunk)
    {
        switch (chunk)
        {
            case ReasoningDelta reasoning:
            {
                var live = EnsureLive();
                var item = live.Blocks.OfType<ReasoningItem>().FirstOrDefault();
                if (item is null)
                {
                    item = new ReasoningItem { IsStreaming = true, IsOpen = true };
                    live.Blocks.Insert(0, item);
                }
                item.Text += reasoning.Text;
                break;
            }
            case TextDelta text:
            {
                var live = EnsureLive();
                var item = live.Blocks.OfType<MarkdownItem>().FirstOrDefault();
                if (item is null)
                {
                    item = new MarkdownItem();
                    live.Blocks.Add(item);
                }
                item.Text += text.Text;
                break;
            }
            case ToolCallDelta tool:
            {
                var live = EnsureLive();
                if (!_liveTools.TryGetValue(tool.Index, out var item))
                {
                    item = new ToolItem { IsStreaming = true };
                    _liveTools[tool.Index] = item;
                    live.Blocks.Add(item);
                }
                if (tool.Id is not null) item.Id = tool.Id;
                if (tool.Name is not null) item.Name = tool.Name;
                if (tool.PartialJson is not null)
                {
                    item.RawArgsBuffer += tool.PartialJson;
                    item.SetRawArgs(item.RawArgsBuffer);
                }
                break;
            }
        }
    }

    private AssistantItem EnsureLive()
    {
        if (_live is not null) return _live;
        _liveGroup = CurrentGroup();
        _live = new AssistantItem { IsStreaming = true };
        _liveTools.Clear();
        _liveGroup.Children.Add(_live);
        _liveGroup.HasLiveAssistant = true;
        Raise(nameof(IsEmpty));
        return _live;
    }

    private void ClearLive()
    {
        if (_live is null) return;
        _liveGroup?.Children.Remove(_live);
        if (_liveGroup is not null) _liveGroup.HasLiveAssistant = false;
        _live = null;
        _liveGroup = null;
        _liveTools.Clear();
        Raise(nameof(IsEmpty));
    }
}

/* ===== main view model ===== */
public sealed class MainViewModel : ViewModelBase
{
    private readonly SessionManager _manager;
    private readonly SessionStore _store;
    private readonly SettingsService _settings;
    private readonly WorkspaceService _workspaces;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;

    private ChatViewModel? _currentChat;
    private string? _currentId;
    private bool _isSidebarCollapsed;
    private bool _isRightbarOpen;
    private string? _selectedModel;
    private WorkspaceEntry? _selectedWorkspace;
    private string _sessionFilter = "";
    private bool _isSearchOpen;

    public ObservableCollection<WorkspaceGroupVm> Sessions { get; } = [];
    public ObservableCollection<WorkspaceEntry> Workspaces { get; } = [];
    public ObservableCollection<WorkspacePickVm> PickerItems { get; } = [];
    public ObservableCollection<FileEntryVm> FileTree { get; } = [];
    public ObservableCollection<ToastVm> Toasts { get; } = [];
    public ObservableCollection<ModelOptionVm> Models { get; } = [];

    // Feature panels
    public GoalPanelVm Goal { get; } = new();

    // Composer statistics dock (mirrors StatsPills + ContextMeter below the input card).
    public ComposerStatsVm ComposerStats { get; } = new();
    public ObservableCollection<TeamTaskRowVm> TeamTasks { get; } = [];
    public ObservableCollection<ScheduleRowVm> Schedules { get; } = [];
    public ObservableCollection<WorkflowRowVm> Workflows { get; } = [];
    public ObservableCollection<JobRowVm> JobList { get; } = [];
    public ObservableCollection<AgentRowVm> Agents { get; } = [];
    public bool IsPlanActive { get => _isPlanActive; private set => Set(ref _isPlanActive, value); }
    private bool _isPlanActive;
    /// <summary>Effective target: a pending selection flips the logged state (harness
    /// PlanChip renders `pending ? !active : active`, a folded host value, not optimism).</summary>
    public bool IsPlanTargetActive { get => _isPlanTargetActive; private set => Set(ref _isPlanTargetActive, value); }
    private bool _isPlanTargetActive;
    /// <summary>A selection awaits the next accepted in-turn pre-step.</summary>
    public bool IsPlanPending { get => _isPlanPending; private set => Set(ref _isPlanPending, value); }
    private bool _isPlanPending;
    public string PlanStatusText => IsPlanPending
        ? (IsPlanTargetActive ? "计划模式：开启（下一步生效）" : "计划模式：关闭（下一步生效）")
        : (IsPlanTargetActive ? "计划模式：开启" : "计划模式：关闭");
    public bool IsTeamEnabled { get => _isTeamEnabled; private set => Set(ref _isTeamEnabled, value); }
    private bool _isTeamEnabled;

    // Pending plan review (mirrors PlanReviewPanel)
    public string? PendingQuestionId
    {
        get => _pendingQuestionId;
        private set { if (Set(ref _pendingQuestionId, value)) Raise(nameof(HasPendingReview)); }
    }
    private string? _pendingQuestionId;
    public string? ReviewPlanText { get => _reviewPlanText; private set => Set(ref _reviewPlanText, value); }
    private string? _reviewPlanText;
    public string? ReviewQuestion { get => _reviewQuestion; private set => Set(ref _reviewQuestion, value); }
    private string? _reviewQuestion;
    public bool HasPendingReview => PendingQuestionId is not null;

    // Pending sandbox escalation approval (mirrors ui-approval ApprovalPanel).
    public string? PendingApprovalId
    {
        get => _pendingApprovalId;
        private set { if (Set(ref _pendingApprovalId, value)) Raise(nameof(HasPendingApproval)); }
    }
    private string? _pendingApprovalId;
    public string? ApprovalToolName { get => _approvalToolName; private set => Set(ref _approvalToolName, value); }
    private string? _approvalToolName;
    public string? ApprovalReason { get => _approvalReason; private set => Set(ref _approvalReason, value); }
    private string? _approvalReason;
    public bool HasPendingApproval => PendingApprovalId is not null;

    // Permission preset selector (mirrors ui-permission-presets PermissionSelect).
    public ObservableCollection<PresetOptionVm> PermissionOptions { get; } = [];
    public string? SelectedPermission
    {
        get => _selectedPermission;
        set
        {
            if (!Set(ref _selectedPermission, value)) return;
            if (value is null || value == PermissionPresetService.CustomPreset) return;
            var live = CurrentId is null ? null : _manager.Get(CurrentId);
            if (live is null) return;
            try { _manager.Permissions.SetForAgent(live.Agent, value); }
            catch (HarnessError ex) { Toast(ex.Message, true); }
            RefreshPermissionSelection();
        }
    }
    private string? _selectedPermission;

    public HarnessSettings Settings => _settings.Current;

    /// <summary>是否有任何对话正在运行（含后台/子代理会话）。设置页据此拦截数据目录修改。</summary>
    public bool AnySessionRunning => _manager.AnySessionRunning;

    public ChatViewModel? CurrentChat { get => _currentChat; private set => Set(ref _currentChat, value); }
    public string? CurrentId { get => _currentId; private set => Set(ref _currentId, value); }
    public bool IsSidebarCollapsed { get => _isSidebarCollapsed; set => Set(ref _isSidebarCollapsed, value); }
    public bool IsRightbarOpen { get => _isRightbarOpen; set => Set(ref _isRightbarOpen, value); }
    public bool HasCurrentSession => CurrentId is not null;
    public WorkspaceEntry? SelectedWorkspace { get => _selectedWorkspace; private set => Set(ref _selectedWorkspace, value); }
    public bool IsSearchOpen { get => _isSearchOpen; set => Set(ref _isSearchOpen, value); }

    public string SessionFilter
    {
        get => _sessionFilter;
        set { if (Set(ref _sessionFilter, value)) RefreshSessions(); }
    }

    public string? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (Set(ref _selectedModel, value) && value is not null && value != _settings.Current.Model)
                UpdateSettings(new { model = value });
        }
    }

    public MainViewModel(SessionManager manager, SessionStore store, SettingsService settings, WorkspaceService workspaces)
    {
        _manager = manager;
        _store = store;
        _settings = settings;
        _workspaces = workspaces;
        _dispatcher = Application.Current.Dispatcher;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); RefreshSessions(); };

        manager.SessionEventBroadcast += (sid, evt) => _dispatcher.BeginInvoke(() => OnSessionEvent(sid, evt));
        manager.SessionStreamBroadcast += (sid, chunk) => _dispatcher.BeginInvoke(() => OnSessionChunk(sid, chunk));
        manager.SessionRunningBroadcast += (sid, running) => _dispatcher.BeginInvoke(() => OnSessionRunning(sid, running));

        // Plan review / approval requests raised by exit_plan_mode.
        manager.Questions.QuestionRaised += (id, req) => _dispatcher.BeginInvoke(() =>
        {
            PendingQuestionId = id;
            ReviewQuestion = req.Question;
            ReviewPlanText = req.Detail ?? "";
        });
        manager.Questions.QuestionCleared += id => _dispatcher.BeginInvoke(() =>
        {
            if (PendingQuestionId == id) PendingQuestionId = null;
        });

        // Sandbox escalation approval requests (mirrors ui-approval ApprovalPanel wiring).
        manager.ApprovalInbox.Raised += (id, req) => _dispatcher.BeginInvoke(() =>
        {
            PendingApprovalId = id;
            ApprovalToolName = req.ToolName;
            ApprovalReason = req.Reason ?? "";
        });
        manager.ApprovalInbox.Cleared += id => _dispatcher.BeginInvoke(() =>
        {
            if (PendingApprovalId == id) PendingApprovalId = null;
        });

        RefreshPermissionCatalog();
        RefreshWorkspaces();
        RefreshSessions();
        RefreshCatalog();
        UpdateProviderMeta();
        ComposerStats.SetDetailed(settings.Current.PerformanceUsage != "compact");
    }

    /// <summary>Re-fold the composer statistics dock from the current session's durable log.</summary>
    private void RefreshComposerStats()
    {
        if (CurrentId is null)
        {
            ComposerStats.Clear();
            return;
        }
        var live = _manager.Get(CurrentId);
        if (live is null)
        {
            ComposerStats.Clear();
            return;
        }
        ComposerStats.SetDetailed(_settings.Current.PerformanceUsage != "compact");
        ComposerStats.Refresh(live.Session, _settings.Current.ContextWindow);
    }

    /// <summary>Approve the pending escalation (the one-shot grant).</summary>
    public void ApproveEscalation()
    {
        if (PendingApprovalId is { } id) _manager.ApprovalInbox.Answer(id, ApprovalOutcome.AllowedOnce);
    }

    /// <summary>Reject the pending escalation — the operation stays denied.</summary>
    public void RejectEscalation()
    {
        if (PendingApprovalId is { } id) _manager.ApprovalInbox.Answer(id, ApprovalOutcome.Rejected);
    }

    private void RefreshPermissionCatalog()
    {
        PermissionOptions.Clear();
        foreach (var option in _manager.Permissions.Catalog())
            PermissionOptions.Add(new PresetOptionVm { Value = option.Value, Name = option.Name, Description = option.Description });
        PermissionOptions.Add(new PresetOptionVm
        {
            Value = PermissionPresetService.CustomPreset,
            Name = "自定义",
            Description = "当前沙箱与审批设置不匹配任何预设。",
        });
    }

    /// <summary>Re-derive the current session's preset without triggering a switch.</summary>
    public void RefreshPermissionSelection()
    {
        var live = CurrentId is null ? null : _manager.Get(CurrentId);
        var current = live is null ? null : _manager.Permissions.Current(live.Session);
        if (_selectedPermission != current)
        {
            _selectedPermission = current;
            Raise(nameof(SelectedPermission));
        }
    }

    public void RefreshWorkspaces()
    {
        Workspaces.Clear();
        foreach (var workspace in _workspaces.Current) Workspaces.Add(workspace);
        if (SelectedWorkspace is null || !Workspaces.Contains(SelectedWorkspace))
            SelectedWorkspace = Workspaces.FirstOrDefault();
        RefreshPicker();
    }

    private void RefreshPicker()
    {
        PickerItems.Clear();
        var names = Workspaces.GroupBy(w => w.Name).ToDictionary(g => g.Key, g => g.Count());
        foreach (var workspace in Workspaces)
        {
            var display = names[workspace.Name] > 1
                ? $"{workspace.Name}（{Directory.GetParent(workspace.Path.TrimEnd('\\', '/'))?.Name}）"
                : workspace.Name;
            PickerItems.Add(new WorkspacePickVm(workspace, workspace == SelectedWorkspace, display));
        }
        Raise(nameof(SelectedWorkspace));
    }

    public void PickWorkspace(WorkspaceEntry workspace)
    {
        SelectedWorkspace = workspace;
        RefreshPicker();
        NewSession(workspace.Path);
    }

    public void AddWorkspace(string path)
    {
        var entry = _workspaces.Add(path);
        RefreshWorkspaces();
        PickWorkspace(entry);
    }

    private void OnSessionEvent(string sid, SessionEvent evt)
    {
        if (evt.Type == "agent/error")
        {
            var message = evt.Data.TryGetProperty("message", out var m) ? m.GetString() : "Agent error";
            Toast(message ?? "Agent error", true);
            // Keep the failure visible in the transcript too — a toast alone disappears
            // and the user is left with a silently stopped conversation.
            if (sid == CurrentId) CurrentChat?.Items.Add(new ErrorItem { Text = message ?? "Agent error" });
            return;
        }
        if (evt.Type == "compaction/status")
        {
            var state = evt.Data.TryGetProperty("state", out var st) ? st.GetString() : null;
            if (sid == CurrentId && CurrentChat is not null)
                CurrentChat.IsCompacting = state == "running";
            return;
        }
        if (sid == CurrentId) CurrentChat?.Fold(evt);
        if (evt.Type is SessionEventTypes.Rename or SessionEventTypes.UserMessage or SessionEventTypes.TurnEnd)
            RefreshSessionsSoon();
        // Feature state is folded from session events; refresh the panels when current session changes state.
        if (sid == CurrentId && evt.Type is
            SessionEventTypes.GoalChange or SessionEventTypes.PlanMode or SessionEventTypes.TeamTask or
            SessionEventTypes.TeamMode or SessionEventTypes.WorkflowStart or SessionEventTypes.WorkflowEnd or
            SessionEventTypes.WorkflowPhase or SessionEventTypes.JobRegistered or SessionEventTypes.JobSettled or
            SessionEventTypes.SubagentCatalog or SessionEventTypes.SubagentStart or SessionEventTypes.SubagentEnd or
            SessionEventTypes.PlanSubmitted or SessionEventTypes.TurnEnd)
            RefreshFeatures();
        // Permission knobs are durable session state; keep the selector in sync with the fold.
        if (sid == CurrentId && evt.Type is
            SessionEventTypes.PermissionPreset or SessionEventTypes.SandboxMode or SessionEventTypes.ApprovalPolicy)
            RefreshPermissionSelection();
        // Composer statistics fold from settled facts: assistant messages carry usage+timing,
        // tool results complete the wall-time pairs. Refresh only on those (not every delta).
        if (sid == CurrentId && evt.Type is
            SessionEventTypes.AssistantMessage or SessionEventTypes.ToolResult or SessionEventTypes.RequestHeader)
            RefreshComposerStats();
    }

    private void OnSessionChunk(string sid, StreamChunk chunk)
    {
        if (sid == CurrentId) CurrentChat?.ApplyChunk(chunk);
    }

    private void OnSessionRunning(string sid, bool running)
    {
        if (sid == CurrentId && CurrentChat is not null) CurrentChat.IsRunning = running;
        foreach (var row in Sessions.SelectMany(g => g.Children).Where(r => r.Id == sid))
            row.Running = running;
    }

    private void RefreshSessionsSoon()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    public void RefreshSessions()
    {
        var summaries = _store.ListSessions()
            .Where(s => !s.Archived)
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();
        var filter = SessionFilter.Trim();
        Sessions.Clear();
        foreach (var workspace in Workspaces)
        {
            var rows = summaries
                .Where(s => string.Equals(s.Cwd, workspace.Path, StringComparison.OrdinalIgnoreCase))
                .Where(s => MatchesFilter(s, filter))
                .ToList();
            if (filter.Length > 0 && rows.Count == 0) continue;
            var group = new WorkspaceGroupVm { Name = workspace.Name, Path = workspace.Path };
            foreach (var summary in rows) group.Children.Add(Row(summary));
            Sessions.Add(group);
        }
        var ungrouped = summaries
            .Where(s => !Workspaces.Any(w => string.Equals(w.Path, s.Cwd, StringComparison.OrdinalIgnoreCase)))
            .Where(s => MatchesFilter(s, filter))
            .ToList();
        if (ungrouped.Count > 0)
        {
            var group = new WorkspaceGroupVm { Name = "未分组" };
            foreach (var summary in ungrouped) group.Children.Add(Row(summary));
            Sessions.Add(group);
        }
    }

    private bool MatchesFilter(SessionSummary summary, string filter) =>
        filter.Length == 0 ||
        (summary.Title ?? "New Session").Contains(filter, StringComparison.OrdinalIgnoreCase);

    private SessionRowVm Row(SessionSummary summary) => new()
    {
        Id = summary.Id,
        Cwd = summary.Cwd,
        UpdatedAt = summary.UpdatedAt,
        Title = string.IsNullOrEmpty(summary.Title) ? "New Session" : summary.Title!,
        IsActive = summary.Id == CurrentId,
        Running = _manager.Get(summary.Id)?.Agent.IsRunning == true,
    };

    public void SelectSession(string id)
    {
        if (id == CurrentId) return;
        var live = _manager.Ensure(id);
        CurrentId = id;
        Raise(nameof(HasCurrentSession));
        var chat = new ChatViewModel();
        chat.LoadSnapshot(live.Session.Events, live.Agent.IsRunning, live.Session.Title, live.Session.Cwd);
        CurrentChat = chat;
        SelectedWorkspace = Workspaces.FirstOrDefault(w => string.Equals(w.Path, chat.Cwd, StringComparison.OrdinalIgnoreCase));
        RefreshPicker();
        foreach (var row in Sessions.SelectMany(g => g.Children))
            row.IsActive = row.Id == id;
        LoadFileTree("");
        RefreshFeatures();
        RefreshPermissionSelection();
        RefreshComposerStats();
    }

    public void NewSession() => NewSession(SelectedWorkspace?.Path);

    public void NewSession(string? cwd)
    {
        var target = cwd ?? Workspaces.FirstOrDefault()?.Path;
        if (target is not null)
        {
            var blank = _store.ListSessions()
                .Where(s => !s.Archived && string.Equals(s.Cwd, target, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.UpdatedAt)
                .FirstOrDefault(s => _store.OpenSession(s.Id) is { Events.Count: 0 });
            if (blank is not null)
            {
                RefreshSessions();
                SelectSession(blank.Id);
                return;
            }
        }
        var live = _manager.Create(target);
        RefreshSessions();
        SelectSession(live.Session.Id);
    }

    public void RenameSession(string id, string title)
    {
        _manager.Rename(id, title);
        if (id == CurrentId && CurrentChat is not null) CurrentChat.Title = title;
        RefreshSessions();
    }

    public void ArchiveSession(string id)
    {
        _store.ArchiveSession(id, true);
        if (id == CurrentId)
        {
            CurrentId = null;
            CurrentChat = null;
            Raise(nameof(HasCurrentSession));
            FileTree.Clear();
        }
        RefreshSessions();
    }

    /// <summary>归档列表（含所有工作区，按更新时间倒序）。归档窗口用。</summary>
    public IReadOnlyList<SessionSummary> ListArchivedSessions() =>
        [.. _store.ListSessions(includeArchived: true).Where(s => s.Archived)];

    /// <summary>取消归档：会话回到侧边栏原工作区分组。</summary>
    public void UnarchiveSession(string id)
    {
        _store.ArchiveSession(id, false);
        RefreshSessions();
    }

    public void DeleteSession(string id)
    {
        _manager.Remove(id);
        if (id == CurrentId)
        {
            CurrentId = null;
            CurrentChat = null;
            Raise(nameof(HasCurrentSession));
            FileTree.Clear();
            var next = _store.ListSessions().FirstOrDefault(s => !s.Archived);
            if (next is not null) SelectSession(next.Id);
            else NewSession();
        }
        RefreshSessions();
    }

    public void DeleteWorkspace(string id)
    {
        _workspaces.Remove(id);
        RefreshWorkspaces();
        RefreshSessions();
    }

    public void Send(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (CurrentId is null)
        {
            var live = _manager.Create(null);
            RefreshSessions();
            SelectSession(live.Session.Id);
        }
        _manager.Ensure(CurrentId!).Agent.Followup(text);
    }

    public void Cancel() => _manager.Get(CurrentId ?? "")?.Agent.Cancel();

    public void UpdateSettings(object patch)
    {
        _settings.Update(JsonSerializer.SerializeToElement(patch));
        App.ApplyTheme(_settings.Current.Theme);
        RefreshCatalog();
        UpdateProviderMeta();
        // performanceUsage 切换立即生效（compact/detailed 重排统计胶囊）。
        RefreshComposerStats();
    }

    private void UpdateProviderMeta() =>
        ComposerStats.ProviderMeta = $"{_settings.Current.Provider} · {_settings.Current.Model}";

    public void RefreshCatalog()
    {
        Models.Clear();
        var current = _settings.Current;
        if (current.ModelCatalogProvider == current.Provider && current.ModelCatalog.Count > 0)
        {
            foreach (var id in current.ModelCatalog)
                Models.Add(new ModelOptionVm { Id = id, Name = id });
        }
        else
        {
            switch (current.Provider)
            {
                case "openai-compat":
                    Models.Add(new ModelOptionVm { Id = "deepseek-chat", Name = "Chat" });
                    Models.Add(new ModelOptionVm { Id = "deepseek-reasoner", Name = "Reasoner" });
                    break;
                case "mock":
                    Models.Add(new ModelOptionVm { Id = "mock", Name = "Mock (keyless demo)" });
                    break;
                default:
                    Models.Add(new ModelOptionVm { Id = MyAgent.Llm.ModelIds.Flash, Name = "Flash" });
                    Models.Add(new ModelOptionVm { Id = MyAgent.Llm.ModelIds.V4Pro, Name = "V4 Pro" });
                    break;
            }
        }
        if (current.Provider != "mock" && !Models.Any(m => m.Id == current.Model))
            Models.Add(new ModelOptionVm { Id = current.Model, Name = current.Model });
        _selectedModel = Models.FirstOrDefault(m => m.Id == current.Model)?.Id ?? Models.FirstOrDefault()?.Id;
        Raise(nameof(SelectedModel));
    }

    /* ===== file tree ===== */
    public void LoadFileTree(string relative)
    {
        FileTree.Clear();
        if (CurrentId is null) return;
        var root = _manager.Ensure(CurrentId).Session.Cwd;
        foreach (var entry in EnumerateEntries(root, relative))
            FileTree.Add(entry);
    }

    private IEnumerable<FileEntryVm> EnumerateEntries(string root, string relative)
    {
        var dir = Path.GetFullPath(Path.Combine(root, relative));
        if (!dir.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(dir))
            yield break;
        var entries = Directory.EnumerateFileSystemEntries(dir)
            .Select(entry => new
            {
                Full = entry,
                Name = Path.GetFileName(entry),
                Path = Path.GetRelativePath(root, entry).Replace('\\', '/'),
                IsDirectory = Directory.Exists(entry),
            })
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(500);
        foreach (var entry in entries)
        {
            yield return new FileEntryVm
            {
                Name = entry.Name,
                Path = entry.Path,
                IsDirectory = entry.IsDirectory,
                Toggle = vm =>
                {
                    foreach (var child in EnumerateEntries(root, vm.Path))
                        vm.Children.Add(child);
                },
            };
        }
    }

    /* ===== feature panels ===== */
    public void RefreshFeatures()
    {
        if (CurrentId is null) return;
        var live = _manager.Ensure(CurrentId);
        var session = live.Session;

        var goal = _manager.Goals.Get(session);
        Goal.HasGoal = goal is not null;
        if (goal is not null)
        {
            Goal.Objective = goal.Objective;
            Goal.Phase = goal.Phase switch
            {
                MyAgent.Goal.GoalPhase.Active => "进行中",
                MyAgent.Goal.GoalPhase.Paused => "已暂停",
                MyAgent.Goal.GoalPhase.Blocked => "受阻",
                MyAgent.Goal.GoalPhase.Complete => "已完成",
                _ => "",
            };
            Goal.Progress = $"第 {goal.RoundsStarted}/{goal.MaxGoalRounds} 轮 · " +
                            (goal.Activation == MyAgent.Goal.GoalActivation.Armed ? "自动续跑已就绪" : "待人工继续");
        }

        var plan = _manager.Plans.Get(session);
        IsPlanActive = plan.Active;
        IsPlanPending = plan.Pending;
        IsPlanTargetActive = plan.Pending ? !plan.Active : plan.Active;
        Raise(nameof(PlanStatusText));

        TeamTasks.Clear();
        foreach (var task in _manager.Teams.List(session, limit: 100))
            TeamTasks.Add(new TeamTaskRowVm
            {
                Id = task.Task.Id,
                Subject = task.Task.Subject,
                Status = MyAgent.Team.TeamJson.StatusName(task.Task.Status),
                Ready = task.Ready,
                Owner = task.Task.OwnerName ?? "",
            });

        Schedules.Clear();
        foreach (var s in _manager.Schedules.List(limit: 100))
            Schedules.Add(new ScheduleRowVm
            {
                Id = s.Id,
                Title = s.Title,
                Kind = s.Kind,
                Next = s.ScheduledAt.ToLocalTime().ToString("MM-dd HH:mm"),
                Status = MyAgent.Schedule.ScheduleJson.StatusName(s.Status),
            });

        IsTeamEnabled = _manager.Teams.IsEnabled(session);

        JobList.Clear();
        foreach (var job in _manager.Jobs.List(CurrentId))
            JobList.Add(new JobRowVm
            {
                Id = job.Id, Label = job.Label, Status = job.Status, OutputBytes = job.OutputTotal,
            });

        Agents.Clear();
        Agents.Add(new AgentRowVm { Name = "lead", Status = "root", Description = "根代理" });
        foreach (var member in _manager.Subagents.ListDescendants(session))
        {
            var act = _manager.Subagents.Find(member.ChildId);
            Agents.Add(new AgentRowVm
            {
                Name = member.Name, Status = act?.Status ?? "inactive", Description = member.Label,
            });
        }

        Workflows.Clear();
        var wfEvents = session.Events.ToList();
        foreach (var evt in wfEvents.Where(e => e.Type == SessionEventTypes.WorkflowEnd).TakeLast(20).Reverse())
        {
            var runId = evt.Data.TryGetProperty("runId", out var rid) ? rid.GetString() ?? "" : "";
            var stop = evt.Data.TryGetProperty("stopReason", out var sr) ? sr.GetString() ?? "" : "";
            var agents = evt.Data.TryGetProperty("agentsStarted", out var ag) && ag.ValueKind == JsonValueKind.Number
                ? ag.GetInt32() : 0;
            var name = runId;
            foreach (var start in wfEvents.Where(e => e.Type == SessionEventTypes.WorkflowStart))
            {
                if (!start.Data.TryGetProperty("runId", out var r) || r.GetString() != runId) continue;
                if (start.Data.TryGetProperty("name", out var nm)) name = nm.GetString() ?? runId;
                break;
            }
            var phases = wfEvents.Where(e => e.Type == SessionEventTypes.WorkflowPhase)
                .Select(e => e.Data.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "")
                .Where(s => s.Length > 0).ToList();
            Workflows.Add(new WorkflowRowVm
            {
                RunId = runId, Name = name, StopReason = stop, AgentsStarted = agents,
                Phases = phases, AgentCount = wfEvents.Count(e => e.Type == SessionEventTypes.WorkflowAgentStart),
            });
        }
    }

    public void TogglePlanMode()
    {
        if (CurrentId is null) return;
        var live = _manager.Ensure(CurrentId);
        var session = live.Session;
        // Toggle the EFFECTIVE target (a pending selection flips back), mirroring the
        // harness chip/control. Between turns the change commits immediately — the old
        // Select()-only path left the UI state frozen until a turn that might never run.
        var wanted = !IsPlanTargetActive;
        var outcome = _manager.Plans.SetSelected(session, wanted, live.Agent.IsRunning);
        if (outcome == MyAgent.Plan.PlanSelectionOutcome.Committed)
            live.Agent.Inject(wanted
                ? "The user switched this session to plan mode."
                : "The user switched this session back to the default mode.");
        RefreshFeatures();
        Toast(outcome switch
        {
            MyAgent.Plan.PlanSelectionOutcome.Committed => wanted ? "计划模式已开启" : "计划模式已关闭",
            MyAgent.Plan.PlanSelectionOutcome.Queued => wanted
                ? "正在进入计划模式（下一步生效）"
                : "正在退出计划模式（下一步生效）",
            MyAgent.Plan.PlanSelectionOutcome.Cancelled => "已取消待生效的计划模式切换",
            _ => wanted ? "计划模式已开启" : "计划模式已关闭",
        }, false);
    }

    /// <summary>The plan chip is an exit control (mirrors ui-plan PlanChip: click = /plan off).</summary>
    public void ExitPlanMode()
    {
        if (CurrentId is null || !IsPlanTargetActive) return;
        TogglePlanMode();
    }

    public void ToggleTeamMode()
    {
        if (CurrentId is null) return;
        var session = _manager.Ensure(CurrentId).Session;
        _manager.Teams.SetMode(session, !_manager.Teams.IsEnabled(session));
        RefreshFeatures();
        Toast(_manager.Teams.IsEnabled(session) ? "团队模式已开启" : "团队模式已关闭（任务保留）", false);
    }

    // ---- GoalBar actions (mirrors ui-goal GoalBar: edit / pause / resume / clear) ----
    public void GoalEdit(string objective)
    {
        if (CurrentId is null) return;
        var session = _manager.Ensure(CurrentId).Session;
        var goal = _manager.Goals.Get(session);
        if (goal is null) return;
        try { _manager.Goals.Edit(session, goal.Id, goal.Revision, objective: objective); }
        catch (Exception ex) { Toast(ex.Message, true); }
        RefreshFeatures();
    }

    public void GoalPauseResume()
    {
        if (CurrentId is null) return;
        var session = _manager.Ensure(CurrentId).Session;
        var goal = _manager.Goals.Get(session);
        if (goal is null) return;
        try
        {
            if (goal.Phase == MyAgent.Goal.GoalPhase.Active)
                _manager.Goals.Pause(session, goal.Id, goal.Revision);
            else
                _manager.Goals.Resume(session, goal.Id, goal.Revision);
        }
        catch (Exception ex) { Toast(ex.Message, true); }
        RefreshFeatures();
    }

    public void GoalClear()
    {
        if (CurrentId is null) return;
        _manager.Goals.Clear(_manager.Ensure(CurrentId).Session);
        RefreshFeatures();
    }

    // ---- Plan review panel actions (mirrors PlanReviewPanel: approve / discuss) ----
    public void ApprovePlan()
    {
        if (PendingQuestionId is null) return;
        _manager.Questions.Answer(PendingQuestionId, MyAgent.Plan.ExitPlanModeTool.ApproveLabel);
        RefreshFeatures();
    }

    public void DiscussPlan()
    {
        if (PendingQuestionId is null) return;
        _manager.Questions.Dismiss(PendingQuestionId);
        PendingQuestionId = null;
        RefreshFeatures();
    }

    /* ===== toast ===== */
    public void Toast(string message, bool isError)
    {
        var toast = new ToastVm { Message = message, IsError = isError };
        Toasts.Add(toast);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => { Toasts.Remove(toast); timer.Stop(); };
        timer.Start();
    }
}