using System.Collections.Concurrent;
using System.Text.Json;
using MyAgent.Core;

namespace MyAgent.Subagents;

public sealed record SubagentDescriptor(string Name, string Label, string Provider, string Mode = "continuable");

public sealed record SubagentCatalogEntry(string ChildId, string Name, string Label, string Mode);

/// <summary>One durable child identity plus its current residency epoch.</summary>
public sealed class SubagentActivation
{
    public required string ChildId { get; init; }
    public required string RunId { get; init; }
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required Agent Agent { get; init; }
    public required Session RootSession { get; init; }
    public required string[] Ancestors { get; init; }
    public required string Provider { get; init; }
    public string? Path { get; set; }
    public bool Closing { get; set; }
    public TaskCompletionSource<SubagentResult> Result { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Status => Agent.IsRunning ? "running" : "inactive";
}

public sealed record SubagentResult(string ChildId, string Name, string StopReason, string Output);

/// <summary>Factory that creates and activates a real child Agent (implemented by the App layer).</summary>
public delegate (Session Session, Agent Agent) SubagentFactory(
    string cwd, string parentSessionId, int depth, string origin,
    string childId, IReadOnlyList<SessionEvent>? seed, string name);

/// <summary>
/// In-process subagent runtime mirroring dsh-subagent: spawn (fresh) and fork (seeded from the
/// parent's last completed turn), durable childId vs epoch runId, root/ancestor authority,
/// name resolution, catalog/descriptor events, message relay, interrupt and tree draining.
/// </summary>
public sealed class SubagentRuntime
{
    private readonly SubagentFactory _factory;
    private readonly int _maxDepth;
    private readonly int _maxActive;
    private readonly ConcurrentDictionary<string, SubagentActivation> _resident = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Agent> _roots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _reservedNames = new(StringComparer.Ordinal);
    private readonly object _startGate = new();

    public SubagentRuntime(SubagentFactory factory, int maxDepth = 1, int maxActive = 8)
    {
        _factory = factory;
        _maxDepth = maxDepth;
        _maxActive = maxActive;
    }

    public void RegisterRoot(Agent root) => _roots[root.Session.Id] = root;

    public string[] NamesOf(Session session)
    {
        var root = RootSessionId(session);
        var self = session.Id == root ? "lead" : FindName(session.Id) ?? session.Id;
        return [self];
    }

    public string RootSessionId(Session session)
    {
        if (_resident.TryGetValue(session.Id, out var act)) return act.RootSession.Id;
        return session.Id;
    }

    public SubagentActivation? Find(string idOrName)
    {
        if (_resident.TryGetValue(idOrName, out var byId)) return byId;
        return _resident.Values.FirstOrDefault(a => a.Name == idOrName || a.Path == idOrName);
    }

    /// <summary>Resolve `lead`, a member name/path, or a raw session id.</summary>
    public string? ResolveName(Session caller, string name)
    {
        var root = RootSessionId(caller);
        if (name == "lead") return root;
        var act = Find(name);
        if (act is not null) return act.ChildId;
        return name.Contains('/') ? null : name;
    }

    public IReadOnlyList<SubagentCatalogEntry> ReadCatalog(Session rootSession)
    {
        var list = new List<SubagentCatalogEntry>();
        foreach (var evt in rootSession.Events)
        {
            if (evt.Type != SessionEventTypes.SubagentCatalog) continue;
            try
            {
                var childId = evt.Data.GetProperty("childId").GetString() ?? "";
                var name = evt.Data.GetProperty("name").GetString() ?? "";
                var label = evt.Data.GetProperty("label").GetString() ?? "";
                var mode = evt.Data.GetProperty("mode").GetString() ?? "continuable";
                list.Add(new SubagentCatalogEntry(childId, name, label, mode));
            }
            catch (KeyNotFoundException) { }
            catch (JsonException) { }
        }
        return list;
    }

