using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level tests for B1 operator endpoints:
/// <list type="bullet">
///   <item><c>GET /baselines</c> — full sweep report with referencing work items.</item>
///   <item><c>GET /admin/baseline-images</c> — compact dashboard summary.</item>
/// </list>
/// The factory installs a deterministic <see cref="FakeResolver"/> so the
/// reaper's most-recent-sweep state is predictable; the tests assert the
/// exact JSON shape documented in <c>docs/api.md</c> and verify that the
/// per-baseline <c>workItems</c> array is wired through
/// <see cref="IWorkItemStore.ListWorkItemsForBaselineAsync"/>.
/// </summary>
public sealed class BaselineEndpointsTests : IDisposable
{
    private readonly BaselineApiFactory _factory = new();
    private readonly HttpClient _client;

    public BaselineEndpointsTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static WorkItem Sample(string projectId, string? baselineRef, WorkItemState state, string? title = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId(projectId),
        Title = title ?? "wi",
        Prompt = "p",
        Agent = AgentKind.Claude,
        State = state,
        BaselineImageRef = baselineRef,
    };

    /// <summary>
    /// With no on-host baselines, both endpoints return the documented "empty"
    /// shape — <c>baselines: []</c> and <c>sweepEntries: 0</c> on /baselines;
    /// total/live/orphan-in-grace counters all zero on /admin/baseline-images.
    /// This is also the shape callers see when the provider does not implement
    /// IBaselineImageResolver (NullBaselineImageResolver returns an empty list).
    /// </summary>
    [Fact]
    public async Task GetBaselines_EmptyHost_ReturnsEmptyShape()
    {
        await _factory.SweepAsync();

        var resp = await _client.GetAsync("/baselines");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, doc.GetProperty("baselines").ValueKind);
        Assert.Equal(0, doc.GetProperty("baselines").GetArrayLength());
        Assert.Equal(0, doc.GetProperty("sweepEntries").GetInt32());
    }

    /// <summary>
    /// Asserts the per-entry JSON contract of <c>/baselines</c>: name, isLive,
    /// firstObservedOrphanAt (null when live, non-null when orphaned),
    /// ageInGraceMinutes (rounded to 1dp; null when live), and the
    /// workItems array (each entry has id, title, state).
    /// </summary>
    [Fact]
    public async Task GetBaselines_PopulatedHost_ReturnsDocumentedShape()
    {
        // One live (referenced by a Working work item) + one orphan (no reference).
        var live = Sample("p", "cb-baseline-live", WorkItemState.Working, title: "live work");
        await _factory.WorkItemStore.CreateAsync(live);
        _factory.Resolver.AddImage("cb-baseline-live");
        _factory.Resolver.AddImage("cb-baseline-orphan");

        // First sweep stamps the orphan's first-observed clock at "now".
        await _factory.SweepAsync();

        var resp = await _client.GetAsync("/baselines");
        resp.EnsureSuccessStatusCode();

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, doc.GetProperty("sweepEntries").GetInt32());

        var arr = doc.GetProperty("baselines");
        Assert.Equal(2, arr.GetArrayLength());

        var byName = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var el in arr.EnumerateArray())
            byName[el.GetProperty("name").GetString()!] = el;

        // Live entry.
        var liveEntry = byName["cb-baseline-live"];
        Assert.True(liveEntry.GetProperty("isLive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, liveEntry.GetProperty("firstObservedOrphanAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, liveEntry.GetProperty("ageInGraceMinutes").ValueKind);
        // workItems array wired to ListWorkItemsForBaselineAsync.
        var liveWorkItems = liveEntry.GetProperty("workItems");
        Assert.Equal(1, liveWorkItems.GetArrayLength());
        Assert.Equal(live.Id.ToString(), liveWorkItems[0].GetProperty("id").GetString());
        Assert.Equal("live work", liveWorkItems[0].GetProperty("title").GetString());
        Assert.Equal((int)WorkItemState.Working, liveWorkItems[0].GetProperty("state").GetInt32());

        // Orphan entry.
        var orphanEntry = byName["cb-baseline-orphan"];
        Assert.False(orphanEntry.GetProperty("isLive").GetBoolean());
        Assert.Equal(JsonValueKind.String, orphanEntry.GetProperty("firstObservedOrphanAt").ValueKind);
        Assert.True(orphanEntry.GetProperty("ageInGraceMinutes").GetDouble() >= 0);
        Assert.Equal(0, orphanEntry.GetProperty("workItems").GetArrayLength());
    }

    /// <summary>
    /// /admin/baseline-images returns counts only and never includes a
    /// workItems array on any entry — the dashboard summary must stay cheap
    /// to render.
    /// </summary>
    [Fact]
    public async Task GetAdminBaselineImages_PopulatedHost_ReturnsCountSummary()
    {
        var live = Sample("p", "cb-baseline-a", WorkItemState.Working);
        await _factory.WorkItemStore.CreateAsync(live);
        _factory.Resolver.AddImage("cb-baseline-a");
        _factory.Resolver.AddImage("cb-baseline-b");
        _factory.Resolver.AddImage("cb-baseline-c");

        await _factory.SweepAsync();

        var resp = await _client.GetAsync("/admin/baseline-images");
        resp.EnsureSuccessStatusCode();

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, doc.GetProperty("total").GetInt32());
        Assert.Equal(1, doc.GetProperty("live").GetInt32());
        Assert.Equal(2, doc.GetProperty("orphanedInGrace").GetInt32());

        var entries = doc.GetProperty("entries");
        Assert.Equal(3, entries.GetArrayLength());
        foreach (var e in entries.EnumerateArray())
        {
            // Compact summary: no workItems array.
            Assert.False(e.TryGetProperty("workItems", out _),
                "GET /admin/baseline-images entries must not embed workItems lists");
            // Each entry has name, isLive, ageInGraceMinutes.
            Assert.True(e.TryGetProperty("name", out _));
            Assert.True(e.TryGetProperty("isLive", out _));
            Assert.True(e.TryGetProperty("ageInGraceMinutes", out _));
        }
    }

    /// <summary>
    /// Sanity: only NON-terminal work items show up in a live baseline's
    /// workItems array. The reaper's "live" set is the union of refs held by
    /// non-terminal items, so a baseline solely referenced by a Done item is
    /// reported as orphan (and the workItems list still includes the Done
    /// item, since ListWorkItemsForBaselineAsync returns every match
    /// regardless of state — useful for historical attribution).
    /// </summary>
    [Fact]
    public async Task GetBaselines_TerminalRef_OrphanedButWorkItemListedHistorically()
    {
        var doneItem = Sample("p", "cb-baseline-old", WorkItemState.Done);
        await _factory.WorkItemStore.CreateAsync(doneItem);
        _factory.Resolver.AddImage("cb-baseline-old");

        await _factory.SweepAsync();

        var resp = await _client.GetAsync("/baselines");
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var entry = doc.GetProperty("baselines")[0];

        // The reaper's live-ref query excludes Done; entry is orphan.
        Assert.False(entry.GetProperty("isLive").GetBoolean());
        // But the per-baseline work-items list is the historical attribution.
        var workItems = entry.GetProperty("workItems");
        Assert.Equal(1, workItems.GetArrayLength());
        Assert.Equal(doneItem.Id.ToString(), workItems[0].GetProperty("id").GetString());
    }
}

