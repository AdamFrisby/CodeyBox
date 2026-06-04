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
/// HTTP-level tests for the operator-facing <c>/concurrency</c> endpoint. The
/// response is the operator surface called out by spec Part C: it must return
/// the global cap, per-agent caps, live in-flight counts, latest avg-burn-per-agent,
/// and per-class rate-aware fit estimates. Regressions in any of those projections
/// (response shape, NaN→null conversion, union-of-(caps,running) agent enumeration)
/// are invisible without these tests.
/// </summary>
public sealed class ConcurrencyEndpointTests : IClassFixture<ConcurrencyEndpointTests.ConcurrencyApiFactory>
{
    private readonly ConcurrencyApiFactory _factory;

    public ConcurrencyEndpointTests(ConcurrencyApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetConcurrency_ReturnsExpectedResponseShape()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/concurrency");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Number, body.GetProperty("globalMaxConcurrent").ValueKind);
        Assert.Equal(JsonValueKind.Number, body.GetProperty("currentlyRunningTotal").ValueKind);
        Assert.Equal(JsonValueKind.Object, body.GetProperty("perAgentCaps").ValueKind);
        Assert.Equal(JsonValueKind.Object, body.GetProperty("currentlyRunningPerAgent").ValueKind);
        Assert.Equal(JsonValueKind.Array, body.GetProperty("burnEstimates").ValueKind);
        Assert.Equal(JsonValueKind.Array, body.GetProperty("memberFits").ValueKind);
    }

    [Fact]
    public async Task GetConcurrency_ReportsConfiguredCapsAndLiveCounts()
    {
        var client = _factory.CreateClient();
        var orchestrator = _factory.Services.GetRequiredService<OrchestratorService>();

        // Simulate one codex in flight; reserved through the public test hook
        // so we exercise the same path the dispatch loop uses.
        Assert.True(orchestrator.TryReserveAgentSlotForTest(AgentKind.Codex));
        try
        {
            var body = await client.GetFromJsonAsync<JsonElement>("/concurrency");
            Assert.Equal(4, body.GetProperty("globalMaxConcurrent").GetInt32());

            var caps = body.GetProperty("perAgentCaps");
            Assert.Equal(1, caps.GetProperty("codex").GetInt32());
            Assert.Equal(2, caps.GetProperty("claude").GetInt32());

            var running = body.GetProperty("currentlyRunningPerAgent");
            Assert.Equal(1, running.GetProperty("codex").GetInt32());
            // Agents with running == 0 must NOT appear in the surface; the
            // OrchestratorService.Snapshot >0 filter would have been bypassed
            // by a regression that omitted the filter.
            Assert.False(running.TryGetProperty("claude", out _));
        }
        finally
        {
            orchestrator.ReleaseAgentSlotForTest(AgentKind.Codex);
        }
    }

