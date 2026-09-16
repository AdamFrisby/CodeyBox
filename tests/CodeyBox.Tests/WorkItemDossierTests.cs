using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for the per-item delivery dossier
/// (<c>GET /workitems/{id}/dossier</c> + <see cref="WorkItemDossierBuilder"/>):
/// unrun gates are explicit, publication legs are distinct, superseded
/// results are stale, every artifact names its phase and producer, and the
/// link is stable and reachable for completed items.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class WorkItemDossierTests : IDisposable
{
    private readonly DossierApiFactory _factory = new();
    private readonly HttpClient _client;

    public WorkItemDossierTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static WorkItem MakeItem(WorkItemId id, int promptRevision = 1) => new()
    {
        Id = id,
        ProjectId = new ProjectId("p"),
        Title = "t",
        Prompt = "pr",
        Agent = AgentKind.Claude,
        PromptRevision = promptRevision,
    };

    private static AuditReport MakeReport(
        string workItemId,
        string auditorName,
        DateTimeOffset startedAt,
        string worstSeverity = "none") => new()
        {
            Id = Guid.NewGuid().ToString(),
            WorkItemId = workItemId,
            Iteration = 1,
            Target = AuditTarget.Code,
            AuditorName = auditorName,
            AuditorKind = "tool",
            WorstSeverity = worstSeverity,
            StartedAt = startedAt,
            EndedAt = startedAt.AddSeconds(1),
            DurationMs = 1000,
            Findings = [],
        };

    // ── 1. Unrun test gate is explicit, never a silent pass ──────────────

    [Fact]
    public void TestGateNotRun_ShowsExplicitlyAndNotFullyPassing()
    {
        var item = MakeItem(WorkItemId.New());
        var diff = new DossierDiffInput("base", "work", 2, 10, 3, ["a.cs", "b.cs"], false);

        var dossier = WorkItemDossierBuilder.Build(
            item, [], [], [], [], diff, null, null);

        var testGate = Assert.Single(dossier.Gates, g => g.Name == "test");
        Assert.Equal(DossierOutcomes.NotRun, testGate.Outcome);
        Assert.NotEqual("fully_passing", dossier.OverallStatus);
    }

    [Fact]
    public async Task DossierEndpoint_TestGateNotRun_IsExplicit()
    {
        var id = WorkItemId.New();
        await _factory.WorkItemStore.CreateAsync(MakeItem(id));

        var resp = await _client.GetAsync($"/workitems/{id}/dossier");
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var testGate = doc.GetProperty("gates").EnumerateArray()
            .Single(g => g.GetProperty("name").GetString() == "test");
        Assert.Equal("not_run", testGate.GetProperty("outcome").GetString());
        Assert.NotEqual("fully_passing", doc.GetProperty("overallStatus").GetString());
    }

    // ── 2. Publication legs are distinct facts ───────────────────────────

    [Fact]
    public void PublicationLegs_AreDistinctStates()
    {
        var id = WorkItemId.New();
        var item = MakeItem(id) with
        {
            MergeSha = "abc123",
            MergedPrNumber = 42,
            MergedPrUrl = "https://example.com/pr/42",
        };

        var openDossier = WorkItemDossierBuilder.Build(
            item, [], [], [], [], null, "open", null);
        Assert.Equal("merged", openDossier.Publication.LocalMerge.State);
        Assert.Equal("open", openDossier.Publication.OpenPr.State);
        Assert.Equal("none", openDossier.Publication.MergedPr.State);

        var mergedDossier = WorkItemDossierBuilder.Build(
            item, [], [], [], [], null, "merged", "deadbee");
        Assert.Equal("merged", mergedDossier.Publication.LocalMerge.State);
        Assert.Equal("none", mergedDossier.Publication.OpenPr.State);
        Assert.Equal("merged", mergedDossier.Publication.MergedPr.State);

        var localOnly = WorkItemDossierBuilder.Build(
            item, [], [], [], [], null, null, null);
        Assert.Equal("merged", localOnly.Publication.LocalMerge.State);
        Assert.Equal("none", localOnly.Publication.OpenPr.State);
        Assert.Equal("none", localOnly.Publication.MergedPr.State);
    }

    // ── 3. Superseded results are stale ──────────────────────────────────

    [Fact]
    public void SupersededRevision_MarksArtifactStale()
    {
        var id = WorkItemId.New();
        var t0 = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var t1 = t0.AddHours(1);
        var item = MakeItem(id, promptRevision: 2);
        var iterations = new List<WorkItemIteration>
        {
            new(id, 1, 1, t0),
            new(id, 2, 2, t1),
        };
        var oldReport = MakeReport(id.ToString(), "csharp:test-pass", t0.AddMinutes(30));
        var freshReport = MakeReport(id.ToString(), "csharp:test-pass", t1.AddMinutes(30));

        var dossier = WorkItemDossierBuilder.Build(
            item, [oldReport, freshReport], [], [], iterations, null, null, null);

        var auditArtifacts = dossier.Artifacts
            .Where(a => a.Name.StartsWith("audit:", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, auditArtifacts.Count);
        Assert.True(auditArtifacts[0].IsStale);
        Assert.False(auditArtifacts[1].IsStale);
    }

    [Fact]
    public async Task DossierEndpoint_SupersededAuditReport_IsStale()
    {
        var id = WorkItemId.New();
        var t0 = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        await _factory.WorkItemStore.CreateAsync(MakeItem(id));
        await _factory.WorkItemStore.RecordIterationDispatchAsync(id, 1, 1, t0);
        var reportTime = t0.AddMinutes(30);
        await _factory.AuditReportStore.CreateAsync(MakeReport(id.ToString(), "csharp:test-pass", reportTime));

        var t1 = t0.AddHours(1);
        var replaced = await _factory.WorkItemStore.TryReplacePromptAsync(id, "new prompt", t1);
        Assert.Equal(PromptReplaceOutcome.Updated, replaced.Outcome);
        await _factory.WorkItemStore.RecordIterationDispatchAsync(id, 2, replaced.NewRevision!.Value, t1.AddMinutes(1));

        var resp = await _client.GetAsync($"/workitems/{id}/dossier");
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var auditArtifact = doc.GetProperty("artifacts").EnumerateArray()
            .Single(a => a.GetProperty("name").GetString()!.StartsWith("audit:", StringComparison.Ordinal));
        Assert.True(auditArtifact.GetProperty("isStale").GetBoolean());
    }

    // ── 4. Every artifact names its phase and producer ───────────────────

    [Fact]
    public void EveryArtifact_NamesPhaseAndProducer()
    {
        var id = WorkItemId.New();
        var now = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var item = MakeItem(id) with { MergeSha = "abc", MergedPrNumber = 7, MergedPrUrl = "https://example.com/7" };
        var reports = new List<AuditReport>
        {
            MakeReport(id.ToString(), "csharp:build", now),
            MakeReport(id.ToString(), "csharp:test-pass", now),
        };
        var costs = new List<WorkItemCost>
        {
            new()
            {
                Id = "c1", WorkItemId = id.ToString(), Phase = "work",
                AgentKind = "claude", ModelId = "m",
                InputTokens = 10, OutputTokens = 5, EstimatedUsd = 0.01,
                StartedAt = now, EndedAt = now.AddSeconds(2),
            },
        };
        var timings = new List<TimingRecord>
        {
            new() { WorkItemId = id, Phase = "work", Step = "agent-turn", StartedAt = now, EndedAt = now.AddSeconds(2), DurationMs = 2000 },
        };
        var diff = new DossierDiffInput("b", "w", 1, 4, 0, ["a.cs"], false);

        var dossier = WorkItemDossierBuilder.Build(
            item, reports, costs, timings, [], diff, "open", null);

        Assert.NotEmpty(dossier.Artifacts);
        foreach (var artifact in dossier.Artifacts)
        {
            Assert.False(string.IsNullOrWhiteSpace(artifact.Phase), $"artifact {artifact.Name} missing phase");
            Assert.False(string.IsNullOrWhiteSpace(artifact.ProducedBy), $"artifact {artifact.Name} missing producer");
        }
    }

    // ── 5. Stable link, reachable for completed items ────────────────────

    [Fact]
    public void DossierLink_IsStableAcrossBuilds()
    {
        var id = WorkItemId.New();
        var item = MakeItem(id);
        var first = WorkItemDossierBuilder.Build(item, [], [], [], [], null, null, null);
        var second = WorkItemDossierBuilder.Build(item, [], [], [], [], null, null, null);
        Assert.Equal(first.DossierLink, second.DossierLink);
        Assert.Equal($"/workitems/{id}/dossier", first.DossierLink);
    }

    [Fact]
    public async Task DossierEndpoint_StableLinkAndReachableWhenDone()
    {
        var id = WorkItemId.New();
        var item = MakeItem(id) with { State = WorkItemState.Done, MergeSha = "abc123" };
        await _factory.WorkItemStore.CreateAsync(item);

        var first = await _client.GetAsync($"/workitems/{id}/dossier");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstDoc = await first.Content.ReadFromJsonAsync<JsonElement>();

        var second = await _client.GetAsync($"/workitems/{id}/dossier");
        second.EnsureSuccessStatusCode();
        var secondDoc = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            firstDoc.GetProperty("dossierLink").GetString(),
            secondDoc.GetProperty("dossierLink").GetString());
        Assert.Equal(
            $"/workitems/{id}/dossier",
            firstDoc.GetProperty("dossierLink").GetString());
    }

    [Fact]
    public async Task DossierEndpoint_UnknownId_Returns404()
    {
        var resp = await _client.GetAsync($"/workitems/{Guid.NewGuid()}/dossier");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task DossierEndpoint_InvalidGuid_Returns400()
    {
        var resp = await _client.GetAsync("/workitems/not-a-guid/dossier");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}

internal sealed class DossierApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-dossier-httptest-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore WorkItemStore { get; }
    public SqliteAuditReportStore AuditReportStore { get; }
    public SqliteWorkItemCostStore CostStore { get; }
    public SqliteTimingStore TimingStore { get; }

    public DossierApiFactory()
    {
        WorkItemStore = new SqliteWorkItemStore(_dbPath);
        AuditReportStore = new SqliteAuditReportStore(_dbPath);
        CostStore = new SqliteWorkItemCostStore(_dbPath);
        TimingStore = new SqliteTimingStore(_dbPath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var tmp = Path.GetTempPath();
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(WorkItemStore);
            services.RemoveAll<IAuditReportStore>();
            services.AddSingleton<IAuditReportStore>(AuditReportStore);
            services.RemoveAll<IWorkItemCostStore>();
            services.AddSingleton<IWorkItemCostStore>(CostStore);
            services.RemoveAll<ITimingStore>();
            services.AddSingleton<ITimingStore>(TimingStore);
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            TimingStore.Dispose();
            CostStore.Dispose();
            WorkItemStore.Dispose();
            AuditReportStore.Dispose();
            try { File.Delete(_dbPath); } catch { /* best-effort */ }
        }
        base.Dispose(disposing);
    }
}