/// <summary>
/// Web-host factory that swaps the real BaselineImageReaper / resolver with
/// a deterministic fake so the /baselines endpoint operates on a known sweep
/// state. The factory exposes <see cref="SweepAsync"/> so each test can
/// trigger the in-memory report it expects to assert.
/// </summary>
internal sealed class BaselineApiFactory : CodeyBox.Tests.CodeyBoxWebApplicationFactory
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-baseline-api-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore WorkItemStore { get; }
    public FakeResolver Resolver { get; } = new();
    private BaselineImageReaper? _reaper;

    public BaselineApiFactory() => WorkItemStore = new SqliteWorkItemStore(_dbPath);

    public Task SweepAsync()
    {
        // Force the singleton to materialize before sweeping so the swap
        // installed in ConfigureTestServices is the instance that ends up
        // exposed via DI to the endpoint.
        _reaper ??= Services.GetRequiredService<BaselineImageReaper>();
        return _reaper.RunSweepAsync(CancellationToken.None);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var tmp = Temp.Root;
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-bsl-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-bsl-audit-{Guid.NewGuid():N}-.json"),
            });
        });
        builder.ConfigureTestServices(services =>
        {
            // Stop background services — we drive the reaper manually.
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(WorkItemStore);

            // Replace the registered resolver with our deterministic fake so
            // ListBaselineImagesAsync returns the set the test configured.
            services.RemoveAll<IBaselineImageResolver>();
            services.AddSingleton<IBaselineImageResolver>(Resolver);

            // Replace BaselineImageReaper with one wired to our resolver +
            // store. Set a non-zero grace window so the entry is reported as
            // orphan-in-grace (not reaped on the first sweep).
            services.RemoveAll<BaselineImageReaper>();
            services.AddSingleton(sp => new BaselineImageReaper(
                Resolver,
                WorkItemStore,
                new BaselineImageReaperOptions { GraceWindow = TimeSpan.FromHours(24) },
                NullLogger<BaselineImageReaper>.Instance));
        });
    }

    protected override void Dispose(bool disposing)
        => DisposeHostThenDeleteSqliteDatabase(disposing, _dbPath, WorkItemStore.Dispose);
}

/// <summary>
/// Deterministic in-memory <see cref="IBaselineImageResolver"/>: tests call
/// <see cref="AddImage"/> to seed the on-host set the reaper will diff
/// against the live-ref set.
/// </summary>
internal sealed class FakeResolver : IBaselineImageResolver
{
    private readonly List<BaselineImageInfo> _images = [];
    public List<string> Disposed { get; } = [];

    public void AddImage(string name) => _images.Add(new BaselineImageInfo(name, null, null));

    public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor) => null;

    public Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BaselineImageInfo>>(_images.ToList());

    public Task DisposeBaselineImageAsync(string name, CancellationToken ct)
    {
        Disposed.Add(name);
        _images.RemoveAll(i => i.Name == name);
        return Task.CompletedTask;
    }
}
