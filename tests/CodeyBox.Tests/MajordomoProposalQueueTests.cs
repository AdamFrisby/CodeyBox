using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CodeyBox.Api;
using CodeyBox.Api.Majordomo;
using CodeyBox.Core;
using CodeyBox.Majordomo;
using CodeyBox.Orchestrator;
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

    /// <summary>Files a proposal through the service, asserting it was not refused.</summary>
    private static async Task<MajordomoProposalRecord> ProposeOrThrowAsync(
        MajordomoProposalService service,
        MajordomoTool tool,
        MajordomoMutateArgs args,
        MajordomoChangeSet reviewedChangeSet,
        string proposedBy,
        CancellationToken ct = default)
    {
        var outcome = await service.ProposeAsync(tool, args, reviewedChangeSet, proposedBy, ct);
        Assert.Null(outcome.Refusal);
        return outcome.Record!;
    }

    /// <summary>A pending proposal record for <paramref name="title"/>.</summary>
    private static MajordomoProposalRecord Proposal(string title, DateTimeOffset? at = null) =>
        MajordomoProposalRecord.Create(
            "create_work_item",
            new CreateWorkItemArgs(ItemSpec(title)),
            "majordomo",
            at ?? DateTimeOffset.UtcNow);

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

    /// <summary>The dry-run change set a proposal for <paramref name="spec"/> would carry.</summary>
    private static MajordomoChangeSet ReviewedPlan(NewWorkItemSpec spec) =>
        new(DryRun: true, [new MajordomoPlannedChange.CreateItem(spec)], []);

    /// <summary>
    /// Runs the real mutate backend's plan-only arm so the stored proposal
    /// carries exactly the change set proposal-time review would have shown.
    /// </summary>
    private static async Task<MajordomoChangeSet> PlanWithBackendAsync(
        WorkItemApiFactory factory,
        MajordomoTool tool,
        MajordomoMutateArgs args)
    {
        var backend = factory.Services.GetRequiredService<MajordomoMutateBackend>();
        var planned = await backend.MutateAsync(
            tool, args, TestInitiator, cancelCascadeTargets: null, commit: false, CancellationToken.None);
        Assert.Null(planned.Refusal);
        return planned.ChangeSet!;
    }

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

        var record = await ProposeOrThrowAsync(service,
            MajordomoTools.CreateWorkItem,
            new CreateWorkItemArgs(ItemSpec("doomed item")),
            ReviewedPlan(ItemSpec("doomed item")),
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
        // never satisfy — the refusal fires during the all-nodes dependency
        // check, before any node is prepared: the partway point that must
        // file nothing.
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

        var record = await ProposeOrThrowAsync(service,
            MajordomoTools.CreateWorkItem,
            new CreateWorkItemArgs(ItemSpec("unwanted item")),
            ReviewedPlan(ItemSpec("unwanted item")),
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

        var record = await ProposeOrThrowAsync(service,
            MajordomoTools.CreateWorkItem,
            new CreateWorkItemArgs(ItemSpec("once only")),
            ReviewedPlan(ItemSpec("once only")),
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
        var plan = ReviewedPlan(ItemSpec("durable item"));
        using (var store = new SqliteMajordomoProposalStore(path))
        {
            var record = MajordomoProposalRecord.Create(
                "create_work_item",
                new CreateWorkItemArgs(ItemSpec("durable item")) { Reasoning = "because tests" },
                "majordomo",
                now,
                reviewedChangeSet: plan);
            id = record.Id;
            await store.EnqueueAsync(record, new MajordomoOptions(), DateTimeOffset.UtcNow);
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
                // The reviewed change set round-trips — the approval-time
                // drift check compares this against a live re-plan.
                Assert.NotNull(loaded.ReviewedChangeSet);
                Assert.Equal(
                    JsonSerializer.Serialize(plan.Changes, MajordomoJson.Options),
                    JsonSerializer.Serialize(loaded.ReviewedChangeSet!.Changes, MajordomoJson.Options));
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

    // ── concurrency: one proposal, one commit ────────────────────────────────

    [Fact]
    public async Task ConcurrentApprovals_CommitExactlyOnce()
    {
        using var factory = new WorkItemApiFactory();
        var store = new InMemoryMajordomoProposalStore();
        var time = new ControllableTime(DateTimeOffset.UtcNow);
        var service = ManualService(factory, store, time);

        var record = await ProposeOrThrowAsync(service,
            MajordomoTools.CreateWorkItem,
            new CreateWorkItemArgs(ItemSpec("raced approval")),
            ReviewedPlan(ItemSpec("raced approval")),
            "majordomo",
            CancellationToken.None);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            service.ApproveAsync(record.Id, "test-operator", TestInitiator)));

        // Exactly one approval committed; the rest replay the recorded ids.
        var committed = outcomes.Where(o => o.ChangeSet is not null).ToList();
        Assert.Single(committed);
        Assert.All(outcomes.Where(o => o.ChangeSet is null), o =>
        {
            Assert.Null(o.Refusal);
            Assert.NotNull(o.AlreadyApprovedIds);
        });
        Assert.Single(await ListAllAsync(factory));
        Assert.Equal(
            MajordomoProposalState.Approved, (await store.GetAsync(record.Id))!.State);
    }

    [Fact]
    public async Task ApproveAndReject_Concurrently_NeverDiverge()
    {
        using var factory = new WorkItemApiFactory();
        var store = new InMemoryMajordomoProposalStore();
        var time = new ControllableTime(DateTimeOffset.UtcNow);
        var service = ManualService(factory, store, time);

        var record = await ProposeOrThrowAsync(service,
            MajordomoTools.CreateWorkItem,
            new CreateWorkItemArgs(ItemSpec("raced decision")),
            ReviewedPlan(ItemSpec("raced decision")),
            "majordomo",
            CancellationToken.None);

        var approveTask = service.ApproveAsync(record.Id, "test-operator", TestInitiator);
        var rejectTask = service.RejectAsync(record.Id, "test-operator", "changed my mind");
        await Task.WhenAll(approveTask, rejectTask);
        var approval = await approveTask;
        var rejection = await rejectTask;

        var final = (await store.GetAsync(record.Id))!;
        var items = await ListAllAsync(factory);
        if (approval.ChangeSet is not null)
        {
            // Approval claimed first: the commit landed and the record says so.
            Assert.Equal(MajordomoProposalState.Approved, final.State);
            Assert.Single(items);
            Assert.Equal(
                "proposal_not_pending", rejection.Refusal?.Reason);
        }
        else
        {
            // The rejection won: the record is rejected and the queue was
            // never touched — never "rejected but mutated".
            Assert.Equal(MajordomoProposalState.Rejected, final.State);
            Assert.Empty(items);
            Assert.Equal("proposal_not_pending", approval.Refusal?.Reason);
        }
    }

    // ── drift: the committed plan must be the reviewed plan ──────────────────

    [Fact]
    public async Task Approval_WhenReviewedCascadeGrew_IsRefusedAsDrifted()
    {
        using var factory = new WorkItemApiFactory();
        var target = Seed(WorkItemState.Queued, "cancel target");
        var dependent = Seed(WorkItemState.Queued, "dependent one") with { DependsOn = [target.Id] };
        await factory.Store.CreateAsync(target);
        await factory.Store.CreateAsync(dependent);

        var store = new InMemoryMajordomoProposalStore();
        var time = new ControllableTime(DateTimeOffset.UtcNow);
        var service = ManualService(factory, store, time);

        var args = new CancelWorkItemArgs(target.Id, "consolidating work");
        var reviewed = await PlanWithBackendAsync(factory, MajordomoTools.CancelWorkItem, args);
        var record = await ProposeOrThrowAsync(service,
            MajordomoTools.CancelWorkItem, args, reviewed, "majordomo", CancellationToken.None);

        // The blast radius grows between proposal and approval: another item
        // now depends on the cancel target.
        await factory.Store.CreateAsync(
            Seed(WorkItemState.Queued, "dependent two") with { DependsOn = [target.Id] });

        var approval = await service.ApproveAsync(record.Id, "test-operator", TestInitiator);
        Assert.NotNull(approval.Refusal);
        Assert.Equal("proposal_drifted", approval.Refusal!.Reason);

        // Nothing was cancelled; the proposal stays pending for a re-proposal.
        Assert.All(await ListAllAsync(factory), i => Assert.Equal(WorkItemState.Queued, i.State));
        Assert.Equal(MajordomoProposalState.Pending, (await store.GetAsync(record.Id))!.State);
    }

    // ── interrupted commit: refused re-approval, closable ────────────────────

    [Fact]
    public async Task InterruptedCommit_CannotBeReapproved_ButCanBeClosed()
    {
        using var factory = new WorkItemApiFactory();
        var store = new InMemoryMajordomoProposalStore();
        var time = new ControllableTime(DateTimeOffset.UtcNow);
        var service = ManualService(factory, store, time);

        var record = await ProposeOrThrowAsync(service,
            MajordomoTools.CreateWorkItem,
            new CreateWorkItemArgs(ItemSpec("interrupted")),
            ReviewedPlan(ItemSpec("interrupted")),
            "majordomo",
            CancellationToken.None);

        // Simulate a crashed commit: claimed, but the outcome never landed.
        Assert.True(await store.TryTransitionAsync(
            record.Id,
            MajordomoProposalState.Pending,
            record with
            {
                State = MajordomoProposalState.Applying,
                DecisionReason = MajordomoProposalService.CommitInFlightReason,
            }));

        // Re-approval is refused rather than risking a double commit.
        var approval = await service.ApproveAsync(record.Id, "test-operator", TestInitiator);
        Assert.NotNull(approval.Refusal);
        Assert.Equal("proposal_commit_incomplete", approval.Refusal!.Reason);
        Assert.Empty(await ListAllAsync(factory));

        // The operator closes the row out; the record keeps the caveat that
        // the mutation may already have applied.
        var close = await service.RejectAsync(record.Id, "test-operator", "verified nothing landed");
        Assert.Null(close.Refusal);
        Assert.Equal(MajordomoProposalState.Rejected, close.Record!.State);
        Assert.Contains("claimed commit", close.Record.DecisionReason);
        Assert.Empty(await ListAllAsync(factory));
    }

    // ── retired tool: refusal, never a commit path ───────────────────────────

    [Fact]
    public async Task Proposal_WhoseToolLeftTheVocabulary_IsRefusedAsRetired()
    {
        using var factory = new WorkItemApiFactory();
        var store = new InMemoryMajordomoProposalStore();
        var time = new ControllableTime(DateTimeOffset.UtcNow);
        var service = ManualService(factory, store, time);

        // A hand-built row as if the tool had been removed between proposal
        // and approval — the durable-store read path reports this as corrupt,
        // the service path reports the precise reason.
        var record = new MajordomoProposalRecord
        {
            Id = MajordomoProposalRecord.NewId(),
            ToolName = "drain_the_queue",
            Arguments = new CreateWorkItemArgs(ItemSpec("stale")),
            ProposedBy = "majordomo",
            ProposedAt = DateTimeOffset.UtcNow,
        };
        await store.EnqueueAsync(record, new MajordomoOptions(), DateTimeOffset.UtcNow);

        var approval = await service.ApproveAsync(record.Id, "test-operator", TestInitiator);
        Assert.NotNull(approval.Refusal);
        Assert.Equal("proposal_tool_retired", approval.Refusal!.Reason);
        Assert.Empty(await ListAllAsync(factory));
    }

    // ── operator gate: proposal decisions are not the majordomo's ────────────

    private static DefaultHttpContext ContextWith(ApiClientPrincipal principal)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[ApiKeyAuth.PrincipalItemKey] = principal;
        return ctx;
    }

    private static int? StatusCodeOf(IResult? result) =>
        result is null ? null : Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;

    private static WorkInitiator ClientInitiator(string subject) => new()
    {
        Issuer = "test",
        Subject = subject,
        DisplayName = subject,
    };

    [Fact]
    public void ProposalGate_MissingPrincipal_IsUnauthorized()
    {
        var result = MajordomoProposalEndpoints.CheckProposalOperator(
            new DefaultHttpContext(), new MajordomoServerOptions());
        Assert.Equal(StatusCodes.Status401Unauthorized, StatusCodeOf(result));
    }

    [Fact]
    public void ProposalGate_AuthenticationDisabled_IsAllowed()
    {
        var ctx = ContextWith(new ApiClientPrincipal(
            ApiKeyAuth.AuthenticationDisabledClientName,
            ClientInitiator("operator"),
            CanDelegateInitiator: false));
        Assert.Null(MajordomoProposalEndpoints.CheckProposalOperator(ctx, new MajordomoServerOptions()));
    }

    [Fact]
    public void ProposalGate_MajordomoClient_IsForbidden()
    {
        // The credential whose calls proposed mode exists to review must
        // never reach the decision routes.
        var ctx = ContextWith(new ApiClientPrincipal(
            "majordomo", ClientInitiator("majordomo"), CanDelegateInitiator: false));
        var result = MajordomoProposalEndpoints.CheckProposalOperator(ctx, new MajordomoServerOptions());
        Assert.Equal(StatusCodes.Status403Forbidden, StatusCodeOf(result));
    }

    [Fact]
    public void ProposalGate_ExecutorBoundClient_IsForbidden()
    {
        // A host-bound token proves which executor is calling, not that an
        // operator is deciding.
        var ctx = ContextWith(new ApiClientPrincipal(
            "executor-1", ClientInitiator("executor-1"), CanDelegateInitiator: false,
            ExecutorHostId: "exec-1"));
        var result = MajordomoProposalEndpoints.CheckProposalOperator(ctx, new MajordomoServerOptions());
        Assert.Equal(StatusCodes.Status403Forbidden, StatusCodeOf(result));
    }

    [Fact]
    public void ProposalGate_OperatorAndOtherClients_AreAllowed()
    {
        var options = new MajordomoServerOptions();
        var legacy = ContextWith(new ApiClientPrincipal(
            "legacy-operator", ClientInitiator("operator"), CanDelegateInitiator: false));
        Assert.Null(MajordomoProposalEndpoints.CheckProposalOperator(legacy, options));

        var other = ContextWith(new ApiClientPrincipal(
            "audit-runner", ClientInitiator("audit-runner"), CanDelegateInitiator: false));
        Assert.Null(MajordomoProposalEndpoints.CheckProposalOperator(other, options));
    }

    // ── operator gate over real HTTP wiring ──────────────────────────────────

    [Fact]
    public async Task ProposalEndpoints_RefuseMajordomoAndExecutorTokens_AcceptOperator()
    {
        const string operatorKey = "test-token-operator-proposal-gate-00";
        const string majordomoKey = "test-token-majordomo-proposal-gate-0";
        const string executorKey = "test-token-executor-proposal-gate-00";
        using var envOperator = new EnvironmentVariableScope("CODEYBOX_API_KEY", operatorKey);
        using var envMajordomo = new EnvironmentVariableScope("CODEYBOX_TEST_MAJORDOMO_TOKEN", majordomoKey);
        using var envExecutor = new EnvironmentVariableScope("CODEYBOX_TEST_EXECUTOR_TOKEN", executorKey);

        using var inner = new WorkItemApiFactory();
        using var host = inner.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "false",
                    ["CodeyBox:ApiClients:0:Name"] = "majordomo",
                    ["CodeyBox:ApiClients:0:TokenEnvVar"] = "CODEYBOX_TEST_MAJORDOMO_TOKEN",
                    ["CodeyBox:ApiClients:0:Principal:Issuer"] = "test",
                    ["CodeyBox:ApiClients:0:Principal:Subject"] = "majordomo",
                    ["CodeyBox:ApiClients:0:Principal:DisplayName"] = "Test majordomo",
                    ["CodeyBox:ApiClients:1:Name"] = "executor-1",
                    ["CodeyBox:ApiClients:1:TokenEnvVar"] = "CODEYBOX_TEST_EXECUTOR_TOKEN",
                    ["CodeyBox:ApiClients:1:Principal:Issuer"] = "test",
                    ["CodeyBox:ApiClients:1:Principal:Subject"] = "executor-1",
                    ["CodeyBox:ApiClients:1:Principal:DisplayName"] = "Test executor",
                    ["CodeyBox:ApiClients:1:ExecutorHostId"] = "exec-1",
                })));

        var spec = ItemSpec("gated proposal");
        var proposal = MajordomoProposalRecord.Create(
            "create_work_item",
            new CreateWorkItemArgs(spec),
            "majordomo",
            DateTimeOffset.UtcNow,
            reviewedChangeSet: ReviewedPlan(spec));
        await host.Services.GetRequiredService<IMajordomoProposalStore>()
            .EnqueueAsync(proposal, new MajordomoOptions(), DateTimeOffset.UtcNow);

        using var http = host.CreateClient();

        // The majordomo credential is refused on every proposal route.
        http.DefaultRequestHeaders.Authorization = new("Bearer", majordomoKey);
        var response = await http.PostAsync(
            $"/majordomo/proposals/{proposal.Id}/approve", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        response = await http.PostAsJsonAsync(
            $"/majordomo/proposals/{proposal.Id}/reject", new { reason = "self-approval" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        response = await http.GetAsync($"/majordomo/proposals/{proposal.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // An executor-bound token is refused too.
        http.DefaultRequestHeaders.Authorization = new("Bearer", executorKey);
        response = await http.PostAsync(
            $"/majordomo/proposals/{proposal.Id}/approve", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // None of the refused calls touched the proposal or the queue.
        var record = await host.Services.GetRequiredService<IMajordomoProposalStore>()
            .GetAsync(proposal.Id);
        Assert.Equal(MajordomoProposalState.Pending, record!.State);
        Assert.Empty(await ListAllAsync(inner));

        // The operator key decides.
        http.DefaultRequestHeaders.Authorization = new("Bearer", operatorKey);
        response = await http.PostAsJsonAsync(
            $"/majordomo/proposals/{proposal.Id}/reject", new { reason = "not needed" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        record = await host.Services.GetRequiredService<IMajordomoProposalStore>()
            .GetAsync(proposal.Id);
        Assert.Equal(MajordomoProposalState.Rejected, record!.State);
        Assert.Empty(await ListAllAsync(inner));
    }

    // ── queue bounds: the enqueue path is capped and self-sweeping ─────────

    [Fact]
    public async Task Enqueue_WhenQueueIsFull_RefusesAtomically()
    {
        var store = new InMemoryMajordomoProposalStore();
        var policy = new MajordomoOptions { MaxPendingProposals = 2 };
        var now = DateTimeOffset.UtcNow;

        await store.EnqueueAsync(Proposal("one"), policy, now);
        await store.EnqueueAsync(Proposal("two"), policy, now);

        var ex = await Assert.ThrowsAsync<MajordomoProposalQueueFullException>(
            () => store.EnqueueAsync(Proposal("three"), policy, now));
        Assert.Equal(2, ex.MaxPending);
        Assert.Equal(2, (await store.ListAsync()).Count);
    }

    [Fact]
    public async Task Enqueue_ReapsExpiredPendingRows_FreeingCapacity()
    {
        var store = new InMemoryMajordomoProposalStore();
        var policy = new MajordomoOptions
        {
            MaxPendingProposals = 1,
            ProposalTimeToLive = TimeSpan.FromMinutes(5),
        };
        var t0 = DateTimeOffset.UtcNow;

        await store.EnqueueAsync(Proposal("stale", t0), policy, t0);
        // While the stale row is undecided the cap refuses new proposals …
        await Assert.ThrowsAsync<MajordomoProposalQueueFullException>(
            () => store.EnqueueAsync(Proposal("blocked", t0), policy, t0));

        // … but once it is past its TTL the next enqueue reaps it and lands.
        var later = t0.AddMinutes(10);
        await store.EnqueueAsync(Proposal("fresh", later), policy, later);
        var remaining = Assert.Single(await store.ListAsync());
        Assert.Equal("fresh", Assert.IsType<CreateWorkItemArgs>(remaining.Arguments).Item.Title);
    }

    [Fact]
    public async Task Enqueue_ReapsOnlyDecidedRowsPastRetention()
    {
        var store = new InMemoryMajordomoProposalStore();
        var policy = new MajordomoOptions
        {
            ProposalTimeToLive = TimeSpan.FromDays(30),
            DecidedProposalRetention = TimeSpan.FromDays(7),
        };
        var t0 = DateTimeOffset.UtcNow;

        var decided = Proposal("old rejection", t0);
        var pending = Proposal("still pending", t0);
        var stuck = Proposal("stuck commit", t0);
        await store.EnqueueAsync(decided, policy, t0);
        await store.EnqueueAsync(pending, policy, t0);
        await store.EnqueueAsync(stuck, policy, t0);
        Assert.True(await store.TryTransitionAsync(
            decided.Id, MajordomoProposalState.Pending,
            decided with
            {
                State = MajordomoProposalState.Rejected,
                DecidedAt = t0.AddDays(1),
                DecidedBy = "test-operator",
                DecisionReason = "not needed",
            }));
        Assert.True(await store.TryTransitionAsync(
            stuck.Id, MajordomoProposalState.Pending,
            stuck with
            {
                State = MajordomoProposalState.Applying,
                DecidedAt = t0,
                DecidedBy = "test-operator",
                DecisionReason = MajordomoProposalService.CommitInFlightReason,
            }));

        // Past the retention window an enqueue reaps the decided row — but a
        // live pending row and a claimed commit are never reaped.
        var later = t0.AddDays(9);
        await store.EnqueueAsync(Proposal("trigger", later), policy, later);

        var rows = await store.ListAsync();
        Assert.Equal(3, rows.Count);
        Assert.DoesNotContain(rows, r => r.Id == decided.Id);
        Assert.Contains(rows, r => r.Id == pending.Id);
        Assert.Contains(rows, r => r.Id == stuck.Id);
    }

    [Fact]
    public async Task SqliteStore_Enqueue_EnforcesCap_AndSweepsExpiredPending()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mjd-prop-{Guid.NewGuid():N}.db");
        var policy = new MajordomoOptions
        {
            MaxPendingProposals = 1,
            ProposalTimeToLive = TimeSpan.FromMinutes(5),
        };
        var t0 = DateTimeOffset.UtcNow;
        try
        {
            using (var store = new SqliteMajordomoProposalStore(path))
            {
                await store.EnqueueAsync(Proposal("stale", t0), policy, t0);
                await Assert.ThrowsAsync<MajordomoProposalQueueFullException>(
                    () => store.EnqueueAsync(Proposal("blocked", t0), policy, t0));

                var later = t0.AddMinutes(10);
                await store.EnqueueAsync(Proposal("fresh", later), policy, later);
            }

            using (var reopened = new SqliteMajordomoProposalStore(path))
            {
                var rows = await reopened.ListAsync();
                var remaining = Assert.Single(rows);
                Assert.Equal("fresh", Assert.IsType<CreateWorkItemArgs>(remaining.Arguments).Item.Title);
            }
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
            TestScratchDirectory.DeleteSqliteCompanions(path);
        }
    }

    [Fact]
    public async Task FullProposalQueue_RefusesProposals_UntilDecisionsDrainIt()
    {
        using var factory = new WorkItemApiFactory();
        var store = new InMemoryMajordomoProposalStore();
        var time = new ControllableTime(DateTimeOffset.UtcNow);
        var service = new MajordomoProposalService(
            store,
            factory.Services.GetRequiredService<MajordomoMutateBackend>(),
            MonitorOf(new MajordomoServerOptions { MaxPendingProposals = 1 }),
            time);

        var first = await service.ProposeAsync(
            MajordomoTools.CreateWorkItem,
            new CreateWorkItemArgs(ItemSpec("queued")),
            ReviewedPlan(ItemSpec("queued")),
            "majordomo",
            CancellationToken.None);
        Assert.Null(first.Refusal);
        Assert.NotNull(first.Record);

        var overflow = await service.ProposeAsync(
            MajordomoTools.CreateWorkItem,
            new CreateWorkItemArgs(ItemSpec("overflow")),
            ReviewedPlan(ItemSpec("overflow")),
            "majordomo",
            CancellationToken.None);
        Assert.NotNull(overflow.Refusal);
        Assert.Equal("proposal_queue_full", overflow.Refusal!.Reason);
        Assert.Null(overflow.Record);

        // Deciding drains the backlog, and the refusal was never a write.
        var rejection = await service.RejectAsync(first.Record!.Id, "test-operator", "no");
        Assert.Null(rejection.Refusal);
        var after = await service.ProposeAsync(
            MajordomoTools.CreateWorkItem,
            new CreateWorkItemArgs(ItemSpec("after drain")),
            ReviewedPlan(ItemSpec("after drain")),
            "majordomo",
            CancellationToken.None);
        Assert.Null(after.Refusal);
        Assert.NotNull(after.Record);
        Assert.Empty(await ListAllAsync(factory));
    }

    // ── approval commits exactly the cascade revalidation measured ──────────

    [Fact]
    public async Task Approval_CommitsExactlyTheMeasuredCancelCascade()
    {
        using var factory = new WorkItemApiFactory();
        var target = Seed(WorkItemState.Queued, "cancel target");
        var dependent = Seed(WorkItemState.Queued, "dependent") with { DependsOn = [target.Id] };
        await factory.Store.CreateAsync(target);
        await factory.Store.CreateAsync(dependent);

        var store = new InMemoryMajordomoProposalStore();
        var time = new ControllableTime(DateTimeOffset.UtcNow);
        var service = ManualService(factory, store, time);

        var args = new CancelWorkItemArgs(target.Id, "consolidating work");
        var reviewed = await PlanWithBackendAsync(factory, MajordomoTools.CancelWorkItem, args);
        var record = await ProposeOrThrowAsync(
            service, MajordomoTools.CancelWorkItem, args, reviewed, "majordomo");

        var approval = await service.ApproveAsync(record.Id, "test-operator", TestInitiator);
        Assert.Null(approval.Refusal);
        Assert.Equal(2, approval.ChangeSet!.AffectedItems.Count);
        Assert.Equal(WorkItemState.Cancelled, (await factory.Store.GetAsync(target.Id))!.State);
        Assert.Equal(WorkItemState.Cancelled, (await factory.Store.GetAsync(dependent.Id))!.State);
        Assert.Equal(
            MajordomoProposalState.Approved, (await store.GetAsync(record.Id))!.State);
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
