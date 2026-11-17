using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using MyAgent.App;
using MyAgent.Core;
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
    private string _title = "New Session";
    private string _cwd = "";
    private string _usageText = "";
    private AssistantItem? _live;
    private ProcessGroupItem? _liveGroup;
    private readonly Dictionary<int, ToolItem> _liveTools = [];

    public ObservableCollection<ChatItem> Items { get; } = [];
    public ObservableCollection<TodoVm> Todos { get; } = [];

    public bool IsRunning { get => _isRunning; set { if (Set(ref _isRunning, value)) Raise(nameof(IsEmpty)); } }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Cwd { get => _cwd; set => Set(ref _cwd, value); }
    public string UsageText { get => _usageText; set => Set(ref _usageText, value); }
    public bool HasTodos => Todos.Count > 0;
    public bool IsEmpty => Items.Count == 0 && _live is null;

    public ChatViewModel()
    {
        Items.CollectionChanged += (_, _) => Raise(nameof(IsEmpty));
        Todos.CollectionChanged += (_, _) => Raise(nameof(HasTodos));
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
                if (message?.Usage is { } usage && usage.PromptTokens > 0)
                    UsageText = $"ctx ~{(int)Math.Round(usage.PromptTokens / 1000.0)}k tokens";
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
            case UsageChunk usage:
                if (usage.PromptTokens > 0)
                    UsageText = $"ctx ~{(int)Math.Round(usage.PromptTokens / 1000.0)}k tokens";
                break;
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
    private string _providerMeta = "";
    private WorkspaceEntry? _selectedWorkspace;
    private string _sessionFilter = "";
    private bool _isSearchOpen;

    public ObservableCollection<WorkspaceGroupVm> Sessions { get; } = [];
    public ObservableCollection<WorkspaceEntry> Workspaces { get; } = [];
    public ObservableCollection<WorkspacePickVm> PickerItems { get; } = [];
    public ObservableCollection<FileEntryVm> FileTree { get; } = [];
    public ObservableCollection<ToastVm> Toasts { get; } = [];
    public ObservableCollection<ModelOptionVm> Models { get; } = [];

    public HarnessSettings Settings => _settings.Current;

    public ChatViewModel? CurrentChat { get => _currentChat; private set => Set(ref _currentChat, value); }
    public string? CurrentId { get => _currentId; private set => Set(ref _currentId, value); }
    public bool IsSidebarCollapsed { get => _isSidebarCollapsed; set => Set(ref _isSidebarCollapsed, value); }
    public bool IsRightbarOpen { get => _isRightbarOpen; set => Set(ref _isRightbarOpen, value); }
    public bool HasCurrentSession => CurrentId is not null;
    public string ProviderMeta { get => _providerMeta; private set => Set(ref _providerMeta, value); }
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

        RefreshWorkspaces();
        RefreshSessions();
        RefreshCatalog();
        UpdateProviderMeta();
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
            return;
        }
        if (sid == CurrentId) CurrentChat?.Fold(evt);
        if (evt.Type is SessionEventTypes.Rename or SessionEventTypes.UserMessage or SessionEventTypes.TurnEnd)
            RefreshSessionsSoon();
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
    }

    private void UpdateProviderMeta() =>
        ProviderMeta = $"{_settings.Current.Provider} · {_settings.Current.Model}";

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