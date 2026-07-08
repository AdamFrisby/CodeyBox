using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level coverage for <c>GET /quota/reset-advice</c>. The evaluator tests
/// drive the decision logic; this file pins the route, query-binding,
/// validation, the 503 'advisor not registered' fallback, and the JSON
/// contract of the advice record.
/// </summary>
public sealed class ResetAdviceEndpointTests : IClassFixture<ResetAdviceEndpointTests.ResetAdviceApiFactory>
{
    private readonly ResetAdviceApiFactory _factory;

    public ResetAdviceEndpointTests(ResetAdviceApiFactory factory)
    {
        _factory = factory;
        _factory.Advisor.Reset();
    }

    [Fact]
    public async Task Get_NoAdvisorRegistered_Returns503()
    {
        using var bareFactory = new BareResetAdviceApiFactory();
        using var client = bareFactory.CreateClient();

        var resp = await client.GetAsync("/quota/reset-advice");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("statistics plugin", body.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_DefaultParams_PassesNullFilters()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/quota/reset-advice");
        resp.EnsureSuccessStatusCode();

        Assert.NotNull(_factory.Advisor.LastRequest);
        Assert.Null(_factory.Advisor.LastRequest!.Agent);
        Assert.Null(_factory.Advisor.LastRequest.FromUtc);
        Assert.Null(_factory.Advisor.LastRequest.ToUtc);
    }

    [Fact]
    public async Task Get_BindsAndTrimsQueryParameters()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            "/quota/reset-advice?agent=%20codex%20&from=2026-06-01T00:00:00Z&to=2026-07-01T00:00:00Z");
        resp.EnsureSuccessStatusCode();

        var r = _factory.Advisor.LastRequest!;
        Assert.Equal("codex", r.Agent);
        Assert.Equal(DateTimeOffset.Parse("2026-06-01T00:00:00Z"), r.FromUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-07-01T00:00:00Z"), r.ToUtc);
    }

    [Fact]
    public async Task Get_FromGreaterOrEqualTo_Returns400()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            "/quota/reset-advice?from=2026-06-14T15:00:00Z&to=2026-06-14T15:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(_factory.Advisor.LastRequest); // never invoked
    }

    [Fact]
    public async Task Get_SerializesAdvice_WithVerdictReasonAndWindow()
    {
        var now = DateTimeOffset.Parse("2026-07-03T12:00:00Z");
        var deadline = now + TimeSpan.FromDays(1);
        _factory.Advisor.NextAdvice = new ResetSpendAdvice
        {
            Agent = "codex",
            EvaluatedAt = now,
            ShouldSpend = true,
            Reason = ResetAdviceReason.SpendBeforeDeadline,
            Rationale = "spend before the deadline",
            PredictedNaturalReset = now + TimeSpan.FromDays(5),
            DecisionDeadline = deadline,
            NextCreditExpiresAt = deadline,
            NextCreditIsEstimated = false,
            UsableQuotaPct = 0.0,
            DustThresholdPct = 1.0,
            OptimalWindow = new ResetSpendWindow(now, deadline),
        };

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/quota/reset-advice");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("shouldSpend").GetBoolean());
        // The enum serialises as its string name by the API's JSON options.
        Assert.Equal("SpendBeforeDeadline", body.GetProperty("reason").GetString());
        Assert.Equal(deadline, body.GetProperty("decisionDeadline").GetDateTimeOffset());
        var window = body.GetProperty("optimalWindow");
        Assert.Equal(now, window.GetProperty("opensAt").GetDateTimeOffset());
        Assert.Equal(deadline, window.GetProperty("closesAt").GetDateTimeOffset());
    }

    // ── Test fixtures ─────────────────────────────────────────────────────────

    public sealed class ResetAdviceApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-resetadvice-endpoint-{Guid.NewGuid():N}.db");

        public RecordingAdvisor Advisor { get; } = new();

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
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"resetadvice-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"resetadvice-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"resetadvice-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"resetadvice-streams-{Guid.NewGuid():N}"),
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddSingleton<IResetOptimalityAdvisor>(Advisor);
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
    }

    public sealed class BareResetAdviceApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-resetadvice-bare-{Guid.NewGuid():N}.db");

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
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"resetadvice-bare-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"resetadvice-bare-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"resetadvice-bare-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"resetadvice-bare-streams-{Guid.NewGuid():N}"),
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IResetOptimalityAdvisor>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Advisor stub: records the request the endpoint passes through and returns
    /// a programmable advice so the JSON contract can be pinned end-to-end.
    /// </summary>
    public sealed class RecordingAdvisor : IResetOptimalityAdvisor
    {
        public ResetAdviceRequest? LastRequest { get; private set; }
        public ResetSpendAdvice? NextAdvice { get; set; }

        public void Reset()
        {
            LastRequest = null;
            NextAdvice = null;
        }

        public Task<ResetSpendAdvice> AdviseAsync(ResetAdviceRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(NextAdvice ?? new ResetSpendAdvice
            {
                Agent = "codex",
                EvaluatedAt = default,
                ShouldSpend = false,
                Reason = ResetAdviceReason.NoBankedCredit,
                Rationale = "stub",
            });
        }
    }
}
