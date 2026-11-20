using System.Collections.Concurrent;
using MyAgent.Compaction;
using MyAgent.Context;
using MyAgent.Core;
using MyAgent.Fs;
using MyAgent.Goal;
using MyAgent.Jobs;
using MyAgent.Llm;
using MyAgent.Plan;
using MyAgent.Schedule;
using MyAgent.Sessions;
using MyAgent.Shell;
using MyAgent.Subagents;
using MyAgent.Team;
using MyAgent.Todo;
using MyAgent.Workflow;
using MyAgent.Approval;
using MyAgent.Permissions;
using MyAgent.Sandbox;

namespace MyAgent.App;

public sealed class RoutingProvider(SettingsService settings) : ILlmProvider
{
    private ILlmProvider? _cached;
    private string _cachedSignature = "";

    public string Name => settings.Current.Provider;

    public IAsyncEnumerable<StreamChunk> StreamAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var current = settings.Current;
        var signature = $"{current.Provider}|{current.ApiKey}|{current.BaseUrl}|{current.Effort}|{current.Thinking}";
        if (_cached is null || signature != _cachedSignature)
        {
            (_cached as IDisposable)?.Dispose();
            _cached = Create(current);
            _cachedSignature = signature;
        }
        return _cached.StreamAsync(request, cancellationToken);
    }

    private static ILlmProvider Create(HarnessSettings settings) => settings.Provider switch
    {
        "anthropic" => new AnthropicProvider(new AnthropicOptions
        {
            ApiKey = settings.ApiKey,
            BaseUrl = string.IsNullOrEmpty(settings.BaseUrl) ? "https://api.deepseek.com/anthropic" : settings.BaseUrl,
            Model = settings.Model,
            Effort = settings.Effort,
        }),
        "openai-compat" => new OpenAiCompatProvider(new OpenAiCompatOptions
        {
            ApiKey = settings.ApiKey,
            BaseUrl = string.IsNullOrEmpty(settings.BaseUrl) ? "https://api.deepseek.com" : settings.BaseUrl,
            Model = settings.Model,
            Thinking = settings.Thinking,
            Effort = settings.Effort,
        }),
        _ => new MockProvider(),
    };
}

public sealed class LiveSession
{
    public required Core.Session Session { get; init; }
    public required Agent Agent { get; init; }
    public required AgentOptions Options { get; init; }
    public BasicCompactor? Compactor { get; set; }
}

public sealed class SessionManager : ISessionDispatcher, IDisposable
{
    private readonly ConcurrentDictionary<string, LiveSession> _live = new();
    private readonly RoutingProvider _provider;
    private readonly SystemPromptAssembler _promptAssembler = new();

    // Feature services — process-wide singletons folded from each session's durable event log.
    public GoalService Goals { get; } = new();
    public PlanService Plans { get; } = new();
    public TeamService Teams { get; } = new();
    public ScheduleStore Schedules { get; }
    public ScheduleRunner ScheduleRunner { get; }
    public SubagentRuntime Subagents { get; }
    public JobRegistry Jobs { get; }
    public WorkflowService Workflows { get; }
    public GoalRoundDriver GoalDriver { get; }
    public ExitPlanModeTool ExitPlanTool { get; }
    public QuestionService Questions { get; } = new();

    // Permission & sandbox stack (mirrors dsh-sandbox-policy / dsh-user-approval / dsh-permission-presets).
    public SandboxPolicyService SandboxPolicy { get; }
    public ApprovalService Approvals { get; }
    public ApprovalInbox ApprovalInbox { get; } = new();
    public PermissionPresetService Permissions { get; }
    private readonly WindowsAclSandboxProvider? _sandboxProvider;
    private readonly ToolSandbox _toolSandbox;

    public SessionStore Store { get; }
    public SettingsService Settings { get; }

    public event Action<string, SessionEvent>? SessionEventBroadcast;
    public event Action<string, StreamChunk>? SessionStreamBroadcast;
    public event Action<string, bool>? SessionRunningBroadcast;

    public SessionManager(SessionStore store, SettingsService settings)
    {
        Store = store;
        Settings = settings;
        _provider = new RoutingProvider(settings);
        settings.Changed += OnSettingsChanged;

        Subagents = new SubagentRuntime(CreateChildAgent, maxDepth: 2, maxActive: 8);
        Jobs = new JobRegistry(id => _live.TryGetValue(id, out var l) ? l.Session : null);
        Workflows = new WorkflowService(Subagents);
        // 定时任务是会话数据的一部分，跟随数据目录（而非配置锚点）。
        Schedules = new ScheduleStore(store.DataHome);
        ScheduleRunner = new ScheduleRunner(Schedules, this);
        GoalDriver = new GoalRoundDriver(Goals);
        ExitPlanTool = new ExitPlanModeTool(Plans, Questions);

        _promptAssembler.DynamicSections = session =>
            Plans.IsActive(session) ? [Plans.PolicySection] : [];
        ExitPlanTool.PlanSubmitted += (sid, plan) =>
            SessionEventBroadcast?.Invoke(sid, FeatureEvent(SessionEventTypes.PlanSubmitted, new { plan }));

        // Permission & sandbox stack. The deployment default mode comes from settings
        // (fail-safe read-only when unset); the process backend is the Windows ACL runner
        // where usable — elsewhere composition stays unconfined (danger-full-access tools).
        SandboxPolicy = new SandboxPolicyService(
            SandboxModes.TryParse(settings.Current.SandboxMode, out var mode) ? mode : SandboxMode.WorkspaceWrite,
            additionalWritableRoots: settings.Current.SandboxWritableRoots);
        Approvals = new ApprovalService();
        Approvals.SetAnswerer(ApprovalInbox);
        Permissions = new PermissionPresetService(SandboxPolicy, Approvals)
        {
            DefaultPreset = string.IsNullOrEmpty(settings.Current.PermissionPreset)
                ? "workspace-write"
                : settings.Current.PermissionPreset,
        };
        if (OperatingSystem.IsWindows())
            _sandboxProvider = new WindowsAclSandboxProvider();
        _toolSandbox = new ToolSandbox(SandboxPolicy, _sandboxProvider, Approvals);
    }

