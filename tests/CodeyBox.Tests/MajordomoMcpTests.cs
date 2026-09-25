using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Majordomo;
using CodeyBox.Orchestrator;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Acceptance coverage for the majordomo MCP server: closed vocabulary
/// publication, authorization routing (Proposed/Autonomous/refusal),
/// plan-only semantics for proposals and dry-runs, item+field refusal
/// detail, per-turn budget, distinct identity, and the pre-execution audit
/// record — all asserted against the real store and the real audit file,
/// not mocks.
/// </summary>
public sealed class MajordomoMcpTests
{
    private const string McpPath = "/mcp/majordomo";

    // ── helpers ────────────────────────────────────────────────────────────

    private static async Task<McpClient> ConnectAsync(
        WebApplicationFactory<Program> factory, HttpClient http)
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
        // Error results carry the same envelope as text content.
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        Assert.NotNull(text);
        return JsonSerializer.Deserialize<JsonElement>(text!);
    }

    private static string OutcomeOf(CallToolResult result)
        => Structured(result).GetProperty("outcome").GetString()!;

    private static void AssertOutcome(CallToolResult result, string expected)
        => Assert.True(OutcomeOf(result) == expected,
            $"expected {expected}, got: {Structured(result).GetRawText()}");

    private static JsonElement RefusalOf(CallToolResult result)
        => Structured(result).GetProperty("refusal");

    private static async Task<int> StoreCountAsync(WorkItemApiFactory factory)
    {
        var n = 0;
        await foreach (var _ in factory.Store.ListAsync())
            n++;
        return n;
    }

    private static WorkItem Seed(WorkItemState state, string title, ProjectId? projectId = null,
        IReadOnlyList<WorkItemId>? dependsOn = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = projectId ?? new ProjectId("test-project"),
        Title = title,
        Prompt = "prompt for " + title,
        State = state,
        DependsOn = dependsOn ?? [],
    };

    private static Dictionary<string, object?> Spec(string title, string projectId = "test-project") =>
        new()
        {
            ["project_id"] = projectId,
            ["title"] = title,
            ["prompt"] = "do " + title,
        };

    private static WebApplicationFactory<Program> AutonomousFactory(
        WorkItemApiFactory inner, int? maxMutatedItemsPerTurn = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["CodeyBox:Majordomo:Mode"] = "autonomous",
        };
        if (maxMutatedItemsPerTurn is { } cap)
            values["CodeyBox:Majordomo:MaxMutatedItemsPerTurn"] = cap.ToString();
        return inner.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(values)));
    }

    // ── vocabulary publication ─────────────────────────────────────────────

    [Fact]
    public async Task PublishesExactlyTheVocabulary()
    {
        using var factory = new WorkItemApiFactory();
        using var http = factory.CreateClient();
        await using var mcp = await ConnectAsync(factory, http);

        var tools = await mcp.ListToolsAsync();
        var names = tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var expected = MajordomoTools.All.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, names);

        // Every advertised input schema is a JSON object schema.
        foreach (var tool in tools)
            Assert.Equal(JsonValueKind.Object, tool.JsonSchema.ValueKind);
    }

    [Fact]
    public async Task UnknownToolIsNotInvocable()
    {
        using var factory = new WorkItemApiFactory();
        using var http = factory.CreateClient();
        await using var mcp = await ConnectAsync(factory, http);

        // A name outside the vocabulary never reaches a handler: the server
        // refuses it at the protocol level.
        await Assert.ThrowsAnyAsync<McpException>(() =>
            mcp.CallToolAsync("pause_queue", new Dictionary<string, object?>()).AsTask());
        await Assert.ThrowsAnyAsync<McpException>(() =>
            mcp.CallToolAsync("run_shell", new Dictionary<string, object?>()).AsTask());
    }

    // ── reads ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadToolsReturnOrchestratorShapes()
    {
        using var factory = new WorkItemApiFactory();
        var item = Seed(WorkItemState.Queued, "queued item");
        var done = Seed(WorkItemState.Done, "done item");
        await factory.Store.CreateAsync(item);
        await factory.Store.CreateAsync(done);

        using var http = factory.CreateClient();
        await using var mcp = await ConnectAsync(factory, http);
        var noArgs = new Dictionary<string, object?>();

        // get_queue_status — state + per-state counts match the store.
        var queue = await mcp.CallToolAsync("get_queue_status", noArgs);
        AssertOutcome(queue, "executed");
        var queueResult = Structured(queue).GetProperty("result");
        Assert.Equal("running", queueResult.GetProperty("state").GetString());
        Assert.Equal(
            await factory.Store.CountByStateAsync(WorkItemState.Queued),
            queueResult.GetProperty("item_counts_by_state").GetProperty("queued").GetInt32());
        Assert.Equal(
            await factory.Store.CountByStateAsync(WorkItemState.Done),
            queueResult.GetProperty("item_counts_by_state").GetProperty("done").GetInt32());

        // get_dispatch_status — the pool shape the operator endpoint reports.
        var dispatch = await mcp.CallToolAsync("get_dispatch_status", noArgs);
        AssertOutcome(dispatch, "executed");
        var dispatchResult = Structured(dispatch).GetProperty("result");
        Assert.True(dispatchResult.GetProperty("max_concurrent").GetInt32() >= 0);
        Assert.Equal(0, dispatchResult.GetProperty("currently_running").GetInt32());
        Assert.True(dispatchResult.GetProperty("occupied_slots").GetArrayLength() == 0);

        // get_agent_capacity — one entry per registered agent kind.
        var capacity = await mcp.CallToolAsync("get_agent_capacity", noArgs);
        AssertOutcome(capacity, "executed");
        var entries = Structured(capacity).GetProperty("result").GetProperty("entries");
        var registered = factory.Services.GetRequiredService<IAgentRegistry>().Available;
        Assert.Equal(
            registered.Select(a => a.Value).OrderBy(v => v, StringComparer.Ordinal),
            entries.EnumerateArray().Select(e => e.GetProperty("agent").GetString()!).OrderBy(v => v, StringComparer.Ordinal));

        // list_work_items — filtered to the seeded project, same ordering as the queue read.
        var list = await mcp.CallToolAsync("list_work_items", new Dictionary<string, object?>
        {
            ["project_id"] = "test-project",
            ["states"] = new[] { "queued" },
        });
        AssertOutcome(list, "executed");
        var listResult = Structured(list).GetProperty("result");
        Assert.Equal(1, listResult.GetProperty("total_matched").GetInt32());
        var row = listResult.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(item.Id.ToString(), row.GetProperty("id").GetString());
        Assert.Equal("queued", row.GetProperty("state").GetString());

        // get_work_item — full detail for the seeded row; unknown id → null item.
        var detail = await mcp.CallToolAsync("get_work_item", new Dictionary<string, object?>
        {
            ["id"] = item.Id.ToString(),
        });
        AssertOutcome(detail, "executed");
        var detailItem = Structured(detail).GetProperty("result").GetProperty("item");
        Assert.Equal(item.Id.ToString(), detailItem.GetProperty("id").GetString());
        Assert.Equal(item.Prompt, detailItem.GetProperty("prompt").GetString());
        var missing = await mcp.CallToolAsync("get_work_item", new Dictionary<string, object?>
        {
            ["id"] = WorkItemId.New().ToString(),
        });
        AssertOutcome(missing, "executed");
        // The writer elides nulls, so a missing item is either absent or null.
        var missingItem = Structured(missing).GetProperty("result");
        Assert.True(
            !missingItem.TryGetProperty("item", out var el) || el.ValueKind == JsonValueKind.Null,
            missingItem.GetRawText());

        // get_work_item_audit — reports the seeded audit rows for the item.
        var reports = factory.Services.GetRequiredService<IAuditReportStore>();
        var now = DateTimeOffset.UtcNow;
        await reports.CreateAsync(new AuditReport
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = item.Id.ToString(),
            Iteration = 0,
            AuditorName = "test-auditor",
            AuditorKind = "script",
            WorstSeverity = "warning",
            StartedAt = now,
            EndedAt = now,
            DurationMs = 12,
            Findings =
            [
                new AuditReportFinding(
                    "f1", "warning", "test finding", "detail", [], []),
            ],
        });
        var audit = await mcp.CallToolAsync("get_work_item_audit", new Dictionary<string, object?>
        {
            ["id"] = item.Id.ToString(),
        });
        AssertOutcome(audit, "executed");
        var auditResult = Structured(audit).GetProperty("result");
        var report = auditResult.GetProperty("reports").EnumerateArray().Single();
        Assert.Equal("test-auditor", report.GetProperty("auditor_name").GetString());
        Assert.Equal("warning", report.GetProperty("worst_severity").GetString());
        Assert.Equal("test finding", report.GetProperty("findings").EnumerateArray().Single().GetProperty("title").GetString());
    }

    // ── proposals ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ProposedMode_MutationsProduceProposalsAndMutateNothing()
    {
        using var factory = new WorkItemApiFactory();
        var queued = Seed(WorkItemState.Queued, "proposed target");
        var terminal = Seed(WorkItemState.Failed, "retryable target");
        await factory.Store.CreateAsync(queued);
        await factory.Store.CreateAsync(terminal);

        // Default mode is Proposed — no config override.
        using var http = factory.CreateClient();
        await using var mcp = await ConnectAsync(factory, http);

        var proposals = new[]
        {
            await mcp.CallToolAsync("create_work_item", new Dictionary<string, object?>
            {
                ["item"] = Spec("proposed create"),
            }),
            await mcp.CallToolAsync("create_work_item_chain", new Dictionary<string, object?>
            {
                ["items"] = new object[]
                {
                    new Dictionary<string, object?> { ["item"] = Spec("chain a"), ["depends_on_indexes"] = Array.Empty<int>() },
                    new Dictionary<string, object?> { ["item"] = Spec("chain b"), ["depends_on_indexes"] = new[] { 0 } },
                },
            }),
            await mcp.CallToolAsync("update_work_item", new Dictionary<string, object?>
            {
                ["id"] = queued.Id.ToString(),
                ["patch"] = new Dictionary<string, object?> { ["title"] = "new title" },
            }),
            await mcp.CallToolAsync("cancel_work_item", new Dictionary<string, object?>
            {
                ["id"] = queued.Id.ToString(),
                ["reason"] = "operator asked",
            }),
            await mcp.CallToolAsync("retry_work_item", new Dictionary<string, object?>
            {
                ["id"] = terminal.Id.ToString(),
                ["from"] = "work",
            }),
        };

        foreach (var proposal in proposals)
        {
            AssertOutcome(proposal, "proposed");
            var view = Structured(proposal).GetProperty("proposal");
            Assert.True(view.GetProperty("affected_items").GetInt32() >= 1);
            // The proposal carries the validated change set — the same payload
            // a dry-run would return — so the operator reviews real structure.
            Assert.True(view.GetProperty("change_set").GetProperty("changes").GetArrayLength() >= 1);
        }

        // Nothing reached the store: still exactly the two seeded rows,
        // untouched.
        Assert.Equal(2, await StoreCountAsync(factory));
        var storedQueued = await factory.Store.GetAsync(queued.Id);
        Assert.Equal("proposed target", storedQueued!.Title);
        Assert.Equal(WorkItemState.Queued, storedQueued.State);
        var storedTerminal = await factory.Store.GetAsync(terminal.Id);
        Assert.Equal(WorkItemState.Failed, storedTerminal!.State);
    }

    // ── refusal classes ────────────────────────────────────────────────────

    [Fact]
    public async Task ChainWithForwardEdge_IsRefused_NothingCreated()
    {
        using var factory = new WorkItemApiFactory();
        using var autonomous = AutonomousFactory(factory);
        using var http = autonomous.CreateClient();
        await using var mcp = await ConnectAsync(autonomous, http);

        // depends_on_indexes may only point backward; a forward edge is the
        // chain's nearest representable cycle and is refused at the contract.
        var result = await mcp.CallToolAsync("create_work_item_chain", new Dictionary<string, object?>
        {
            ["items"] = new object[]
            {
                new Dictionary<string, object?> { ["item"] = Spec("a"), ["depends_on_indexes"] = new[] { 1 } },
                new Dictionary<string, object?> { ["item"] = Spec("b"), ["depends_on_indexes"] = Array.Empty<int>() },
            },
        });

        var refusal = RefusalOf(result);
        AssertOutcome(result, "refused");
        // The refusal names the offending field so the model can correct and
        // retry from the message alone — a forward edge is rejected by the
        // chain-shape contract before the store is ever consulted.
        Assert.Equal("argument_contract_mismatch", refusal.GetProperty("reason").GetString());
        Assert.Equal("items", refusal.GetProperty("field").GetString());
        Assert.Equal(0, await StoreCountAsync(factory));
    }

    [Fact]
    public async Task ChainWithRefactorNode_IsRefused_NamingItemAndField_NothingCreated()
    {
        // The chain-shape contract accepts this input, so the refusal comes
        // from the whole-set composition review — the path that must name the
        // offending item position and field for the model to correct.
        using var factory = new WorkItemApiFactory();
        using var autonomous = AutonomousFactory(factory);
        using var http = autonomous.CreateClient();
        await using var mcp = await ConnectAsync(autonomous, http);

        var result = await mcp.CallToolAsync("create_work_item_chain", new Dictionary<string, object?>
        {
            ["items"] = new object[]
            {
                new Dictionary<string, object?> { ["item"] = Spec("a"), ["depends_on_indexes"] = Array.Empty<int>() },
                new Dictionary<string, object?>
                {
                    ["item"] = Merge(Spec("b"), ("is_refactor", true)),
                    ["depends_on_indexes"] = new[] { 0 },
                },
            },
        });

        var refusal = RefusalOf(result);
        AssertOutcome(result, "refused");
        Assert.Equal("invalid_chain", refusal.GetProperty("reason").GetString());
        Assert.Equal("items[1]", refusal.GetProperty("item").GetString());
        Assert.Equal("is_refactor", refusal.GetProperty("field").GetString());
        Assert.Equal(0, await StoreCountAsync(factory));
    }

    [Fact]
    public async Task ChainWithNonexistentDependency_IsRefused_NothingCreated()
    {
        using var factory = new WorkItemApiFactory();
        using var autonomous = AutonomousFactory(factory);
        using var http = autonomous.CreateClient();
        await using var mcp = await ConnectAsync(autonomous, http);

        var missingId = WorkItemId.New();
        var result = await mcp.CallToolAsync("create_work_item_chain", new Dictionary<string, object?>
        {
            ["items"] = new object[]
            {
                new Dictionary<string, object?> { ["item"] = Spec("root"), ["depends_on_indexes"] = Array.Empty<int>() },
                new Dictionary<string, object?>
                {
                    ["item"] = Merge(Spec("dependent"), ("depends_on", new[] { missingId.ToString() })),
                    ["depends_on_indexes"] = new[] { 0 },
                },
            },
        });

        var refusal = RefusalOf(result);
        AssertOutcome(result, "refused");
        Assert.Equal("dependency_not_found", refusal.GetProperty("reason").GetString());
        Assert.Equal("items[1]", refusal.GetProperty("item").GetString());
        Assert.Equal("depends_on", refusal.GetProperty("field").GetString());
        Assert.Contains(missingId.ToString(), refusal.GetProperty("detail").GetString());
        Assert.Equal(0, await StoreCountAsync(factory));
    }

    [Fact]
    public async Task ChainWithTerminalDependency_IsRefused_NothingCreated()
    {
        using var factory = new WorkItemApiFactory();
        var terminal = Seed(WorkItemState.Cancelled, "already cancelled");
        await factory.Store.CreateAsync(terminal);
        using var autonomous = AutonomousFactory(factory);
        using var http = autonomous.CreateClient();
        await using var mcp = await ConnectAsync(autonomous, http);

        var result = await mcp.CallToolAsync("create_work_item_chain", new Dictionary<string, object?>
        {
            ["items"] = new object[]
            {
                new Dictionary<string, object?> { ["item"] = Spec("root"), ["depends_on_indexes"] = Array.Empty<int>() },
                new Dictionary<string, object?>
                {
                    ["item"] = Merge(Spec("dependent"), ("depends_on", new[] { terminal.Id.ToString() })),
                    ["depends_on_indexes"] = new[] { 0 },
                },
            },
        });

        var refusal = RefusalOf(result);
        AssertOutcome(result, "refused");
        Assert.Equal("dependency_terminal", refusal.GetProperty("reason").GetString());
        Assert.Equal("items[1]", refusal.GetProperty("item").GetString());
        Assert.Equal("depends_on", refusal.GetProperty("field").GetString());
        Assert.Contains(terminal.Id.ToString(), refusal.GetProperty("detail").GetString());
        Assert.Equal(1, await StoreCountAsync(factory)); // only the seeded row
    }

    [Fact]
    public async Task ChainWithUnknownKnob_IsRefused_NothingCreated()
    {
        using var factory = new WorkItemApiFactory();
        using var autonomous = AutonomousFactory(factory);
        using var http = autonomous.CreateClient();
        await using var mcp = await ConnectAsync(autonomous, http);

        var result = await mcp.CallToolAsync("create_work_item_chain", new Dictionary<string, object?>
        {
            ["items"] = new object[]
            {
                new Dictionary<string, object?> { ["item"] = Spec("root"), ["depends_on_indexes"] = Array.Empty<int>() },
                new Dictionary<string, object?>
                {
                    ["item"] = Merge(Spec("knobby"), ("knobs", new Dictionary<string, object?> { ["notARealKnob"] = "1" })),
                    ["depends_on_indexes"] = new[] { 0 },
                },
            },
        });

        var refusal = RefusalOf(result);
        AssertOutcome(result, "refused");
        Assert.Equal("items[1]", refusal.GetProperty("item").GetString());
        Assert.Contains("notARealKnob", refusal.GetProperty("detail").GetString());
        Assert.Equal(0, await StoreCountAsync(factory));
    }

    [Fact]
    public async Task ChainWithOutOfVocabularyKnobValue_IsRefused_NothingCreated()
    {
        using var factory = new WorkItemApiFactory();
        factory.AdditionalKnobs.Add(new EnumTestKnob("mergeStyle", ["rebase", "squash"]));
        using var autonomous = AutonomousFactory(factory);
        using var http = autonomous.CreateClient();
        await using var mcp = await ConnectAsync(autonomous, http);

        var result = await mcp.CallToolAsync("create_work_item_chain", new Dictionary<string, object?>
        {
            ["items"] = new object[]
            {
                new Dictionary<string, object?> { ["item"] = Spec("root"), ["depends_on_indexes"] = Array.Empty<int>() },
                new Dictionary<string, object?>
                {
                    ["item"] = Merge(Spec("knobby"), ("knobs", new Dictionary<string, object?> { ["mergeStyle"] = "explode" })),
                    ["depends_on_indexes"] = new[] { 0 },
                },
            },
        });

        var refusal = RefusalOf(result);
        AssertOutcome(result, "refused");
        Assert.Equal("items[1]", refusal.GetProperty("item").GetString());
        Assert.Contains("mergeStyle", refusal.GetProperty("detail").GetString());
        Assert.Equal(0, await StoreCountAsync(factory));
    }

    [Fact]
    public async Task UpdateContradictingCurrentState_IsRefused_NothingChanges()
    {
        using var factory = new WorkItemApiFactory();
        var done = Seed(WorkItemState.Done, "finished item");
        await factory.Store.CreateAsync(done);
        using var autonomous = AutonomousFactory(factory);
        using var http = autonomous.CreateClient();
        await using var mcp = await ConnectAsync(autonomous, http);

        // Patching a terminal item contradicts its state — refused, not applied.
        var result = await mcp.CallToolAsync("update_work_item", new Dictionary<string, object?>
        {
            ["id"] = done.Id.ToString(),
            ["patch"] = new Dictionary<string, object?> { ["title"] = "rename a done item" },
        });

        var refusal = RefusalOf(result);
        AssertOutcome(result, "refused");
        Assert.Equal(done.Id.ToString(), refusal.GetProperty("item").GetString());
        var stored = await factory.Store.GetAsync(done.Id);
        Assert.Equal("finished item", stored!.Title);
    }

    [Fact]
    public async Task UpdateCreatingDependencyCycle_IsRefused_NothingChanges()
    {
        // The only representable cycle runs through existing items: adding
        // item Y as a dependency of item X when Y already depends on X.
        using var factory = new WorkItemApiFactory();
        var x = Seed(WorkItemState.Queued, "cycle x");
        var y = Seed(WorkItemState.Queued, "cycle y", dependsOn: [x.Id]);
        await factory.Store.CreateAsync(x);
        await factory.Store.CreateAsync(y);
        using var autonomous = AutonomousFactory(factory);
        using var http = autonomous.CreateClient();
        await using var mcp = await ConnectAsync(autonomous, http);

        var result = await mcp.CallToolAsync("update_work_item", new Dictionary<string, object?>
        {
            ["id"] = x.Id.ToString(),
            ["patch"] = new Dictionary<string, object?>
            {
                ["depends_on"] = new[] { y.Id.ToString() },
            },
        });

        var refusal = RefusalOf(result);
        AssertOutcome(result, "refused");
        Assert.Equal(x.Id.ToString(), refusal.GetProperty("item").GetString());
        Assert.Contains("circular", refusal.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);

        var stored = await factory.Store.GetAsync(x.Id);
        Assert.Empty(stored!.DependsOn);
    }

    [Fact]
    public async Task SingleCallAndCumulativeBudget_RefuseOverCap()
    {
        using var factory = new WorkItemApiFactory();
        using var autonomous = AutonomousFactory(factory, maxMutatedItemsPerTurn: 1);
        using var http = autonomous.CreateClient();
        await using var mcp = await ConnectAsync(autonomous, http);

        // A single call exceeding the per-call cap is refused.
        var overCap = await mcp.CallToolAsync("create_work_item_chain", new Dictionary<string, object?>
        {
            ["items"] = new object[]
            {
                new Dictionary<string, object?> { ["item"] = Spec("a"), ["depends_on_indexes"] = Array.Empty<int>() },
                new Dictionary<string, object?> { ["item"] = Spec("b"), ["depends_on_indexes"] = new[] { 0 } },
            },
        });
        AssertOutcome(overCap, "refused");
        Assert.Equal("too_many_items_in_one_call",
            RefusalOf(overCap).GetProperty("reason").GetString());

        // One real mutation is allowed…
        var first = await mcp.CallToolAsync("create_work_item", new Dictionary<string, object?>
        {
            ["item"] = Spec("first"),
        });
        AssertOutcome(first, "executed");
        Assert.Equal(1, await StoreCountAsync(factory));

        // …then the turn budget is spent and the next mutation is refused.
        var second = await mcp.CallToolAsync("create_work_item", new Dictionary<string, object?>
        {
            ["item"] = Spec("second"),
        });
        AssertOutcome(second, "refused");
        Assert.Equal("turn_mutation_budget_exhausted",
            RefusalOf(second).GetProperty("reason").GetString());
        Assert.Equal(1, await StoreCountAsync(factory));
    }

    // ── cancel cascade blast radius ────────────────────────────────────────

    [Fact]
    public async Task CancelCascade_WithinCap_CancelsQueuedDependentsTransitively()
    {
        using var factory = new WorkItemApiFactory();
        var parent = Seed(WorkItemState.Queued, "parent");
        var child = Seed(WorkItemState.Queued, "child", dependsOn: [parent.Id]);
        var grandchild = Seed(WorkItemState.Queued, "grandchild", dependsOn: [child.Id]);
        // An in-flight dependent is deliberately left to run its course.
        var working = Seed(WorkItemState.Working, "in-flight dependent", dependsOn: [parent.Id]);
        await factory.Store.CreateAsync(parent);
        await factory.Store.CreateAsync(child);
        await factory.Store.CreateAsync(grandchild);
        await factory.Store.CreateAsync(working);

        using var autonomous = AutonomousFactory(factory);
        using var http = autonomous.CreateClient();
        await using var mcp = await ConnectAsync(autonomous, http);

        // The dry-run reviews the full cascade — the dependents are named in
        // the change set, not discovered only in the commit.
        var dry = await mcp.CallToolAsync("cancel_work_item", new Dictionary<string, object?>
        {
            ["id"] = parent.Id.ToString(),
            ["reason"] = "chain superseded",
            ["dry_run"] = true,
        });
        AssertOutcome(dry, "dry_run");
        var dryChanges = Structured(dry).GetProperty("result").GetProperty("changes");
        var kinds = dryChanges.EnumerateArray().Select(c => c.GetProperty("kind").GetString()).ToList();
        Assert.Equal(["cancel_item", "cancel_dependents"], kinds);
        var cascadeIds = dryChanges[1].GetProperty("ids").EnumerateArray()
            .Select(e => e.GetString()).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { child.Id.ToString(), grandchild.Id.ToString() }.OrderBy(s => s, StringComparer.Ordinal),
            cascadeIds);

        // Nothing was touched by the dry-run.
        Assert.Equal(4, await StoreCountAsync(factory));
        Assert.Equal(WorkItemState.Queued, (await factory.Store.GetAsync(child.Id))!.State);

        var real = await mcp.CallToolAsync("cancel_work_item", new Dictionary<string, object?>
        {
            ["id"] = parent.Id.ToString(),
            ["reason"] = "chain superseded",
        });
        AssertOutcome(real, "executed");
        // The reviewed set is the executed set.
        Assert.Equal(
            dryChanges.GetRawText(),
            Structured(real).GetProperty("result").GetProperty("changes").GetRawText());
        var affected = Structured(real).GetProperty("result").GetProperty("affected_items");
        Assert.Equal(3, affected.GetArrayLength());

        Assert.Equal(WorkItemState.Cancelled, (await factory.Store.GetAsync(parent.Id))!.State);
        Assert.Equal(WorkItemState.Cancelled, (await factory.Store.GetAsync(child.Id))!.State);
        Assert.Equal(WorkItemState.Cancelled, (await factory.Store.GetAsync(grandchild.Id))!.State);
        Assert.Equal(WorkItemState.Working, (await factory.Store.GetAsync(working.Id))!.State);
    }

    [Fact]
    public async Task CancelCascade_ExceedingPerCallCap_IsRefused_NothingCancelled()
    {
        using var factory = new WorkItemApiFactory();
        var parent = Seed(WorkItemState.Queued, "parent");
        var child = Seed(WorkItemState.Queued, "child", dependsOn: [parent.Id]);
        var grandchild = Seed(WorkItemState.Queued, "grandchild", dependsOn: [child.Id]);
        await factory.Store.CreateAsync(parent);
        await factory.Store.CreateAsync(child);
        await factory.Store.CreateAsync(grandchild);

        // The call's real blast radius is 3 (target + 2 cascade targets) — a
        // cap of 2 must refuse it even though the contract declares 1 item.
        using var autonomous = AutonomousFactory(factory, maxMutatedItemsPerTurn: 2);
        using var http = autonomous.CreateClient();
        await using var mcp = await ConnectAsync(autonomous, http);

        var result = await mcp.CallToolAsync("cancel_work_item", new Dictionary<string, object?>
        {
            ["id"] = parent.Id.ToString(),
            ["reason"] = "chain superseded",
        });

        AssertOutcome(result, "refused");
        Assert.Equal("too_many_items_in_one_call",
            RefusalOf(result).GetProperty("reason").GetString());
        foreach (var seeded in new[] { parent, child, grandchild })
            Assert.Equal(WorkItemState.Queued, (await factory.Store.GetAsync(seeded.Id))!.State);
    }

    [Fact]
    public async Task CancelCascade_ExceedingRemainingTurnBudget_IsRefused()
    {
        using var factory = new WorkItemApiFactory();
        var parent = Seed(WorkItemState.Queued, "parent");
        var child = Seed(WorkItemState.Queued, "child", dependsOn: [parent.Id]);
        var grandchild = Seed(WorkItemState.Queued, "grandchild", dependsOn: [child.Id]);
        await factory.Store.CreateAsync(parent);
        await factory.Store.CreateAsync(child);
        await factory.Store.CreateAsync(grandchild);

        using var autonomous = AutonomousFactory(factory, maxMutatedItemsPerTurn: 3);
        using var http = autonomous.CreateClient();
        await using var mcp = await ConnectAsync(autonomous, http);

        // Spend one unit of turn budget first.
        var first = await mcp.CallToolAsync("create_work_item", new Dictionary<string, object?>
        {
            ["item"] = Spec("first"),
        });
        AssertOutcome(first, "executed");

        // The cancel projects 3 mutations (parent + child + grandchild) —
        // fits the cap of 3 but not the remaining budget of 2.
        var result = await mcp.CallToolAsync("cancel_work_item", new Dictionary<string, object?>
        {
            ["id"] = parent.Id.ToString(),
            ["reason"] = "chain superseded",
        });

        AssertOutcome(result, "refused");
        Assert.Equal("turn_mutation_budget_exhausted",
            RefusalOf(result).GetProperty("reason").GetString());
        Assert.Equal(WorkItemState.Queued, (await factory.Store.GetAsync(parent.Id))!.State);
        Assert.Equal(WorkItemState.Queued, (await factory.Store.GetAsync(child.Id))!.State);
        Assert.Equal(WorkItemState.Queued, (await factory.Store.GetAsync(grandchild.Id))!.State);
    }

    [Fact]
    public async Task CancelProposal_ShowsTheCascadeInTheChangeSet()
    {
        using var factory = new WorkItemApiFactory();
        var parent = Seed(WorkItemState.Queued, "parent");
        var child = Seed(WorkItemState.Queued, "child", dependsOn: [parent.Id]);
        var grandchild = Seed(WorkItemState.Queued, "grandchild", dependsOn: [child.Id]);
        await factory.Store.CreateAsync(parent);
        await factory.Store.CreateAsync(child);
        await factory.Store.CreateAsync(grandchild);

        // Default mode is Proposed — the operator's review must see the whole
        // blast radius, not just the item the call named.
        using var http = factory.CreateClient();
        await using var mcp = await ConnectAsync(factory, http);

        var result = await mcp.CallToolAsync("cancel_work_item", new Dictionary<string, object?>
        {
            ["id"] = parent.Id.ToString(),
            ["reason"] = "chain superseded",
        });

        AssertOutcome(result, "proposed");
        var view = Structured(result).GetProperty("proposal");
        Assert.Equal(3, view.GetProperty("affected_items").GetInt32());
        var changes = view.GetProperty("change_set").GetProperty("changes");
        var kinds = changes.EnumerateArray().Select(c => c.GetProperty("kind").GetString()).ToList();
        Assert.Equal(["cancel_item", "cancel_dependents"], kinds);
        Assert.Equal(2, changes[1].GetProperty("ids").GetArrayLength());

        // The proposal performs no mutation.
        foreach (var seeded in new[] { parent, child, grandchild })
            Assert.Equal(WorkItemState.Queued, (await factory.Store.GetAsync(seeded.Id))!.State);
    }

    // ── budget concurrency + fault accounting ───────────────────────────────

    [Fact]
    public async Task ConcurrentMutations_CannotOvershootTheTurnCap()
    {
        // Slow the store write so the calls genuinely overlap: without
        // per-identity serialization every call snapshots usage=0 and each
        // passes the remaining-budget check before the first commit lands.
        using var factory = new WorkItemApiFactory();
        factory.WorkItemStoreDecorator = inner => new SlowCreateStore(inner, TimeSpan.FromMilliseconds(150));
        using var autonomous = AutonomousFactory(factory, maxMutatedItemsPerTurn: 1);
        _ = autonomous.CreateClient(); // boot the host so Services resolves

        var executor = autonomous.Services
            .GetRequiredService<CodeyBox.Api.Majordomo.MajordomoExecutor>();
        var accessor = autonomous.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext();
        accessor.HttpContext.Items[ApiKeyAuth.PrincipalItemKey] = new ApiClientPrincipal(
            "majordomo",
            new WorkInitiator { Issuer = "codeybox", Subject = "majordomo-subject", DisplayName = "Test majordomo" },
            CanDelegateInitiator: false);

        var calls = Enumerable.Range(0, 5).Select(i =>
            executor.ExecuteAsync(
                "create_work_item",
                new JsonObject
                {
                    ["item"] = new JsonObject
                    {
                        ["project_id"] = "test-project",
                        ["title"] = $"concurrent {i}",
                        ["prompt"] = "p",
                    },
                },
                CancellationToken.None).AsTask()).ToList();
        var results = await Task.WhenAll(calls);

        Assert.Equal(1, results.Count(r => OutcomeOf(r) == "executed"));
        Assert.Equal(4, results.Count(r =>
            OutcomeOf(r) == "refused"
            && RefusalOf(r).GetProperty("reason").GetString() == "turn_mutation_budget_exhausted"));
        Assert.Equal(1, await StoreCountAsync(factory));
    }

    [Fact]
    public async Task MutationThatThrowsAfterCommit_StillChargesTheBudgetAndAuditsAnOutcome()
    {
        var sink = new TestSink();
        using var logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        using var scope = AuditLog.PushScopedLogger(logger);

        using var factory = new WorkItemApiFactory();
        var item = Seed(WorkItemState.Queued, "cancel target");
        await factory.Store.CreateAsync(item);
        // OrphanReplaysAsync runs after the cancel's row write lands, so the
        // call faults post-commit: the mutation is real but unreported by a
        // normal return.
        factory.WorkItemStoreDecorator = inner => new ThrowOnOrphanStore(inner);
        using var autonomous = AutonomousFactory(factory, maxMutatedItemsPerTurn: 1);
        _ = autonomous.CreateClient(); // boot the host so Services resolves

        var executor = autonomous.Services
            .GetRequiredService<CodeyBox.Api.Majordomo.MajordomoExecutor>();
        var accessor = autonomous.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext();
        accessor.HttpContext.Items[ApiKeyAuth.PrincipalItemKey] = new ApiClientPrincipal(
            "majordomo",
            new WorkInitiator { Issuer = "codeybox", Subject = "majordomo-subject", DisplayName = "Test majordomo" },
            CanDelegateInitiator: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(
                "cancel_work_item",
                JsonNode.Parse($$"""{"id":"{{item.Id}}","reason":"post-commit fault"}"""),
                CancellationToken.None).AsTask());

        // The write landed even though the call threw.
        Assert.Equal(WorkItemState.Cancelled, (await factory.Store.GetAsync(item.Id))!.State);

        // ...and the committed mutation was charged: cap is 1, so the next
        // mutation must be refused rather than getting a free retry.
        var refused = await executor.ExecuteAsync(
            "create_work_item",
            JsonNode.Parse("""{"item":{"project_id":"test-project","title":"charged?","prompt":"p"}}"""),
            CancellationToken.None);
        AssertOutcome(refused, "refused");
        Assert.Equal("turn_mutation_budget_exhausted",
            RefusalOf(refused).GetProperty("reason").GetString());
        Assert.Equal(1, await StoreCountAsync(factory));

        // An outcome record exists for the faulted call too.
        var outcomes = sink.Events.Where(e => EventNameOf(e) == "majordomo.tool_outcome").ToList();
        Assert.Contains(outcomes, e => PropOf(e, "Outcome") == "error");
    }

    // ── dry-run parity ─────────────────────────────────────────────────────

    [Fact]
    public async Task DryRun_ReportsTheSameChangeSetAsTheRealCall_AndMutatesNothing()
    {
        using var factory = new WorkItemApiFactory();
        using var autonomous = AutonomousFactory(factory);
        using var http = autonomous.CreateClient();
        await using var mcp = await ConnectAsync(autonomous, http);

        var args = new Dictionary<string, object?> { ["item"] = Spec("dry-run item") };
        var dry = await mcp.CallToolAsync("create_work_item",
            new Dictionary<string, object?>(args) { ["dry_run"] = true });

        AssertOutcome(dry, "dry_run");
        var dryChangeSet = Structured(dry).GetProperty("result");
        Assert.True(dryChangeSet.GetProperty("dry_run").GetBoolean());
        Assert.Equal(0, dryChangeSet.GetProperty("affected_items").GetArrayLength());
        Assert.Equal(0, await StoreCountAsync(factory));

        var real = await mcp.CallToolAsync("create_work_item", args);
        AssertOutcome(real, "executed");
        var realChangeSet = Structured(real).GetProperty("result");
        Assert.False(realChangeSet.GetProperty("dry_run").GetBoolean());

        // Same change payload; the real call additionally reports the new id.
        Assert.Equal(
            dryChangeSet.GetProperty("changes").GetRawText(),
            realChangeSet.GetProperty("changes").GetRawText());
        Assert.Equal(1, realChangeSet.GetProperty("affected_items").GetArrayLength());
        Assert.Equal(1, await StoreCountAsync(factory));
    }

    // ── update dry-run vs real ─────────────────────────────────────────────

    [Fact]
    public async Task UpdateDryRun_ParityWithRealCall()
    {
        using var factory = new WorkItemApiFactory();
        var queued = Seed(WorkItemState.Queued, "before");
        await factory.Store.CreateAsync(queued);
        using var autonomous = AutonomousFactory(factory);
        using var http = autonomous.CreateClient();
        await using var mcp = await ConnectAsync(autonomous, http);

        var dry = await mcp.CallToolAsync("update_work_item", new Dictionary<string, object?>
        {
            ["id"] = queued.Id.ToString(),
            ["patch"] = new Dictionary<string, object?> { ["title"] = "after" },
            ["dry_run"] = true,
        });
        AssertOutcome(dry, "dry_run");
        Assert.Equal("before", (await factory.Store.GetAsync(queued.Id))!.Title);

        var real = await mcp.CallToolAsync("update_work_item", new Dictionary<string, object?>
        {
            ["id"] = queued.Id.ToString(),
            ["patch"] = new Dictionary<string, object?> { ["title"] = "after" },
        });
        AssertOutcome(real, "executed");
        Assert.Equal("after", (await factory.Store.GetAsync(queued.Id))!.Title);

        var dryChanges = Structured(dry).GetProperty("result").GetProperty("changes");
        var realChanges = Structured(real).GetProperty("result").GetProperty("changes");
        Assert.Equal(dryChanges.GetRawText(), realChanges.GetRawText());
    }

    // ── chain commit ───────────────────────────────────────────────────────

    [Fact]
    public async Task ValidChain_Executes_AtomicallyAndLinksDependencies()
    {
        using var factory = new WorkItemApiFactory();
        using var autonomous = AutonomousFactory(factory);
        using var http = autonomous.CreateClient();
        await using var mcp = await ConnectAsync(autonomous, http);

        var result = await mcp.CallToolAsync("create_work_item_chain", new Dictionary<string, object?>
        {
            ["items"] = new object[]
            {
                new Dictionary<string, object?> { ["item"] = Spec("root"), ["depends_on_indexes"] = Array.Empty<int>() },
                new Dictionary<string, object?> { ["item"] = Spec("mid"), ["depends_on_indexes"] = new[] { 0 } },
                new Dictionary<string, object?> { ["item"] = Spec("leaf"), ["depends_on_indexes"] = new[] { 1 } },
            },
        });

        AssertOutcome(result, "executed");
        var affected = Structured(result).GetProperty("result").GetProperty("affected_items");
        Assert.Equal(3, affected.GetArrayLength());
        Assert.Equal(3, await StoreCountAsync(factory));

        var ids = affected.EnumerateArray().Select(e => WorkItemId.Parse(e.GetString()!)).ToList();
        var mid = await factory.Store.GetAsync(ids[1]);
        var leaf = await factory.Store.GetAsync(ids[2]);
        Assert.Equal([ids[0]], mid!.DependsOn);
        Assert.Equal([ids[1]], leaf!.DependsOn);
    }

    // ── no plan parser ─────────────────────────────────────────────────────

    [Fact]
    public void MajordomoAssemblyDoesNotReferenceThePlanParser()
    {
        // The majordomo surface must never route model output through the
        // admin composer's prose plan parser. The parser lives in
        // CodeyBox.Admin.Model; if the API assembly does not reference that
        // assembly, no majordomo path can call it.
        var apiAssembly = typeof(CodeyBox.Api.Majordomo.MajordomoExecutor).Assembly;
        Assert.DoesNotContain(
            apiAssembly.GetReferencedAssemblies(),
            a => a.Name is "CodeyBox.Admin.Model" or "CodeyBox.Admin.Web");

        // Belt and braces: no member under the Majordomo namespace exposes or
        // consumes the parser type (compared by name — the admin model is an
        // aliased reference here, not a compile-time dependency of the API).
        const string parserTypeName = "CodeyBox.Admin.Model.PlanChainParser";
        foreach (var type in apiAssembly.GetTypes().Where(t =>
                     t.Namespace == "CodeyBox.Api.Majordomo"))
        {
            var signatures = type
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static)
                .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType))
                .Concat(type.GetConstructors(System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .SelectMany(c => c.GetParameters().Select(p => p.ParameterType)))
                .Concat(type.GetFields(System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Static).Select(f => f.FieldType));
            Assert.DoesNotContain(signatures, t => t.FullName == parserTypeName);
        }
    }

    // ── audit records ──────────────────────────────────────────────────────

    [Fact]
    public async Task AuditRecordsExistForRefusedAndExecutedCalls()
    {
        // Direct executor invocation keeps the calls on this async flow, so
        // the scoped audit logger captures the server-side events
        // deterministically — no global Log.Logger, no file flush timing.
        var sink = new TestSink();
        using var logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        using var scope = AuditLog.PushScopedLogger(logger);

        using var factory = new WorkItemApiFactory();
        using var autonomous = AutonomousFactory(factory);
        _ = autonomous.CreateClient(); // boot the host so Services resolves

        var executor = autonomous.Services
            .GetRequiredService<CodeyBox.Api.Majordomo.MajordomoExecutor>();
        var accessor = autonomous.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext();
        // The principal Name (audited identity) and the initiator Subject are
        // deliberately distinct so the assertion proves which one is recorded.
        accessor.HttpContext.Items[ApiKeyAuth.PrincipalItemKey] = new ApiClientPrincipal(
            "majordomo",
            new WorkInitiator { Issuer = "codeybox", Subject = "majordomo-subject", DisplayName = "Test majordomo" },
            CanDelegateInitiator: false);

        var executed = await executor.ExecuteAsync(
            "create_work_item",
            JsonNode.Parse("""{"item":{"project_id":"test-project","title":"audited create","prompt":"p"}}"""),
            CancellationToken.None);
        AssertOutcome(executed, "executed");
        var refused = await executor.ExecuteAsync(
            "cancel_work_item",
            JsonNode.Parse($$"""{"id":"{{WorkItemId.New()}}","reason":"cancel a ghost"}"""),
            CancellationToken.None);
        AssertOutcome(refused, "refused");

        var names = sink.Events.Select(EventNameOf).ToList();
        var toolCalls = names.Select((n, i) => (n, i))
            .Where(x => x.n == "majordomo.tool_call").Select(x => x.i).ToList();
        Assert.Equal(2, toolCalls.Count);

        var createCall = sink.Events[toolCalls[0]];
        var cancelCall = sink.Events[toolCalls[1]];
        Assert.Equal("create_work_item", PropOf(createCall, "Tool"));
        Assert.Equal("execute", PropOf(createCall, "Decision"));
        Assert.Equal("majordomo", PropOf(createCall, "Identity"));
        Assert.Equal("cancel_work_item", PropOf(cancelCall, "Tool"));

        // Both calls produced an outcome record.
        var outcomes = names.Select((n, i) => (n, i))
            .Where(x => x.n == "majordomo.tool_outcome").Select(x => x.i).ToList();
        Assert.Equal(2, outcomes.Count);
        Assert.Equal("executed", PropOf(sink.Events[outcomes[0]], "Outcome"));
        Assert.Equal("refused", PropOf(sink.Events[outcomes[1]], "Outcome"));

        // Ordering: the call record precedes the mutation's own audit event.
        var createdIdx = names.FindIndex(n => n == "work_item.created");
        Assert.True(createdIdx > toolCalls[0] && outcomes[0] > createdIdx,
            string.Join(",", names.Where(n => n is not null)));
    }

    private static string? EventNameOf(Serilog.Events.LogEvent e) =>
        e.Properties.TryGetValue("EventName", out var n) && n is Serilog.Events.ScalarValue sv
            ? sv.Value as string
            : null;

    private static string? PropOf(Serilog.Events.LogEvent e, string name) =>
        e.Properties.TryGetValue(name, out var v) && v is Serilog.Events.ScalarValue sv
            ? sv.Value?.ToString()
            : null;

    // ── identity ───────────────────────────────────────────────────────────

    [Fact]
    public async Task MajordomoEndpointRequiresTheMajordomoClientIdentity()
    {
        // The majordomo token must be a distinct, revocable API-client
        // credential — the operator key is refused on this endpoint.
        var tokenVar = "CODEYBOX_TEST_MAJORDOMO_TOKEN_" + Guid.NewGuid().ToString("N");
        var apiKeyVar = "CODEYBOX_API_KEY";
        var priorApiKey = Environment.GetEnvironmentVariable(apiKeyVar);
        Environment.SetEnvironmentVariable(tokenVar, "test-majordomo-token-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(apiKeyVar, "test-operator-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var factory = new WorkItemApiFactory();
            using var authed = factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["CodeyBox:DangerouslyDisableAuth"] = "false",
                        ["CodeyBox:ApiClients:0:Name"] = "majordomo",
                        ["CodeyBox:ApiClients:0:TokenEnvVar"] = tokenVar,
                        ["CodeyBox:ApiClients:0:Principal:Issuer"] = "codeybox",
                        ["CodeyBox:ApiClients:0:Principal:Subject"] = "majordomo",
                        ["CodeyBox:ApiClients:0:Principal:DisplayName"] = "Test majordomo",
                    }));
            });

            // Operator key → forbidden on the majordomo endpoint.
            using var operatorClient = authed.CreateClient();
            operatorClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", Environment.GetEnvironmentVariable(apiKeyVar));
            var denied = await operatorClient.PostAsync(McpPath,
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

            // No credential → unauthorized.
            using var anonymous = authed.CreateClient();
            var unauthenticated = await anonymous.PostAsync(McpPath,
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

            // The majordomo credential reaches the server and runs tools.
            using var majordomoHttp = authed.CreateClient();
            majordomoHttp.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", Environment.GetEnvironmentVariable(tokenVar));
            await using var mcp = await ConnectAsync(authed, majordomoHttp);
            var result = await mcp.CallToolAsync("get_queue_status", new Dictionary<string, object?>());
            AssertOutcome(result, "executed");
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenVar, null);
            Environment.SetEnvironmentVariable(apiKeyVar, priorApiKey);
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> Merge(
        Dictionary<string, object?> spec, params (string Key, object? Value)[] extra)
    {
        var copy = new Dictionary<string, object?>(spec);
        foreach (var (k, v) in extra)
            copy[k] = v;
        return copy;
    }

    /// <summary>Enum-valued test knob: only the listed values are legal.</summary>
    private sealed class EnumTestKnob(string key, string[] allowed) : IKnob
    {
        public string Key { get; } = key;
        public string Description => $"test enum knob {Key}";
        public IReadOnlyList<string> AllowedValues { get; } = allowed;
        public string DefaultValue => allowed[0];
        public string? GetWorkPromptFragment(string value) => null;
    }

    /// <summary>Delays each create so concurrent calls genuinely overlap mid-commit.</summary>
    private sealed class SlowCreateStore(SqliteWorkItemStore inner, TimeSpan delay)
        : ForwardingWorkItemStore(inner)
    {
        public override async Task CreateAsync(WorkItem item, CancellationToken ct = default)
        {
            await Task.Delay(delay, ct);
            await base.CreateAsync(item, ct);
        }
    }

    /// <summary>
    /// Faults after the cancel row write has landed: OrphanReplaysAsync runs
    /// late in the cancel commit, so the call fails post-commit.
    /// </summary>
    private sealed class ThrowOnOrphanStore(SqliteWorkItemStore inner) : ForwardingWorkItemStore(inner)
    {
        public override Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) =>
            throw new InvalidOperationException("injected post-commit failure");
    }
}
