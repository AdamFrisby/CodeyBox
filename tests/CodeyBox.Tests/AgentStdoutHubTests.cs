using Microsoft.AspNetCore.SignalR;
using CodeyBox.Api;
using CodeyBox.Api.Hubs;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="AgentStdoutBroadcastService"/> using a fake
/// <see cref="IHubContext{AgentStdoutHub}"/>. Verifies that:
/// - chunks are stored in the ring buffer (GetTail)
/// - secret patterns are redacted before storage
/// - CompleteAsync flushes pending data and sends streamComplete to the right group
/// - separate work items use separate groups
/// </summary>
public sealed class AgentStdoutHubTests
{
    private static (AgentStdoutBroadcastService svc, FakeHubContext hub) MakeSvc()
    {
        var hub = new FakeHubContext();
        var svc = new AgentStdoutBroadcastService(hub);
        return (svc, hub);
    }

    // ── Ring buffer / GetTail ─────────────────────────────────────────────────

    [Fact]
    public void GetTail_UnknownWorkItem_ReturnsNull()
    {
        var (svc, _) = MakeSvc();
        Assert.Null(svc.GetTail(WorkItemId.New()));
    }

    [Fact]
    public void BroadcastChunk_StoresChunk_GetTailReturnsIt()
    {
        var (svc, _) = MakeSvc();
        var id = WorkItemId.New();

        svc.BroadcastChunk(id, "work", "hello from agent\n");

        var tail = svc.GetTail(id);
        Assert.NotNull(tail);
        Assert.Contains("hello from agent", tail);
    }

    [Fact]
    public void BroadcastChunk_RedactsSecretsBeforeStorage()
    {
        var (svc, _) = MakeSvc();
        var id = WorkItemId.New();

        svc.BroadcastChunk(id, "work", "token=gho_ABCdef123456789012345678901234 done");

        var tail = svc.GetTail(id);
        Assert.NotNull(tail);
        Assert.DoesNotContain("gho_", tail);
        Assert.Contains("***", tail);
    }

    [Fact]
    public void BroadcastChunk_RedactsSessionIdsBeforeStorage()
    {
        var (svc, _) = MakeSvc();
        var id = WorkItemId.New();
        const string SessionId = "e61b65a0-0f1e-4469-94f0-0be82d71b909";

        svc.BroadcastChunk(
            id,
            "work",
            $$"""{"type":"system","subtype":"init","session_id":"{{SessionId}}"}""");

        var tail = svc.GetTail(id);
        Assert.NotNull(tail);
        Assert.DoesNotContain(SessionId, tail);
        Assert.Contains("\"session_id\":\"***\"", tail);
    }

    [Fact]
    public void BroadcastChunk_MultipleSeparateItems_IndependentBuffers()
    {
        var (svc, _) = MakeSvc();
        var id1 = WorkItemId.New();
        var id2 = WorkItemId.New();

        svc.BroadcastChunk(id1, "work", "item1");
        svc.BroadcastChunk(id2, "work", "item2");

        Assert.Contains("item1", svc.GetTail(id1)!);
        Assert.DoesNotContain("item2", svc.GetTail(id1)!);
        Assert.Contains("item2", svc.GetTail(id2)!);
        Assert.DoesNotContain("item1", svc.GetTail(id2)!);
    }

    // ── SignalR group routing ─────────────────────────────────────────────────

    [Fact]
    public async Task CompleteAsync_SendsStreamCompleteToCorrectGroup()
    {
        var (svc, hub) = MakeSvc();
        var id = WorkItemId.New();

        await svc.CompleteAsync(id);

        Assert.Contains(hub.Clients.Sent,
            m => m.Group == $"wi:{id}" && m.Method == "streamComplete");
    }

    [Fact]
    public async Task CompleteAsync_FlushesAndSendsChunkBeforeComplete()
    {
        var (svc, hub) = MakeSvc();
        var id = WorkItemId.New();

        svc.BroadcastChunk(id, "work", "agent output\n");
        await svc.CompleteAsync(id);

        var sent = hub.Clients.Sent.Where(m => m.Group == $"wi:{id}").ToList();
        Assert.Contains(sent, m => m.Method == "stdoutChunk");
        Assert.Contains(sent, m => m.Method == "streamComplete");
        // chunk must come before complete
        var chunkIdx = sent.FindIndex(m => m.Method == "stdoutChunk");
        var completeIdx = sent.FindIndex(m => m.Method == "streamComplete");
        Assert.True(chunkIdx < completeIdx);
    }