    public LiveSession Create(string? cwd)
    {
        var session = Store.CreateSession(string.IsNullOrEmpty(cwd) ? Directory.GetCurrentDirectory() : Path.GetFullPath(cwd));
        return Activate(session);
    }

    public LiveSession? Get(string id) => _live.GetValueOrDefault(id);

    /// <summary>是否有任何会话正在运行（含子代理会话）。
    /// 数据目录等"会挪动会话文件"的配置修改前必须检查。</summary>
    public bool AnySessionRunning => _live.Values.Any(l => l.Agent.IsRunning);

    public LiveSession Ensure(string id)
    {
        if (_live.TryGetValue(id, out var live)) return live;
        var session = Store.OpenSession(id) ?? throw new HarnessError("session_not_found", $"Session '{id}' not found.");
        return Activate(session);
    }

    public IReadOnlyList<object> List() =>
        [.. Store.ListSessions().Select(s => new
        {
            id = s.Id, title = s.Title, cwd = s.Cwd, createdAt = s.CreatedAt, updatedAt = s.UpdatedAt,
            running = _live.TryGetValue(s.Id, out var live) && live.Agent.IsRunning,
        })];

    public void Remove(string id)
    {
        if (_live.TryRemove(id, out var live)) live.Agent.Cancel();
        Store.DeleteSession(id);
    }

    public void Rename(string id, string title)
    {
        if (_live.TryGetValue(id, out var live)) live.Session.Append(SessionEventTypes.Rename, new { title });
        else Store.RenameSession(id, title);
    }

    // ---- ISessionDispatcher ----
    public IReadOnlyList<string> ListSessionIds() => [.. Store.ListSessions(false).Select(s => s.Id)];

    public void Deliver(string sessionId, string prompt, string source)
    {
        try { Ensure(sessionId).Agent.Followup(prompt, source); }
        catch (HarnessError) { Create(null).Agent.Followup(prompt, source); }
    }

    public string CreateSessionFor(string? cwd) => Create(cwd).Session.Id;
    string ISessionDispatcher.CreateSession(string? cwd) => CreateSessionFor(cwd);

    /// <summary>Factory used by the subagent runtime to create &amp; activate a real child Agent.</summary>
    private (Session, Agent) CreateChildAgent(string cwd, string parentId, int depth, string origin,
        string childId, IReadOnlyList<SessionEvent>? seed, string name)
    {
        // The child owns an independent session log; fork seeds it with the parent's
        // history up to the last completed turn (mirrors snapshotEvents-based fork).
        var session = new Core.Session
        {
            Id = childId,
            Cwd = cwd,
            ParentSessionId = parentId,
            Origin = origin,
            DelegationDepth = depth,
            InheritedEventCount = seed?.Count ?? 0,
        };
        if (seed is { Count: > 0 }) session.Restore(seed);
        // Seed the child's permission knobs from the parent's effective values
        // (source='delegation'); Activate's PinInitialPermission preserves them.
        if (_live.TryGetValue(parentId, out var parent))
            Permissions.SeedDelegation(parent.Session, session);
        var live = Activate(session, isRoot: false);
        return (session, live.Agent);
    }

