using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MyAgent.Compaction;
using MyAgent.Context;
using MyAgent.Core;
using MyAgent.Fs;
using MyAgent.Llm;
using MyAgent.Sessions;
using MyAgent.Shell;
using MyAgent.Todo;

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

public sealed class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, LiveSession> _live = new();
    private readonly RoutingProvider _provider;
    private readonly SystemPromptAssembler _promptAssembler = new();

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
    }

    public LiveSession Create(string? cwd)
    {
        var session = Store.CreateSession(string.IsNullOrEmpty(cwd) ? Directory.GetCurrentDirectory() : Path.GetFullPath(cwd));
        return Activate(session);
    }

    public LiveSession? Get(string id) => _live.GetValueOrDefault(id);

    public LiveSession Ensure(string id)
    {
        if (_live.TryGetValue(id, out var live)) return live;
        var session = Store.OpenSession(id) ?? throw new HarnessError("session_not_found", $"Session '{id}' not found.");
        return Activate(session);
    }

    public IReadOnlyList<object> List()
    {
        return [.. Store.ListSessions().Select(s => new
        {
            id = s.Id,
            title = s.Title,
            cwd = s.Cwd,
            createdAt = s.CreatedAt,
            updatedAt = s.UpdatedAt,
            running = _live.TryGetValue(s.Id, out var live) && live.Agent.IsRunning,
        })];
    }

    public void Remove(string id)
    {
        if (_live.TryRemove(id, out var live)) live.Agent.Cancel();
        Store.DeleteSession(id);
    }

    public void Rename(string id, string title)
    {
        if (_live.TryGetValue(id, out var live))
            live.Session.Append(SessionEventTypes.Rename, new { title });
        else
            Store.RenameSession(id, title);
    }

    private LiveSession Activate(Core.Session session)
    {
        var settings = Settings.Current;
        var options = new AgentOptions
        {
            Model = settings.Model,
            ThinkingEnabled = settings.Thinking,
        };
        var tools = new ToolRegistry();
        tools.Register(new BashTool());
        tools.Register(new ReadTool());
        tools.Register(new WriteTool());
        tools.Register(new EditTool());
        tools.Register(new GlobTool());
        tools.Register(new GrepTool());
        tools.Register(new TodoWriteTool());

        var agent = new Agent(session, _provider, tools, _promptAssembler, options);
        var live = new LiveSession { Session = session, Agent = agent, Options = options };
        live.Compactor = BuildCompactor(settings);
        agent.PreStepAsync = (s, ct) => live.Compactor?.MaybeCompactAsync(s, ct) ?? Task.CompletedTask;

        agent.EventAppended += evt => SessionEventBroadcast?.Invoke(session.Id, evt);
        agent.AssistantStream += chunk => SessionStreamBroadcast?.Invoke(session.Id, chunk);
        agent.RunningChanged += running => SessionRunningBroadcast?.Invoke(session.Id, running);
        agent.Error += ex => SessionEventBroadcast?.Invoke(session.Id, new SessionEvent
        {
            Type = "agent/error",
            Seq = -1,
            Time = DateTimeOffset.UtcNow,
            Data = System.Text.Json.JsonSerializer.SerializeToElement(new { message = ex.Message }, Core.Session.JsonOptions),
        });

        _live[session.Id] = live;
        return live;
    }

    private BasicCompactor BuildCompactor(HarnessSettings settings) =>
        new(_provider, settings.Model, ModelIds.ContextWindow, 32_000);

    private void OnSettingsChanged()
    {
        var settings = Settings.Current;
        foreach (var live in _live.Values)
        {
            live.Options.Model = settings.Model;
            live.Options.ThinkingEnabled = settings.Thinking;
            live.Compactor = BuildCompactor(settings);
        }
    }

    public void Dispose() => Settings.Changed -= OnSettingsChanged;
}