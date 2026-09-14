using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <c>agentClassId</c> on PATCH /workitems/{id}: an operator can move
/// an item to a different agent class on any non-terminal state with no worker
/// bound (notably <c>WorkComplete</c> items parked behind an auditor class whose
/// members are all unavailable), unknown ids 400, worker-held items 409, and
/// the change is recorded in the audit log with old and new values.
/// </summary>
public sealed class PatchWorkItemAgentClassTests : IDisposable
{
    private const string OldClass = "old-class";
    private const string NewClass = "new-class";

    private readonly WorkItemApiFactory _factory = new();
    private readonly List<IDisposable> _disposables = new();
    private readonly TestSink _sink = new();

    public void Dispose()
    {
        foreach (var d in _disposables)
            d.Dispose();
        _factory.Dispose();
    }

    private static WorkItem Sample(WorkItemState state, string? agentClassId) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "original title",
        Prompt = "original prompt",
        Agent = AgentKind.Codex,
        AgentClassId = agentClassId,
        State = state,
    };

    private static Project SampleProject() => new()
    {
        Id = new ProjectId("proj"),
        DisplayName = "Test Project",
        RepositoryUrl = "https://github.com/test/repo",
    };

    private (HttpClient Client, IServiceProvider Services) CreateClassClient()
    {
        var customised = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:AgentClasses:0:Id"] = OldClass,
                    ["CodeyBox:AgentClasses:0:DisplayName"] = "Old",
                    ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "codex",
                    ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
                    ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
                    ["CodeyBox:AgentClasses:1:Id"] = NewClass,
                    ["CodeyBox:AgentClasses:1:DisplayName"] = "New",
                    ["CodeyBox:AgentClasses:1:Members:0:Agent"] = "claude",
                    ["CodeyBox:AgentClasses:1:Members:0:Billing"] = "Subscription",
                    ["CodeyBox:AgentClasses:1:Members:0:QualityScore"] = "100",
                });
            });
        });
        _disposables.Add(customised);
        var client = customised.CreateClient();
        _disposables.Add(client);
        return (client, customised.Services);
    }

    private sealed class FixedQuotaProbe(AgentKind kind, double availablePct) : IAgentQuotaProbe
    {
        public AgentKind Kind { get; } = kind;

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
            => Task.FromResult(new AgentQuotaSnapshot { AvailablePct = availablePct });
    }

    // Local router over the same two-class catalog the server is configured
    // with. The server's own router is not used here: in this environment its
    // availability gate benches the members (no installed CLIs/credentials),
    // which would make the routing assertion about the environment rather
    // than about the persisted class id.
    private static AgentClassRouter BuildRouter() => new(
        [
            new AgentClass
            {
                Id = OldClass,
                DisplayName = "Old",
                Members = [new() { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 100 }],
            },
            new AgentClass
            {
                Id = NewClass,
                DisplayName = "New",
                Members = [new() { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 }],
            },
        ],
        [new FixedQuotaProbe(AgentKind.Codex, 80), new FixedQuotaProbe(AgentKind.Claude, 80)],
        new QuotaRouterOptions { MinQuotaPct = 10.0, QuotaRecheckInterval = TimeSpan.FromMinutes(5) },
        NullLogger<AgentClassRouter>.Instance);

    [Fact]
    public async Task PatchAgentClass_OnWorkCompleteWithoutWorker_RouterConsidersNewClassMembers()
    {
        var (client, _) = CreateClassClient();
        var item = Sample(WorkItemState.WorkComplete, OldClass);
        await _factory.Store.CreateAsync(item);

        var router = BuildRouter();
        var project = SampleProject();

        var before = await router.ResolveAsync(item, project, CancellationToken.None);
        Assert.NotNull(before.Chosen);
        Assert.Equal(AgentKind.Codex, before.Chosen!.Agent);

        var response = await client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { agentClassId = NewClass });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(NewClass, stored!.AgentClassId);
        Assert.Equal(WorkItemState.WorkComplete, stored.State);

        var after = await router.ResolveAsync(stored, project, CancellationToken.None);
        Assert.NotNull(after.Chosen);
        Assert.Equal(AgentKind.Claude, after.Chosen!.Agent);
    }

    [Fact]
    public async Task PatchAgentClass_UnknownClass_Returns400AndLeavesItemUnchanged()
    {
        var (client, _) = CreateClassClient();
        var item = Sample(WorkItemState.Queued, OldClass);
        await _factory.Store.CreateAsync(item);

        var response = await client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { agentClassId = "no-such-class" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("unknown agent class", body);

        var stored = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(OldClass, stored!.AgentClassId);
        Assert.Equal(WorkItemState.Queued, stored.State);
    }

    [Fact]
    public async Task PatchAgentClass_WhenWorkerBound_Returns409AndLeavesItemUnchanged()
    {
        var (client, services) = CreateClassClient();
        var item = Sample(WorkItemState.WorkComplete, OldClass);
        await _factory.Store.CreateAsync(item);

        var registry = services.GetRequiredService<IWorkerRegistry>();
        var now = DateTimeOffset.UtcNow;
        await registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = "worker-1",
            HostName = "test-host",
            ProcessId = 1234,
            StartedAt = now,
            LastHeartbeatAt = now,
            CurrentWorkItemId = item.Id.ToString(),
        });

        var response = await client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { agentClassId = NewClass });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("holds", body);

        var stored = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(OldClass, stored!.AgentClassId);
        Assert.Equal(WorkItemState.WorkComplete, stored.State);
    }

    [Fact]
    public async Task PatchAgentClass_EmitsAuditEventWithOldAndNewClass()
    {
        var (client, _) = CreateClassClient();

        // The endpoint emits through the process-global Serilog logger (the
        // test's AsyncLocal scoped logger does not flow to the TestServer
        // pipeline), so swap the global for a sink-backed logger around each
        // PATCH. Each attempt uses a fresh item and matches on its id, so a
        // concurrent host boot in another collection reassigning Log.Logger
        // mid-call retries instead of flaking.
        using var testLogger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();

        LogEvent? found = null;
        for (var attempt = 0; attempt < 5 && found is null; attempt++)
        {
            _sink.Clear();
            var item = Sample(WorkItemState.WorkComplete, OldClass);
            await _factory.Store.CreateAsync(item);

            var previous = Log.Logger;
            Log.Logger = testLogger;
            try
            {
                var response = await client.PatchAsJsonAsync(
                    $"/workitems/{item.Id}",
                    new { agentClassId = NewClass });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            finally
            {
                Log.Logger = previous;
            }

            found = _sink.Events.FirstOrDefault(e =>
                Scalar<string>(e, "EventName") == "work_item.agent_class_changed"
                && Scalar<string>(e, "WorkItemId") == item.Id.ToString());
        }

        Assert.True(found is not null, "expected work_item.agent_class_changed in the audit log");
        Assert.Equal(OldClass, Scalar<string>(found!, "OldAgentClassId"));
        Assert.Equal(NewClass, Scalar<string>(found, "NewAgentClassId"));
    }

    [Fact]
    public async Task PatchAgentClass_OnQueuedItemWithOtherFields_PersistsBoth()
    {
        // The Queued guarded row UPDATE carries agent_class_id, so a combined
        // PATCH must persist the class alongside the other queued edits.
        var (client, _) = CreateClassClient();
        var item = Sample(WorkItemState.Queued, OldClass);
        await _factory.Store.CreateAsync(item);

        var response = await client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { title = "new title", agentClassId = NewClass });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await _factory.Store.GetAsync(item.Id);
        Assert.Equal("new title", stored!.Title);
        Assert.Equal(NewClass, stored.AgentClassId);
        Assert.Equal(WorkItemState.Queued, stored.State);
    }

    [Fact]
    public async Task PatchAgentClass_OnTerminalItem_Returns409AndLeavesItemUnchanged()
    {
        var (client, _) = CreateClassClient();
        var item = Sample(WorkItemState.Done, OldClass);
        await _factory.Store.CreateAsync(item);

        var response = await client.PatchAsJsonAsync(
            $"/workitems/{item.Id}",
            new { agentClassId = NewClass });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var stored = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(OldClass, stored!.AgentClassId);
        Assert.Equal(WorkItemState.Done, stored.State);
    }

    private static T? Scalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return default;
        if (sv.Value is T t)
            return t;
        return default;
    }
}