    private LiveSession Activate(Core.Session session, bool isRoot = true)
    {
        var settings = Settings.Current;
        var options = new AgentOptions
        {
            Model = settings.Model,
            ThinkingEnabled = settings.Thinking,
            ContextWindow = settings.ContextWindow,
        };
        var tools = new ToolRegistry();
        // Enforcing families share ONE ToolSandbox so bash and fs escalate identically.
        tools.Register(new BashTool(_toolSandbox));
        tools.Register(new ReadTool());
        tools.Register(new WriteTool(_toolSandbox));
        tools.Register(new EditTool(_toolSandbox));
        tools.Register(new GlobTool());
        tools.Register(new GrepTool());
        tools.Register(new TodoWriteTool());
        // goal
        tools.Register(new GetGoalTool(Goals));
        tools.Register(new CreateGoalTool(Goals));
        tools.Register(new UpdateGoalTool(Goals));
        // plan
        tools.Register(ExitPlanTool);
        // team
        tools.Register(new TeamTaskCreateTool(Teams));
        tools.Register(new TeamTaskListTool(Teams));
        tools.Register(new TeamTaskGetTool(Teams));
        tools.Register(new TeamTaskUpdateTool(Teams));
        // workflow (JS/PTC) + jobs
        tools.Register(new WorkflowTool(Workflows, Jobs));
        tools.Register(new JobOutputTool(Jobs));
        tools.Register(new JobListTool(Jobs));
        tools.Register(new JobKillTool(Jobs));
        // schedule
        tools.Register(new ScheduleCreateTool(Schedules));
        tools.Register(new ScheduleListTool(Schedules));
        tools.Register(new ScheduleViewTool(Schedules));
        tools.Register(new ScheduleEditTool(Schedules));
        // subagents (only on root sessions; delegated tools available to descendants too)
        tools.Register(new SpawnSubagentTool(Subagents, "spawn"));
        tools.Register(new SpawnSubagentTool(Subagents, "fork", "subagent_fork"));
        tools.Register(new ListAgentsTool(Subagents));
        tools.Register(new SendMessageTool(Subagents));
        tools.Register(new InterruptAgentTool(Subagents));
        tools.Register(new WaitAgentTool(Subagents, id => _live.TryGetValue(id, out var l) ? l.Agent : null));

        var agent = new Agent(session, _provider, tools, _promptAssembler, options);
        var live = new LiveSession { Session = session, Agent = agent, Options = options };
        AttachCompactor(live, settings);

        agent.PreStepAsync = (s, ct) =>
        {
            Plans.CommitPending(s);
            return live.Compactor?.MaybeCompactAsync(s, ct) ?? Task.CompletedTask;
        };
        // Provider rejected the request as too long → force a checkpoint and retry the step.
        agent.ContextOverflowRecoveryAsync = async (s, ct) =>
            live.Compactor is not null && await live.Compactor.ForceCompactAsync(s, ct);
        agent.AfterTurnAsync = (s, stop, ct) => GoalDriver.OnAfterTurnAsync(s, stop, ct);
        // Policy narration travels in the per-request runtime-context snapshot (cache-safe).
        agent.RuntimeContextSections = s => Permissions.RenderContext(s);

        agent.EventAppended += evt => SessionEventBroadcast?.Invoke(session.Id, evt);
        agent.AssistantStream += chunk => SessionStreamBroadcast?.Invoke(session.Id, chunk);
        agent.RunningChanged += running => SessionRunningBroadcast?.Invoke(session.Id, running);
        agent.Error += ex => SessionEventBroadcast?.Invoke(session.Id,
            FeatureEvent("agent/error", new { message = ex.Message }));

        _live[session.Id] = live;
        if (isRoot) Subagents.RegisterRoot(agent);
        Goals.Disarm(session);
        // Fill every missing permission fact at activation (fresh sessions pin the user
        // default; restored/seeded sessions preserve their effective knob values).
        Permissions.PinInitialPermission(session);
        return live;
    }

    private void AttachCompactor(LiveSession live, HarnessSettings settings)
    {
        var compactor = new BasicCompactor(_provider, settings.Model, settings.ContextWindow, 32_000);
        var sid = live.Session.Id;
        // UI visibility: compaction is a long LLM call — without these signals the app
        // looks frozen ("压缩后就不动了"). Failures surface as a notice, never a dead turn.
        compactor.Started += () => SessionEventBroadcast?.Invoke(sid,
            FeatureEvent("compaction/status", new { state = "running" }));
        compactor.Finished += () => SessionEventBroadcast?.Invoke(sid,
            FeatureEvent("compaction/status", new { state = "done" }));
        compactor.Failed += ex => SessionEventBroadcast?.Invoke(sid,
            FeatureEvent("agent/error", new { message = $"上下文压缩失败（本轮将继续使用完整历史）：{ex.Message}" }));
        live.Compactor = compactor;
    }

    private static SessionEvent FeatureEvent(string type, object data) => new()
    {
        Type = type, Seq = -1, Time = DateTimeOffset.UtcNow,
        Data = System.Text.Json.JsonSerializer.SerializeToElement(data, Core.Session.JsonOptions),
    };

    private void OnSettingsChanged()
    {
        var settings = Settings.Current;
        foreach (var live in _live.Values)
        {
            live.Options.Model = settings.Model;
            live.Options.ThinkingEnabled = settings.Thinking;
            live.Options.ContextWindow = settings.ContextWindow;
            AttachCompactor(live, settings);
        }
        // The default preset applies to FUTURE sessions; live sessions keep their durable knobs.
        if (!string.IsNullOrEmpty(settings.PermissionPreset))
            Permissions.DefaultPreset = settings.PermissionPreset;
    }

    public void Dispose()
    {
        Settings.Changed -= OnSettingsChanged;
        ScheduleRunner.Dispose();
        // Revoke the revocable temp grants and delete private temp trees; standing
        // workspace ACEs stay by design (the cross-session reuse cache).
        _sandboxProvider?.Dispose();
    }
}
