using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level coverage for <c>GET /quota/reset-credits</c>. The tracker and
/// estimator tests drive the derivation; this file pins the route,
/// query-binding, validation, the 503 'estimator not registered' fallback,
/// and the JSON contract of the report.
/// </summary>
public sealed class ResetCreditEndpointTests : IClassFixture<ResetCreditEndpointTests.ResetCreditApiFactory>
{
    private readonly ResetCreditApiFactory _factory;

    public ResetCreditEndpointTests(ResetCreditApiFactory factory)
    {
        _factory = factory;
        _factory.Estimator.Reset();
    }

    [Fact]
    public async Task Get_NoEstimatorRegistered_Returns503()
    {
        using var bareFactory = new BareResetCreditApiFactory();
        using var client = bareFactory.CreateClient();

        var resp = await client.GetAsync("/quota/reset-credits");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("statistics plugin", body.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_DefaultParams_PassesNullFilters()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/quota/reset-credits");
        resp.EnsureSuccessStatusCode();

        Assert.NotNull(_factory.Estimator.LastQuery);
        Assert.Null(_factory.Estimator.LastQuery!.Agent);
        Assert.Null(_factory.Estimator.LastQuery.FromUtc);
        Assert.Null(_factory.Estimator.LastQuery.ToUtc);
    }

    [Fact]
    public async Task Get_BindsAndTrimsQueryParameters()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            "/quota/reset-credits?agent=%20codex%20&from=2026-06-01T00:00:00Z&to=2026-07-01T00:00:00Z");
        resp.EnsureSuccessStatusCode();

        var q = _factory.Estimator.LastQuery!;
        Assert.Equal("codex", q.Agent);
        Assert.Equal(DateTimeOffset.Parse("2026-06-01T00:00:00Z"), q.FromUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-07-01T00:00:00Z"), q.ToUtc);
    }

    [Fact]
    public async Task Get_FromGreaterOrEqualTo_Returns400()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            "/quota/reset-credits?from=2026-06-14T15:00:00Z&to=2026-06-14T15:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(_factory.Estimator.LastQuery); // never invoked
    }

    [Fact]
    public async Task Get_SerializesReport_WithEstimatedFlagAndNextExpiry()
    {
        var expires = DateTimeOffset.Parse("2026-07-16T00:00:00Z");
        _factory.Estimator.NextReport = new ResetCreditExpiryReport
        {
            LatestObservedCount = 2,
            ExpiryPeriod = TimeSpan.FromDays(30),
            SafetyBuffer = TimeSpan.FromHours(24),
            NextCreditExpiresAt = expires - TimeSpan.FromHours(24),
            Credits =
            [
                new BankedResetCredit
                {
                    GrantedAt = expires - TimeSpan.FromDays(30),
                    ExpiresAt = expires,
                    AdvisedSpendByAt = expires - TimeSpan.FromHours(24),
                    IsEstimated = true,
                    Label = "credit A",
                },
            ],
        };

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/quota/reset-credits");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("latestObservedCount").GetInt32());
        var credit = body.GetProperty("credits").EnumerateArray().Single();
        Assert.True(credit.GetProperty("isEstimated").GetBoolean());
        Assert.Equal("credit A", credit.GetProperty("label").GetString());
        Assert.Equal(expires, credit.GetProperty("expiresAt").GetDateTimeOffset());
    }

    // ── Test fixtures ─────────────────────────────────────────────────────────

    public sealed class ResetCreditApiFactory : CodeyBoxWebApplicationFactory
    {
        private readonly string _dbPath;
        private readonly string _gitRoot;
        private readonly string _auditLogPath;
        private readonly string _auditPath;
        private readonly string _agentStreamsPath;

        public ResetCreditApiFactory()
        {
            _dbPath = TempDatabasePath("codeybox-resetcredit-endpoint");
            _gitRoot = Temp.NewDirectoryPath("resetcredit-git-");
            _auditLogPath = Temp.NewLogPath("resetcredit-log");
            _auditPath = Temp.NewLogPath("resetcredit-audit");
            _agentStreamsPath = Temp.NewDirectoryPath("resetcredit-streams-");
        }

        public RecordingEstimator Estimator { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = _gitRoot,
                    ["CodeyBox:AuditLog:Path"] = _auditLogPath,
                    ["CodeyBox:AuditLog:AuditPath"] = _auditPath,
                    ["CodeyBox:AgentStreams:Path"] = _agentStreamsPath,
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddSingleton<IResetCreditExpiryEstimator>(Estimator);
            });
        }
    }

    public sealed class BareResetCreditApiFactory : CodeyBoxWebApplicationFactory
    {
        private readonly string _dbPath;
        private readonly string _gitRoot;
        private readonly string _auditLogPath;
        private readonly string _auditPath;
        private readonly string _agentStreamsPath;

        public BareResetCreditApiFactory()
        {
            _dbPath = TempDatabasePath("codeybox-resetcredit-bare");
            _gitRoot = Temp.NewDirectoryPath("resetcredit-bare-git-");
            _auditLogPath = Temp.NewLogPath("resetcredit-bare-log");
            _auditPath = Temp.NewLogPath("resetcredit-bare-audit");
            _agentStreamsPath = Temp.NewDirectoryPath("resetcredit-bare-streams-");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = _gitRoot,
                    ["CodeyBox:AuditLog:Path"] = _auditLogPath,
                    ["CodeyBox:AuditLog:AuditPath"] = _auditPath,
                    ["CodeyBox:AgentStreams:Path"] = _agentStreamsPath,
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IResetCreditExpiryEstimator>();
            });
        }
    }

    /// <summary>
    /// Estimator stub: records the query the endpoint passes through and returns
    /// a programmable report so the JSON contract can be pinned end-to-end.
    /// </summary>
    public sealed class RecordingEstimator : IResetCreditExpiryEstimator
    {
        public ResetCreditExpiryQuery? LastQuery { get; private set; }
        public ResetCreditExpiryReport? NextReport { get; set; }

        public void Reset()
        {
            LastQuery = null;
            NextReport = null;
        }

        public Task<ResetCreditExpiryReport> EstimateAsync(ResetCreditExpiryQuery query, CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(NextReport ?? new ResetCreditExpiryReport());
        }
    }
}