    [Fact]
    public async Task CompleteAsync_DifferentItems_MessagesGoToSeparateGroups()
    {
        var (svc, hub) = MakeSvc();
        var id1 = WorkItemId.New();
        var id2 = WorkItemId.New();

        svc.BroadcastChunk(id1, "work", "item1 output");
        svc.BroadcastChunk(id2, "work", "item2 output");
        await svc.CompleteAsync(id1);
        await svc.CompleteAsync(id2);

        var id1Groups = hub.Clients.Sent.Select(m => m.Group).Where(g => g == $"wi:{id1}").ToList();
        var id2Groups = hub.Clients.Sent.Select(m => m.Group).Where(g => g == $"wi:{id2}").ToList();
        Assert.NotEmpty(id1Groups);
        Assert.NotEmpty(id2Groups);
        // No messages from id2 in id1's group and vice versa
        Assert.DoesNotContain(hub.Clients.Sent, m => m.Group == $"wi:{id1}" && m.Group == $"wi:{id2}");
    }

    [Fact]
    public async Task CompleteAsync_EmptyItem_OnlySendsStreamComplete()
    {
        var (svc, hub) = MakeSvc();
        var id = WorkItemId.New();

        await svc.CompleteAsync(id);

        var sent = hub.Clients.Sent.Where(m => m.Group == $"wi:{id}").ToList();
        Assert.DoesNotContain(sent, m => m.Method == "stdoutChunk");
        Assert.Contains(sent, m => m.Method == "streamComplete");
    }

    [Fact]
    public async Task SupervisionNotifications_RouteToFleetAndSessionGroups()
    {
        var (svc, hub) = MakeSvc();
        var snapshot = new AgentSupervisionSessionSnapshot(
            SessionId: "ags-session",
            WorkItemId: WorkItemId.New().ToString(),
            ProjectId: "project",
            Phase: "work",
            Iteration: 1,
            Agent: "claude",
            AgentInstanceId: null,
            ModelId: null,
            ReasoningMode: null,
            SandboxId: "sandbox",
            WorkingDirectory: "/work",
            Source: "test",
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: null,
            State: "running",
            AcceptingInjections: true,
            PendingInjections: 0,
            OutputTail: "",
            RecentCommands: []);

        await svc.SessionStartedAsync(snapshot);

        var sent = Assert.Single(hub.Clients.Sent, m => m.Method == "supervisionSessionStarted");
        Assert.Contains("supervision:all", sent.Group);
        Assert.Contains("supervision:session:ags-session", sent.Group);
    }
}

// ── Fake SignalR infrastructure ───────────────────────────────────────────────

internal sealed class FakeHubContext : IHubContext<AgentStdoutHub>
{
    public CapturingHubClients Clients { get; } = new();
    IHubClients IHubContext<AgentStdoutHub>.Clients => Clients;
    public IGroupManager Groups { get; } = new FakeGroupManager();
}

internal sealed class CapturingHubClients : IHubClients
{
    private readonly List<(string Group, string Method, object?[] Args)> _sent = [];

    public List<(string Group, string Method, object?[] Args)> Sent
    {
        get
        {
            lock (_sent)
            {
                return new List<(string Group, string Method, object?[] Args)>(_sent);
            }
        }
    }

    private IClientProxy Proxy(string groupName) => new CapturingClientProxy(groupName, _sent);

    IClientProxy IHubClients<IClientProxy>.All => Proxy("*all*");
    IClientProxy IHubClients<IClientProxy>.AllExcept(IReadOnlyList<string> excluded) => Proxy("*all-except*");
    IClientProxy IHubClients<IClientProxy>.Client(string connectionId) => Proxy($"client:{connectionId}");
    IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string> connectionIds) => Proxy("*clients*");
    IClientProxy IHubClients<IClientProxy>.Group(string groupName) => Proxy(groupName);
    IClientProxy IHubClients<IClientProxy>.GroupExcept(string groupName, IReadOnlyList<string> excluded) => Proxy(groupName);
    IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string> groupNames) => Proxy(string.Join(",", groupNames));
    IClientProxy IHubClients<IClientProxy>.User(string userId) => Proxy($"user:{userId}");
    IClientProxy IHubClients<IClientProxy>.Users(IReadOnlyList<string> userIds) => Proxy("*users*");
}

internal sealed class CapturingClientProxy : IClientProxy
{
    private readonly string _group;
    private readonly List<(string Group, string Method, object?[] Args)> _sent;

    public CapturingClientProxy(string group, List<(string Group, string Method, object?[] Args)> sent)
    {
        _group = group;
        _sent = sent;
    }

    public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
    {
        lock (_sent) _sent.Add((_group, method, args));
        return Task.CompletedTask;
    }
}

internal sealed class FakeGroupManager : IGroupManager
{
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default)
        => Task.CompletedTask;
}