    public IReadOnlyList<SubagentCatalogEntry> ListDescendants(Session rootSession) => ReadCatalog(rootSession);

    private string? FindName(string sessionId)
    {
        foreach (var root in _roots.Keys)
            if (_roots.TryGetValue(root, out var agent))
                foreach (var entry in ReadCatalog(agent.Session))
                    if (entry.ChildId == sessionId) return entry.Name;
        return null;
    }

    public SubagentActivation Start(
        Session parent, string provider, string label, string prompt, string? name = null,
        string delivery = "parent")
    {
        lock (_startGate) return StartCore(parent, provider, label, prompt, name, delivery);
    }

    private SubagentActivation StartCore(
        Session parent, string provider, string label, string prompt, string? name,
        string delivery)
    {
        var rootId = RootSessionId(parent);
        var depth = DepthOf(parent) + 1;
        if (depth > _maxDepth)
            throw new HarnessError("DEPTH_EXCEEDED", $"Delegation depth {depth} exceeds max {_maxDepth}.");
        if (_resident.Count >= _maxActive)
            throw new HarnessError("ACTIVATION_LIMIT_REACHED", "No free subagent slots.");

        // Reserve the name synchronously before any await.
        var used = ReadCatalog(RootSessionOf(parent)).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var resolved = name ?? Enumerable.Range(1, 1000).Select(i => $"agent-{i}").First(n => !used.Contains(n));
        if (!System.Text.RegularExpressions.Regex.IsMatch(resolved, "^[a-z][a-z0-9-]{0,31}$") || resolved == "lead")
            throw new HarnessError("INVALID_AGENT_NAME", $"Invalid subagent name '{resolved}'.");
        if (used.Contains(resolved))
            throw new HarnessError("DUPLICATE_AGENT_NAME", $"Name '{resolved}' already used.");
        _reservedNames[resolved] = 1;

        IReadOnlyList<SessionEvent>? seed = null;
        if (provider == "fork")
        {
            var lastEnd = parent.Events.LastOrDefault(e => e.Type == SessionEventTypes.TurnEnd);
            if (lastEnd is not null)
                seed = parent.Events.Where(e => e.Seq <= lastEnd.Seq).ToList();
        }

        var childId = $"s_{Guid.NewGuid():N}";
        var runId = $"run_{Guid.NewGuid():N}";
        var ancestors = AncestorsOf(parent).Append(parent.Id).ToArray();

        var (childSession, childAgent) = _factory(
            parent.Cwd, parent.Id, depth, "subagent", childId, seed, resolved);

        childSession.Append(SessionEventTypes.SubagentDescriptor, new
        {
            version = 3, mode = "continuable", provider = provider == "fork" ? "fork" : "spawn",
            label, name = resolved,
        });

        var activation = new SubagentActivation
        {
            ChildId = childId,
            RunId = runId,
            Name = resolved,
            Label = label,
            Agent = childAgent,
            RootSession = RootSessionOf(parent),
            Ancestors = ancestors,
            Provider = provider,
            Path = resolved,
        };
        _resident[childId] = activation;

        // Catalog is committed once the initial message is accepted.
        activation.RootSession.Append(SessionEventTypes.SubagentCatalog, new
        {
            version = 3, childId, childCreatedAt = DateTimeOffset.UtcNow,
            mode = "continuable", label, name = resolved,
        });
        activation.RootSession.Append(SessionEventTypes.SubagentStart, new
        {
            childId, runId, name = resolved, label, provider,
        });

        childAgent.Followup(prompt, "user");
        WatchAsync(activation, delivery);
        return activation;
    }

