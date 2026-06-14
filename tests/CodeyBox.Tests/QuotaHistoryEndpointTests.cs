using System.Net;
using System.Text.Json;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level coverage for <c>GET /quota/history</c>. Two shapes:
///   - 503 when the statistics plugin is not loaded (no
///     <see cref="IQuotaTimeSeriesStore"/> registered)
///   - 200 with the normalised rows when an implementation is registered
/// </summary>
[Collection("GlobalSerilog")]
public sealed class QuotaHistoryEndpointTests
{
    [Fact]
    public async Task NoStoreRegistered_Returns503WithProblemBody()
    {
        await using var factory = new QuotaHistoryApiFactory(store: null);
        using var client = factory.CreateClient();

        using var resp = await client.GetAsync("/quota/history?agent=claude");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("statistics plugin", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StoreRegistered_Returns200WithRows()
    {
        var snapshotTime = DateTimeOffset.Parse("2026-06-14T15:00:00Z");
        var store = new InMemoryQuotaTimeSeriesStore();
        store.Add(new QuotaSampleRow(
            SampledAt: snapshotTime,
            Agent: "claude",
            ModelId: null,
            OverallPct: 42,
            WouldAllow: true,
            Notes: "ok",
            WindowName: "five_hour",
            WindowPct: 88,
            WindowResetAt: snapshotTime.AddHours(3),
            IsKnown: true,
            UnknownReason: null));

        await using var factory = new QuotaHistoryApiFactory(store);
        using var client = factory.CreateClient();

        using var resp = await client.GetAsync("/quota/history?agent=claude&window=five_hour");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        var first = doc.RootElement.GetProperty("rows")[0];
        Assert.Equal("claude", first.GetProperty("agent").GetString());
        Assert.Equal("five_hour", first.GetProperty("windowName").GetString());
        Assert.Equal(88, first.GetProperty("windowPct").GetDouble());
        Assert.True(first.GetProperty("wouldAllow").GetBoolean());
    }

    [Fact]
    public async Task RawTrueQueryParam_ReturnsRawSnapshotRows()
    {
        var snapshotTime = DateTimeOffset.Parse("2026-06-14T15:00:00Z");
        var store = new InMemoryQuotaTimeSeriesStore();
        store.AddRaw(new QuotaRawSnapshotRow(snapshotTime, "claude", null, "{\"AvailablePct\":42}"));

        await using var factory = new QuotaHistoryApiFactory(store);
        using var client = factory.CreateClient();

        using var resp = await client.GetAsync("/quota/history?agent=claude&raw=true");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.GetProperty("rows");
        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal("{\"AvailablePct\":42}", rows[0].GetProperty("rawJson").GetString());
    }

    [Fact]
    public async Task InvalidTimeRange_Returns400()
    {
        await using var factory = new QuotaHistoryApiFactory(new InMemoryQuotaTimeSeriesStore());
        using var client = factory.CreateClient();

        using var resp = await client.GetAsync(
            "/quota/history?from=2026-06-14T15:00:00Z&to=2026-06-14T10:00:00Z");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private sealed class InMemoryQuotaTimeSeriesStore : IQuotaTimeSeriesStore
    {
        private readonly List<QuotaSampleRow> _rows = new();
        private readonly List<QuotaRawSnapshotRow> _raw = new();

        public void Add(QuotaSampleRow row) => _rows.Add(row);
        public void AddRaw(QuotaRawSnapshotRow row) => _raw.Add(row);

        public Task<IReadOnlyList<QuotaSampleRow>> QueryAsync(
            QuotaTimeSeriesFilter filter,
            CancellationToken ct = default)
        {
            IEnumerable<QuotaSampleRow> q = _rows;
            if (filter.Agent is { } a)
                q = q.Where(r => string.Equals(r.Agent, a, StringComparison.OrdinalIgnoreCase));
            if (filter.WindowName is { } w)
            {
                if (string.Equals(w, "overall", StringComparison.OrdinalIgnoreCase))
                    q = q.Where(r => r.WindowName is null);
                else
                    q = q.Where(r => string.Equals(r.WindowName, w, StringComparison.OrdinalIgnoreCase));
            }
            return Task.FromResult<IReadOnlyList<QuotaSampleRow>>(q.ToList());
        }

        public Task<IReadOnlyList<QuotaRawSnapshotRow>> QueryRawAsync(
            QuotaTimeSeriesFilter filter,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<QuotaRawSnapshotRow>>(_raw);
    }

    private sealed class QuotaHistoryApiFactory : WebApplicationFactory<Program>
    {
        private readonly IQuotaTimeSeriesStore? _store;
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"codeybox-qh-test-{Guid.NewGuid():N}.db");

        public QuotaHistoryApiFactory(IQuotaTimeSeriesStore? store)
        {
            _store = store;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var tmp = Path.GetTempPath();
                var config = new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:SandboxProvider"] = "process",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"qh-test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"qh-test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"qh-test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"qh-test-agent-streams-{Guid.NewGuid():N}"),
                    ["CodeyBox:Changelog:Enabled"] = "false",
                };
                cfg.AddInMemoryCollection(config);
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IProjectRepository>();
                services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
                if (_store is not null)
                {
                    services.RemoveAll<IQuotaTimeSeriesStore>();
                    services.AddSingleton(_store);
                }
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            }
            base.Dispose(disposing);
        }
    }
}
