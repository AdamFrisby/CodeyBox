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
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level coverage for <c>GET /stats/capacity</c>. The calculator tests
/// drive the algorithm; this file pins the route, query-binding, validation,
/// and the 503/400 fallback paths so a regression that broke parameter
/// trimming, default <c>minDeltaPct</c> handling, or the 'calculator not
/// registered' fallback would not pass the calculator suite undetected.
/// </summary>
public sealed class CapacityEndpointTests : IClassFixture<CapacityEndpointTests.CapacityApiFactory>
{
    private readonly CapacityApiFactory _factory;

    public CapacityEndpointTests(CapacityApiFactory factory)
    {
        _factory = factory;
        _factory.Calculator.Reset();
    }

    [Fact]
    public async Task Get_NoCalculatorRegistered_Returns503()
    {
        // Statistics plugin not loaded — the endpoint route exists but the
        // calculator service is absent. ProblemDetails 503 surfaces with a
        // setup hint instead of 404.
        using var bareFactory = new BareCapacityApiFactory();
        using var client = bareFactory.CreateClient();

        var resp = await client.GetAsync("/stats/capacity");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("statistics plugin", body.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_DefaultParams_PassesNullFilters_AndIncludeIntervalsTrue()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/stats/capacity");
        resp.EnsureSuccessStatusCode();

        Assert.NotNull(_factory.Calculator.LastFilter);
        var f = _factory.Calculator.LastFilter!;
        Assert.Null(f.Agent);
        Assert.Null(f.WindowName);
        Assert.Null(f.ModelId);
        Assert.Null(f.FromUtc);
        Assert.Null(f.ToUtc);
        Assert.Equal(0.25, f.MinDeltaPct, 6); // default
        Assert.True(f.IncludeIntervals); // default
    }

    [Fact]
    public async Task Get_BindsAndTrimsQueryParameters()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            "/stats/capacity?agent=%20claude%20&window=%20seven_day%20&model=%20opus%20" +
            "&minDeltaPct=1.5&includeIntervals=false");
        resp.EnsureSuccessStatusCode();

        var f = _factory.Calculator.LastFilter!;
        Assert.Equal("claude", f.Agent);
        Assert.Equal("seven_day", f.WindowName);
        Assert.Equal("opus", f.ModelId);
        Assert.Equal(1.5, f.MinDeltaPct, 6);
        Assert.False(f.IncludeIntervals);
    }

    [Fact]
    public async Task Get_BlankQueryParameters_AreTreatedAsNull()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/stats/capacity?agent=&window=&model=");
        resp.EnsureSuccessStatusCode();

        var f = _factory.Calculator.LastFilter!;
        Assert.Null(f.Agent);
        Assert.Null(f.WindowName);
        Assert.Null(f.ModelId);
    }

    [Fact]
    public async Task Get_NegativeMinDeltaPct_FallsBackToDefault()
    {
        // The endpoint clamps a negative input to the default rather than
        // propagating a value that would let every noise tick count.
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/stats/capacity?minDeltaPct=-5");
        resp.EnsureSuccessStatusCode();

        Assert.Equal(0.25, _factory.Calculator.LastFilter!.MinDeltaPct, 6);
    }

    [Fact]
    public async Task Get_FromGreaterOrEqualTo_Returns400()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            "/stats/capacity?from=2026-06-14T15:00:00Z&to=2026-06-14T15:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("from", body.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(_factory.Calculator.LastFilter); // calculator never invoked
    }

    [Fact]
    public async Task Get_HorizonExceedsCap_Returns400()
    {
        var client = _factory.CreateClient();
        // 61 days > 60-day cap.
        var resp = await client.GetAsync(
            "/stats/capacity?from=2026-01-01T00:00:00Z&to=2026-03-04T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("60 days", body.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(_factory.Calculator.LastFilter);
    }

    [Fact]
    public async Task Get_ConfidenceSerializedAsString()
    {
        // Pinning the wire contract: CapacityConfidence MUST be emitted as a
        // string ("Medium" / "High" / "Low" / "None") so the admin DTO that
        // declares the field as `string Confidence` deserializes successfully.
        // A regression that dropped the JsonStringEnumConverter would emit a
        // number here and break the admin Capacity dashboard.
        _factory.Calculator.NextReport = new CapacityReport(
            GeneratedAt: DateTimeOffset.Parse("2026-06-14T15:00:00Z"),
            FromUtc: DateTimeOffset.Parse("2026-06-07T15:00:00Z"),
            ToUtc: DateTimeOffset.Parse("2026-06-14T15:00:00Z"),
            Entries:
            [
                new CapacityEntry
                {
                    Agent = "claude",
                    WindowName = "seven_day",
                    SampleIntervals = 5,
                    TotalDeltaPct = 10,
                    TotalInputTokens = 100,
                    TotalCachedInputTokens = 0,
                    TotalOutputTokens = 20,
                    TotalRequests = 5,
                    TotalCostMicroCents = 0,
                    Confidence = CapacityConfidence.Medium,
                },
            ]);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/stats/capacity");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var entry = body.GetProperty("entries").EnumerateArray().Single();
        var confidence = entry.GetProperty("confidence");
        Assert.Equal(JsonValueKind.String, confidence.ValueKind);
        Assert.Equal("Medium", confidence.GetString());
    }

    // ── Test fixtures ─────────────────────────────────────────────────────────

    public sealed class CapacityApiFactory : CodeyBox.Tests.CodeyBoxWebApplicationFactory
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-capacity-endpoint-{Guid.NewGuid():N}.db");

        public RecordingCalculator Calculator { get; } = new();

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
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"capacity-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"capacity-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"capacity-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"capacity-streams-{Guid.NewGuid():N}"),
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddSingleton<ICapacityCalculator>(Calculator);
            });
        }

        protected override void Dispose(bool disposing)
        => DisposeHostThenDeleteSqliteDatabase(disposing, _dbPath);
    }

    /// <summary>
    /// Variant factory that does NOT register an ICapacityCalculator — used to
    /// pin the 503 fallback path. Cannot be a class-fixture state on the
    /// shared factory because Program.cs may register a default itself; this
    /// per-test factory keeps the contract clear.
    /// </summary>
    public sealed class BareCapacityApiFactory : CodeyBox.Tests.CodeyBoxWebApplicationFactory
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-capacity-bare-{Guid.NewGuid():N}.db");

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
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"capacity-bare-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"capacity-bare-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"capacity-bare-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"capacity-bare-streams-{Guid.NewGuid():N}"),
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<ICapacityCalculator>();
            });
        }

        protected override void Dispose(bool disposing)
        => DisposeHostThenDeleteSqliteDatabase(disposing, _dbPath);
    }

    /// <summary>
    /// Capacity calculator stub: records the filter the endpoint passes through
    /// (so the test can assert query-binding / trimming / defaulting) and
    /// returns a programmable report (so the JSON serialization contract can
    /// be pinned end-to-end).
    /// </summary>
    public sealed class RecordingCalculator : ICapacityCalculator
    {
        public CapacityFilter? LastFilter { get; private set; }
        public CapacityReport? NextReport { get; set; }

        public void Reset()
        {
            LastFilter = null;
            NextReport = null;
        }

        public Task<CapacityReport> ComputeAsync(CapacityFilter filter, CancellationToken ct = default)
        {
            LastFilter = filter;
            return Task.FromResult(NextReport ?? new CapacityReport(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow - TimeSpan.FromHours(1),
                DateTimeOffset.UtcNow,
                Array.Empty<CapacityEntry>()));
        }
    }
}