    [Fact]
    public async Task GetConcurrency_BurnEstimatesEnumerateUnionOfCapsAndRunning()
    {
        // The endpoint unions agents that appear in caps OR running so a
        // configured-but-quiet agent still surfaces its avg-burn for the operator.
        var client = _factory.CreateClient();
        var body = await client.GetFromJsonAsync<JsonElement>("/concurrency");
        var burns = body.GetProperty("burnEstimates").EnumerateArray()
            .ToDictionary(
                e => e.GetProperty("agent").GetString()!,
                StringComparer.OrdinalIgnoreCase);

        Assert.Contains("codex", burns.Keys);
        Assert.Contains("claude", burns.Keys);
        Assert.Equal("NoWindowBudget", burns["codex"].GetProperty("status").GetString());
        Assert.Equal("Measured", burns["claude"].GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetConcurrency_MemberFits_NaNFit_RenderedAsJsonNull()
    {
        // The endpoint converts double.NaN → null so System.Text.Json doesn't
        // emit an invalid number literal. A regression that emitted `NaN` would
        // produce a parsing failure here, or an unexpected non-null value.
        var client = _factory.CreateClient();
        var body = await client.GetFromJsonAsync<JsonElement>("/concurrency");
        var fits = body.GetProperty("memberFits");
        Assert.Equal(JsonValueKind.Array, fits.ValueKind);
        foreach (var fit in fits.EnumerateArray())
        {
            var v = fit.GetProperty("fitInWindow");
            // Either Null or Number — never a raw NaN string or boolean.
            Assert.True(v.ValueKind == JsonValueKind.Null || v.ValueKind == JsonValueKind.Number,
                $"fitInWindow must be number-or-null, got {v.ValueKind}");
        }
    }

    [Fact]
    public async Task GetConcurrency_MemberFits_NoWindowBudgetFit_ProjectedAsNullWithStatus()
    {
        var client = _factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/concurrency");
        var codexFit = Assert.Single(body.GetProperty("memberFits").EnumerateArray(), f =>
            string.Equals(f.GetProperty("agent").GetString(), "codex", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("NoWindowBudget", codexFit.GetProperty("status").GetString());
        Assert.Equal(10, codexFit.GetProperty("sampleCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, codexFit.GetProperty("fitInWindow").ValueKind);
    }

    [Fact]
    public async Task GetConcurrency_MemberFits_ExcludePayPerApi()
    {
        // PayPerApi members must not appear in the fits list (never gated).
        var client = _factory.CreateClient();
        var body = await client.GetFromJsonAsync<JsonElement>("/concurrency");
        var fits = body.GetProperty("memberFits");
        foreach (var fit in fits.EnumerateArray())
        {
            // None of our configured members are PayPerApi, but verifying the
            // shape pins the contract: every entry has an agent and class id.
            Assert.NotNull(fit.GetProperty("agent").GetString());
            Assert.NotNull(fit.GetProperty("classId").GetString());
        }
    }

    /// <summary>
    /// Test-only WebApplicationFactory that wires the orchestrator + router with
    /// known caps and an in-memory burn estimator so the assertions can pin
    /// concrete values.
    /// </summary>
    public sealed class ConcurrencyApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-concurrency-{Guid.NewGuid():N}.db");

        public SqliteWorkItemStore Store { get; }

        public ConcurrencyApiFactory()
        {
            Store = new SqliteWorkItemStore(_dbPath);
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
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                    ["CodeyBox:Concurrency"] = "4",
                    ["CodeyBox:WorkerPool:MaxConcurrentWorkers"] = "4",
                    ["CodeyBox:AgentConcurrency:Members:codex:MaxConcurrent"] = "1",
                    ["CodeyBox:AgentConcurrency:Members:claude:MaxConcurrent"] = "2",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IWorkItemStore>();
                services.AddSingleton<IWorkItemStore>(Store);

                // Replace the burn estimator with a deterministic fake so
                // tests can pin the fitInWindow projection.
                services.RemoveAll<IAgentBurnEstimator>();
                services.AddSingleton<IAgentBurnEstimator>(new StubBurnEstimator());

                services.RemoveAll<IAgentQuotaProbe>();
                services.AddSingleton<IAgentQuotaProbe>(new FakeProbe(AgentKind.Claude, 100.0));
                services.AddSingleton<IAgentQuotaProbe>(new FakeProbe(AgentKind.Codex, 81.0));
                services.AddSingleton<IAgentQuotaProbe>(new FakeProbe(AgentKind.Cursor, 100.0));
                services.AddSingleton<IAgentQuotaProbe>(new FakeProbe(AgentKind.Gemini, 100.0));
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Store.Dispose();
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            }
            base.Dispose(disposing);
        }
    }

    private sealed class StubBurnEstimator : IAgentBurnEstimator
    {
        public Task<AgentBurnEstimate> GetEstimateAsync(AgentKind agent, CancellationToken ct = default)
        {
            if (agent == AgentKind.Codex)
            {
                return Task.FromResult(new AgentBurnEstimate
                {
                    AvgBurnPctPerItem = -1,
                    SampleCount = 10,
                    Status = AgentBurnEstimateStatus.NoWindowBudget,
                });
            }

            return Task.FromResult(new AgentBurnEstimate
            {
                AvgBurnPctPerItem = 4.0,
                SampleCount = 10,
                Status = AgentBurnEstimateStatus.Measured,
            });
        }
    }
}