    private void WatchAsync(SubagentActivation act, string delivery)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Wait until a turn-end is recorded (the driver starts asynchronously, so
                // IsRunning may still be false right after Followup).
                var deadline = DateTime.UtcNow.AddMinutes(10);
                while (!act.Agent.Session.Events.Any(e => e.Type == SessionEventTypes.TurnEnd)
                       && DateTime.UtcNow < deadline)
                    await Task.Delay(30);
                await act.Agent.WhenIdleAsync();
                var lastAssistant = act.Agent.Session.Events
                    .LastOrDefault(e => e.Type == SessionEventTypes.AssistantMessage);
                var output = "";
                if (lastAssistant is not null && lastAssistant.Data.TryGetProperty("blocks", out var blocks))
                {
                    foreach (var block in blocks.EnumerateArray())
                    {
                        if (block.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                            block.TryGetProperty("text", out var tx))
                            output += tx.GetString() ?? "";
                    }
                }
                var result = new SubagentResult(act.ChildId, act.Name, "completed", output.Trim());
                act.Closing = true;
                act.RootSession.Append(SessionEventTypes.SubagentEnd, new
                {
                    childId = act.ChildId, runId = act.RunId, name = act.Name,
                    stopReason = result.StopReason, output = result.Output,
                });
                if (delivery == "parent" && _roots.TryGetValue(act.RootSession.Id, out var root))
                {
                    var notice = $"Subagent '{act.Name}' finished ({result.StopReason}).\n{result.Output}";
                    if (root.IsRunning) root.Steer(notice, "subagent-settled");
                    else root.Followup(notice, "subagent-settled");
                }
                act.Result.TrySetResult(result);
            }
            catch (Exception ex)
            {
                act.Result.TrySetResult(new SubagentResult(act.ChildId, act.Name, "error", ex.Message));
            }
            finally
            {
                _resident.TryRemove(act.ChildId, out _);
                _reservedNames.TryRemove(act.Name, out _);
            }
        });
    }

    public void SendMessage(Session sender, string target, string content)
    {
        var targetId = ResolveName(sender, target)
            ?? throw new HarnessError("AGENT_NAME_NOT_FOUND", $"Unknown agent '{target}'.");
        if (targetId == sender.Id)
            throw new HarnessError("UNAUTHORIZED", "Cannot message self.");
        var act = Find(targetId);
        if (act is null) throw new HarnessError("NOT_CONTINUABLE", $"Agent '{target}' is not live.");
        if (act.RootSession.Id != RootSessionId(sender))
            throw new HarnessError("UNAUTHORIZED", "Sender and target belong to different roots.");

        var wrapped = $"Agent {NamesOf(sender)[0]} sent a message: {content}";
        if (act.Agent.IsRunning) act.Agent.Steer(wrapped, "agent-message");
        else act.Agent.Followup(wrapped, "agent-message");
    }

    public bool Interrupt(Session caller, string target)
    {
        var targetId = ResolveName(caller, target) ?? target;
        if (targetId == caller.Id) return false;
        var act = Find(targetId);
        if (act is null) return true; // absent/idle target: accepted no-op
        if (!act.Ancestors.Contains(caller.Id) && caller.Id != act.RootSession.Id) return false;
        act.Agent.Cancel();
        return true;
    }

    public async Task<bool> WaitForStepAsync(Agent caller, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Math.Clamp(timeoutMs, 10_000, 3_600_000));
        try { await caller.Inbox.WaitForStepAsync(cts.Token); return true; }
        catch (OperationCanceledException) { return false; }
    }

    public async Task WaitForTreeWorkAsync(string rootId, CancellationToken ct = default)
    {
        while (true)
        {
            var pending = _resident.Values
                .Where(a => a.RootSession.Id == rootId && a.Agent.IsRunning)
                .ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAny(pending.Select(a => a.Agent.WhenIdleAsync(ct)));
            await Task.Delay(50, ct);
        }
    }

    private Session RootSessionOf(Session s) =>
        _roots.TryGetValue(RootSessionId(s), out var root) ? root.Session : s;

    private static int DepthOf(Session s) => s.DelegationDepth;

    private static string[] AncestorsOf(Session s) =>
        string.IsNullOrEmpty(s.ParentSessionId) ? [] : [s.ParentSessionId];
}
