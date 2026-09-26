using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CodeyBox.Api;
using CodeyBox.Api.Majordomo;
using CodeyBox.Core;
using CodeyBox.Majordomo;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ControllableTime = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

/// <summary>
/// Acceptance coverage for the durable majordomo proposal queue: approval
/// commits through the same backend path as Autonomous mode, apply-time
/// revalidation refuses moved preconditions, expired proposals cannot be
/// approved, failed chain approvals file nothing, rejections mutate nothing,
/// and repeated approvals are idempotent — all against the real store and
/// the real mutate backend, never mocks.
/// </summary>
public sealed class MajordomoProposalQueueTests
{
    private const string McpPath = "/mcp/majordomo";

    private static readonly WorkInitiator TestInitiator = new()
    {
        Issuer = "test",
        Subject = "proposal-queue-tests",
        DisplayName = "Proposal queue tests",
    };

    private static async Task<McpClient> ConnectAsync(HttpClient http)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, McpPath),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            http,
            loggerFactory: null,
            ownsHttpClient: false);
        return await McpClient.CreateAsync(transport);
    }

    private static JsonElement Structured(CallToolResult result)
    {
        if (result.StructuredContent is { } structured)
            return structured;
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        Assert.NotNull(text);
        return JsonSerializer.Deserialize<JsonElement>(text!);
    }

    private static string OutcomeOf(CallToolResult result)
        => Structured(result).GetProperty("outcome").GetString()!;

    private static async Task<List<WorkItem>> ListAllAsync(WorkItemApiFactory factory)
    {
        var rows = new List<WorkItem>();
        await foreach (var item in factory.Store.ListAsync())
            rows.Add(item);
        return rows;
    }

    private static WorkItem Seed(WorkItemState state, string title) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = title,
        Prompt = "prompt for " + title,
        State = state,
        DependsOn = [],
    };

    private static Dictionary<string, object?> Spec(string title) => new()
    {
        ["project_id"] = "test-project",
        ["title"] = title,
        ["prompt"] = "do " + title,
    };

    private static Dictionary<string, object?> Merge(
        Dictionary<string, object?> spec, params (string Key, object? Value)[] extra)
    {
        var copy = new Dictionary<string, object?>(spec);
        foreach (var (k, v) in extra)
            copy[k] = v;
        return copy;
    }

    private static WebApplicationFactory<Program> AutonomousFactory(WorkItemApiFactory inner)
    {
        var values = new Dictionary<string, string?>
        {
            ["CodeyBox:Majordomo:Mode"] = "autonomous",
        };
        return inner.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(values)));
    }

    private static MajordomoProposalService ManualService(
        WorkItemApiFactory factory,
        IMajordomoProposalStore store,
        ControllableTime time,
        int ttlSeconds = 3600) =>
        new(
            store,
            factory.Services.GetRequiredService<MajordomoMutateBackend>(),
            MonitorOf(new MajordomoServerOptions { ProposalTimeToLiveSeconds = ttlSeconds }),
            time);

    private static IOptionsMonitor<MajordomoServerOptions> MonitorOf(MajordomoServerOptions options)
        => new StubOptionsMonitor(options);

    private sealed class StubOptionsMonitor(MajordomoServerOptions value) : IOptionsMonitor<MajordomoServerOptions>
    {
        public MajordomoServerOptions CurrentValue => value;
        public MajordomoServerOptions Get(string? name) => value;
        public IDisposable OnChange(Action<MajordomoServerOptions, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private static NewWorkItemSpec ItemSpec(string title) =>
        new(new ProjectId("test-project"), title, "do " + title);

    // ── 1. approval == autonomous ────────────────────────────────────────────

    [Fact]
    public async Task Approval_ProducesIdenticalStoreState_AsAutonomousExecution()
    {
        using var autoFactory = new WorkItemApiFactory();
        using var autoHost = AutonomousFactory(autoFactory);
        using var propFactory = new WorkItemApiFactory();

        var chainArgs = new Dictionary<string, object?>
        {
            ["items"] = new object[]
            {
                new Dictionary<string, object?> { ["item"] = Spec("chain a"), ["depends_on_indexes"] = Array.Empty<int>() },
                new Dictionary<string, object?> { ["item"] = Spec("chain b"), ["depends_on_indexes"] = new[] { 0 } },
            },
        };

        using (var http = autoHost.CreateClient())
        {
            await using var mcp = await ConnectAsync(http);
            var executed = await mcp.CallToolAsync("create_work_item_chain", chainArgs);
            Assert.True(OutcomeOf(executed) == "executed", Structured(executed).GetRawText());
        }

        string proposalId;
        using (var http = propFactory.CreateClient())
        {
            await using var mcp = await ConnectAsync(http);
            var proposed = await mcp.CallToolAsync("create_work_item_chain", chainArgs);
            Assert.True(OutcomeOf(proposed) == "proposed", Structured(proposed).GetRawText());
            proposalId = Structured(proposed).GetProperty("proposal").GetProperty("proposal_id").GetString()!;
        }

        var service = propFactory.Services.GetRequiredService<MajordomoProposalService>();
        var approval = await service.ApproveAsync(proposalId, "test-operator", TestInitiator);
        Assert.Null(approval.Refusal);
        Assert.NotNull(approval.ChangeSet);
        Assert.Equal(2, approval.ChangeSet.AffectedItems.Count);

        // Same semantic queue content on both sides: titles, prompts,
        // project, states, and the dependency edge re-pointed at the new ids.
        var autoItems = (await ListAllAsync(autoFactory)).OrderBy(i => i.Title).ToList();
        var propItems = (await ListAllAsync(propFactory)).OrderBy(i => i.Title).ToList();
        Assert.Equal(2, autoItems.Count);
        Assert.Equal(2, propItems.Count);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(autoItems[i].Title, propItems[i].Title);
            Assert.Equal(autoItems[i].Prompt, propItems[i].Prompt);
            Assert.Equal(autoItems[i].ProjectId, propItems[i].ProjectId);
            Assert.Equal(autoItems[i].State, propItems[i].State);
            Assert.Equal(autoItems[i].DependsOn.Count, propItems[i].DependsOn.Count);
        }

        var autoByTitle = autoItems.ToDictionary(i => i.Title);
        var propByTitle = propItems.ToDictionary(i => i.Title);
        Assert.Equal(
            [autoByTitle["chain a"].Id],
            autoByTitle["chain b"].DependsOn);
        Assert.Equal(
            [propByTitle["chain a"].Id],
            propByTitle["chain b"].DependsOn);

        // The proposal itself is recorded approved with the committed ids.
        var store = propFactory.Services.GetRequiredService<IMajordomoProposalStore>();
        var record = await store.GetAsync(proposalId);
        Assert.NotNull(record);
        Assert.Equal(MajordomoProposalState.Approved, record!.State);
        Assert.Equal(
            propItems.Select(i => i.Id).OrderBy(id => id.ToString()),
            record.ResultAffectedItems!.OrderBy(id => id.ToString()));
    }

    // ── 2. moved precondition refused at apply ───────────────────────────────

    [Fact]
    public async Task Approval_AfterTargetReachedTerminal_IsRefused_AndQueueUnchanged()
    {
        using var factory = new WorkItemApiFactory();
        var target = Seed(WorkItemState.Queued, "cancel target");
        await factory.Store.CreateAsync(target);

        string proposalId;
        using (var http = factory.CreateClient())
        {
            await using var mcp = await ConnectAsync(http);
            var proposed = await mcp.CallToolAsync("cancel_work_item", new Dictionary<string, object?>
            {
                ["id"] = target.Id.ToString(),
                ["reason"] = "operator asked for this",
            });
            Assert.True(OutcomeOf(proposed) == "proposed", Structured(proposed).GetRawText());
            proposalId = Structured(proposed).GetProperty("proposal").GetProperty("proposal_id").GetString()!;
        }

        // The queue moves between proposal and approval: the target completes.
        var done = target with { State = WorkItemState.Done };
        await factory.Store.UpdateAsync(done);

        var service = factory.Services.GetRequiredService<MajordomoProposalService>();
        var approval = await service.ApproveAsync(proposalId, "test-operator", TestInitiator);

        // Refused with a reason naming the moved precondition …
        Assert.NotNull(approval.Refusal);
        Assert.Equal("conflict", approval.Refusal!.Reason);
        Assert.Contains("Done", approval.Refusal.Detail);

        // … and the queue is unchanged: the target is still Done, nothing else filed.
        var stored = await factory.Store.GetAsync(target.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Done, stored!.State);
        Assert.Equal([target.Id], (await ListAllAsync(factory)).Select(i => i.Id));

        // The proposal stays pending for the operator to reject or retry.
        var store = factory.Services.GetRequiredService<IMajordomoProposalStore>();
        var record = await store.GetAsync(proposalId);
        Assert.NotNull(record);
        Assert.Equal(MajordomoProposalState.Pending, record!.State);
    }

    // ── 3. expiry ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExpiredProposal_CannotBeApproved()
    {
        using var factory = new WorkItemApiFactory();
        var store = new InMemoryMajordomoProposalStore();
        var time = new ControllableTime(DateTimeOffset.UtcNow);
        var service = ManualService(factory, store, time, ttlSeconds: 3600);

        var record = await service.ProposeAsync(
            MajordomoTools.CreateWorkItem,
            new CreateWorkItemArgs(ItemSpec("doomed item")),
            "majordomo",
            CancellationToken.None);
        Assert.Equal(MajordomoProposalState.Pending, (await store.GetAsync(record.Id))!.State);

        time.Advance(TimeSpan.FromHours(2));

        var approval = await service.ApproveAsync(record.Id, "test-operator", TestInitiator);
        Assert.NotNull(approval.Refusal);
        Assert.Equal("proposal_expired", approval.Refusal!.Reason);

        // Nothing reached the queue, and the proposal is marked expired …
        Assert.Empty(await ListAllAsync(factory));
        var expired = await store.GetAsync(record.Id);
        Assert.NotNull(expired);
        Assert.Equal(MajordomoProposalState.Expired, expired!.State);

        // … so a later approval is refused as already decided, still mutating nothing.
        var again = await service.ApproveAsync(record.Id, "test-operator", TestInitiator);
        Assert.NotNull(again.Refusal);
        Assert.Equal("proposal_not_pending", again.Refusal!.Reason);
        Assert.Empty(await ListAllAsync(factory));
    }

    // ── 4. chain atomicity ───────────────────────────────────────────────────

    [Fact]
    public async Task ChainProposal_FailingPartway_LeavesNoItemsBehind()
    {
        using var factory = new WorkItemApiFactory();
        var anchor = Seed(WorkItemState.Queued, "anchor");
        await factory.Store.CreateAsync(anchor);

        string proposalId;
        using (var http = factory.CreateClient())
        {
            await using var mcp = await ConnectAsync(http);
            var proposed = await mcp.CallToolAsync("create_work_item_chain", new Dictionary<string, object?>
            {
                ["items"] = new object[]
                {
                    new Dictionary<string, object?> { ["item"] = Spec("chain head"), ["depends_on_indexes"] = Array.Empty<int>() },
                    new Dictionary<string, object?>
                    {
                        ["item"] = Merge(Spec("chain tail"), ("depends_on", new[] { anchor.Id.ToString() })),
                        ["depends_on_indexes"] = new[] { 0 },
                    },
                },
            });
            Assert.True(OutcomeOf(proposed) == "proposed", Structured(proposed).GetRawText());
            proposalId = Structured(proposed).GetProperty("proposal").GetProperty("proposal_id").GetString()!;
        }

        // The anchor completes before approval, so the tail's dependency can
        // never satisfy — the failure surfaces while the head is already
        // prepared, which is exactly the partway point that must file nothing.
        await factory.Store.UpdateAsync(anchor with { State = WorkItemState.Done });

        var service = factory.Services.GetRequiredService<MajordomoProposalService>();
        var approval = await service.ApproveAsync(proposalId, "test-operator", TestInitiator);

        Assert.NotNull(approval.Refusal);
        Assert.Equal("dependency_terminal", approval.Refusal!.Reason);

        // Only the anchor remains: neither the prepared head nor the tail leaked.
        Assert.Equal([anchor.Id], (await ListAllAsync(factory)).Select(i => i.Id));
    }

    // ── rejection mutates nothing ────────────────────────────────────────────

    [Fact]
    public async Task Rejection_RecordsOutcome_AndMutatesNothing()
    {
        using var factory = new WorkItemApiFactory();
        var store = new InMemoryMajordomoProposalStore();
        var time = new ControllableTime(DateTimeOffset.UtcNow);
        var service = ManualService(factory, store, time);

        var record = await service.ProposeAsync(
            MajordomoTools.CreateWorkItem,
            new CreateWorkItemArgs(ItemSpec("unwanted item")),
            "majordomo",
            CancellationToken.None);

        var rejection = await service.RejectAsync(record.Id, "test-operator", "not needed anymore");
        Assert.Null(rejection.Refusal);
        Assert.Equal(MajordomoProposalState.Rejected, rejection.Record!.State);
        Assert.Equal("not needed anymore", rejection.Record.DecisionReason);
        Assert.Equal("test-operator", rejection.Record.DecidedBy);
        Assert.Empty(await ListAllAsync(factory));

        var approval = await service.ApproveAsync(record.Id, "test-operator", TestInitiator);
        Assert.NotNull(approval.Refusal);
        Assert.Equal("proposal_not_pending", approval.Refusal!.Reason);
        Assert.Empty(await ListAllAsync(factory));
    }

    // ── idempotent approve ───────────────────────────────────────────────────

    [Fact]
    public async Task DoubleApproval_IsIdempotent()
    {
        using var factory = new WorkItemApiFactory();
        var store = new InMemoryMajordomoProposalStore();
        var time = new ControllableTime(DateTimeOffset.UtcNow);
        var service = ManualService(factory, store, time);

        var record = await service.ProposeAsync(
            MajordomoTools.CreateWorkItem,
            new CreateWorkItemArgs(ItemSpec("once only")),
            "majordomo",
            CancellationToken.None);

        var first = await service.ApproveAsync(record.Id, "test-operator", TestInitiator);
        Assert.Null(first.Refusal);
        Assert.NotNull(first.ChangeSet);
        var committedId = Assert.Single(first.ChangeSet!.AffectedItems);

        var second = await service.ApproveAsync(record.Id, "test-operator", TestInitiator);
        Assert.Null(second.Refusal);
        Assert.Equal([committedId], second.AlreadyApprovedIds);

        // Still exactly one item: the replay committed nothing new.
        Assert.Equal([committedId], (await ListAllAsync(factory)).Select(i => i.Id));
    }

    // ── durability ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SqliteStore_RoundTrips_ProposalsAcrossReopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mjd-prop-{Guid.NewGuid():N}.db");
        var now = DateTimeOffset.UtcNow;
        string id;
        using (var store = new SqliteMajordomoProposalStore(path))
        {
            var record = MajordomoProposalRecord.Create(
                "create_work_item",
                new CreateWorkItemArgs(ItemSpec("durable item")) { Reasoning = "because tests" },
                "majordomo",
                now);
            id = record.Id;
            await store.EnqueueAsync(record);
        }

        try
        {
            using (var reopened = new SqliteMajordomoProposalStore(path))
            {
                var loaded = await reopened.GetAsync(id);
                Assert.NotNull(loaded);
                Assert.Equal(id, loaded!.Id);
                Assert.Equal("create_work_item", loaded.ToolName);
                Assert.Equal("because tests", loaded.Reasoning);
                Assert.Equal(MajordomoProposalState.Pending, loaded.State);
                Assert.Equal("majordomo", loaded.ProposedBy);
                Assert.Equal(now, loaded.ProposedAt);
                var create = Assert.IsType<CreateWorkItemArgs>(loaded.Arguments);
                Assert.Equal("durable item", create.Item.Title);
            }
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
            TestScratchDirectory.DeleteSqliteCompanions(path);
        }
    }

    // ── MCP wiring: proposal ids resolve ─────────────────────────────────────

    [Fact]
    public async Task ProposedMode_McpResponse_CarriesDurableProposalId()
    {
        using var factory = new WorkItemApiFactory();
        var queued = Seed(WorkItemState.Queued, "wired target");
        await factory.Store.CreateAsync(queued);

        string proposalId;
        using (var http = factory.CreateClient())
        {
            await using var mcp = await ConnectAsync(http);
            var proposed = await mcp.CallToolAsync("update_work_item", new Dictionary<string, object?>
            {
                ["id"] = queued.Id.ToString(),
                ["patch"] = new Dictionary<string, object?> { ["title"] = "new title" },
            });
            Assert.True(OutcomeOf(proposed) == "proposed", Structured(proposed).GetRawText());
            var view = Structured(proposed).GetProperty("proposal");
            proposalId = view.GetProperty("proposal_id").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(proposalId));
        }

        var store = factory.Services.GetRequiredService<IMajordomoProposalStore>();
        var record = await store.GetAsync(proposalId);
        Assert.NotNull(record);
        Assert.Equal(MajordomoProposalState.Pending, record!.State);
        Assert.Equal("update_work_item", record.ToolName);
        var update = Assert.IsType<UpdateWorkItemArgs>(record.Arguments);
        Assert.Equal(queued.Id, update.Id);
        Assert.Equal("new title", update.Patch.Title);
    }
}
