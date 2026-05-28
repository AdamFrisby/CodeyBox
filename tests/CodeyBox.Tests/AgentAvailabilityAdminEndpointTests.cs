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
/// HTTP-level coverage for the three new operator endpoints introduced for
/// agent availability: <c>POST /admin/agent/{name}/smoke</c>,
/// <c>POST /admin/agent/{name}/reset</c>, and
/// <c>GET /admin/agents/availability</c>. Acceptance criterion 2 of the
/// availability spec ("operator installs the binary, runs
/// <c>/admin/agent/cursor/smoke</c> ... agent rejoins the chain") is the
/// operator-facing flow these tests pin down. Unit-level registry tests do
/// not exercise the WebApplicationFactory route, so without this file a
/// regression that swapped Reset/GetAvailability or 200'd an unknown name
/// would ship undetected.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class AgentAvailabilityAdminEndpointTests
    : IClassFixture<AgentAvailabilityAdminEndpointTests.AvailabilityAdminApiFactory>
{
    private readonly AvailabilityAdminApiFactory _factory;

    public AgentAvailabilityAdminEndpointTests(AvailabilityAdminApiFactory factory)
    {
        _factory = factory;
        _factory.Reset();
    }

    [Fact]
    public async Task PostSmoke_PassingProbe_RestoresExcludedAgent_And_ReturnsAvailabilityJson()
    {
        var registry = _factory.Services.GetRequiredService<AgentAvailabilityRegistry>();
        registry.MarkSmokeResult(AgentKind.Claude, new AgentSmokeResult(false, "auth", TimeSpan.Zero));
        Assert.False(registry.GetAvailability(AgentKind.Claude).Available);

        _factory.SetProbeResult(AgentKind.Claude, pass: true);

        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/admin/agent/claude/smoke", content: null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("claude", body.GetProperty("agent").GetString());
        Assert.True(body.GetProperty("smoke").GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Number, body.GetProperty("smoke").GetProperty("durationMs").ValueKind);
        Assert.True(body.GetProperty("availability").GetProperty("available").GetBoolean());

        Assert.True(registry.GetAvailability(AgentKind.Claude).Available);
    }

    [Fact]
    public async Task PostSmoke_FailingProbe_ExcludesAgent_And_ReturnsReason()
    {
        _factory.SetProbeResult(AgentKind.Claude, pass: false);

        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/admin/agent/claude/smoke", content: null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var smoke = body.GetProperty("smoke");
        Assert.False(smoke.GetProperty("ok").GetBoolean());
        Assert.Equal("auth", smoke.GetProperty("reason").GetString());

        var availability = body.GetProperty("availability");
        Assert.False(availability.GetProperty("available").GetBoolean());
        Assert.Contains("auth", availability.GetProperty("reason").GetString());

        var registry = _factory.Services.GetRequiredService<AgentAvailabilityRegistry>();
        Assert.False(registry.GetAvailability(AgentKind.Claude).Available);
    }

    [Fact]
    public async Task PostSmoke_UnknownAgent_Returns404()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/admin/agent/curser/smoke", content: null);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("curser", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostReset_ClearsFastFailExclusion_And_ReturnsAvailable()
    {
        var registry = _factory.Services.GetRequiredService<AgentAvailabilityRegistry>();
        // Drive the breaker by hand — the endpoint is the unit under test, not
        // the registry math (which is covered by AgentAvailabilityRegistryTests).
        for (var i = 0; i < 3; i++)
            registry.RecordRunOutcome(AgentKind.Claude, success: false, duration: TimeSpan.FromSeconds(1));
        Assert.False(registry.GetAvailability(AgentKind.Claude).Available);

        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/admin/agent/claude/reset", content: null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("claude", body.GetProperty("agent").GetString());
        Assert.True(body.GetProperty("availability").GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("availability").GetProperty("reason").ValueKind);

        Assert.True(registry.GetAvailability(AgentKind.Claude).Available);
    }

    [Fact]
    public async Task PostReset_InvalidatesInVmSmokeCache_ForcingReprobe()
    {
        // The reset endpoint must drop any cached passing in-VM verdict so the
        // next sweep / gated dispatch re-execs the CLI instead of replaying a
        // stale pass (which could mark a broken binary Available without re-
        // running it). A regression that dropped the inVmCache.Invalidate call
        // would leave the entry in place.
        var cache = _factory.Services.GetRequiredService<IInVmSmokeCache>();
        cache.Set(AgentKind.Claude, "baseline-ref-A", new AgentSmokeResult(true, null, TimeSpan.Zero));
        Assert.NotNull(cache.TryGet(AgentKind.Claude, "baseline-ref-A"));

        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/admin/agent/claude/reset", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Null(cache.TryGet(AgentKind.Claude, "baseline-ref-A"));
    }

    [Fact]
    public async Task PostReset_UnknownAgent_Returns404()
    {
        // An operator typo (/admin/agent/curser/reset) used to silently return
        // 200 because Reset is a no-op on unknown names. /smoke validates via
        // probe lookup; /reset now validates via the agent registry so typos
        // are visible. Without this regression guard, that quality of life
        // could regress unnoticed.
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/admin/agent/curser/reset", content: null);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PostReset_CapitalisedAgentName_NormalisesToCanonicalLowercase()
    {
        // Canonical AgentKind values are lowercase. A capitalised but otherwise
        // valid name (POST /admin/agent/Claude/reset) used to return 404 because
        // AgentKind equality is case-sensitive. The endpoint now lowercases the
        // route value before lookup so case-mismatched typos resolve to the
        // canonical kind rather than masquerading as "unknown agent".
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/admin/agent/Claude/reset", content: null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("claude", body.GetProperty("agent").GetString());
    }

    [Fact]
    public async Task PostSmoke_CapitalisedAgentName_NormalisesToCanonicalLowercase()
    {
        _factory.SetProbeResult(AgentKind.Claude, pass: true);
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/admin/agent/Claude/smoke", content: null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("claude", body.GetProperty("agent").GetString());
    }

    [Fact]
    public async Task GetAvailability_FlattensRegistrySnapshot()
    {
        var registry = _factory.Services.GetRequiredService<AgentAvailabilityRegistry>();
        registry.MarkSmokeResult(AgentKind.Claude, new AgentSmokeResult(true, null, TimeSpan.FromMilliseconds(10)));
        registry.MarkSmokeResult(AgentKind.Codex, new AgentSmokeResult(false, "auth", TimeSpan.FromMilliseconds(10)));

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/admin/agents/availability");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var agents = body.GetProperty("agents").EnumerateArray().ToList();

        var claude = agents.Single(a => a.GetProperty("agent").GetString() == "claude");
        Assert.False(claude.GetProperty("excluded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, claude.GetProperty("reason").ValueKind);
        Assert.Equal(0, claude.GetProperty("consecutiveFastFails").GetInt32());
        // Timestamp pin: a regression that swapped the smoke-pass / smoke-fail
        // branches in MarkSmokeResult would surface as lastSmokePassedAt being
        // null on the passing entry (or set on the failing one). We don't pin
        // the value, just its presence/absence.
        Assert.NotEqual(JsonValueKind.Null, claude.GetProperty("lastSmokePassedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, claude.GetProperty("lastSmokeFailedAt").ValueKind);

        var codex = agents.Single(a => a.GetProperty("agent").GetString() == "codex");
        Assert.True(codex.GetProperty("excluded").GetBoolean());
        Assert.Contains("auth", codex.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, codex.GetProperty("lastSmokePassedAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, codex.GetProperty("lastSmokeFailedAt").ValueKind);
    }

    /// <summary>
    /// Test-only WebApplicationFactory that swaps the real per-agent
    /// IAgentSmokeProbe registrations for a programmable
    /// <see cref="ControllableSmokeProbe"/> so /admin/agent/{name}/smoke
    /// returns a known result without making a live API call.
    /// </summary>
    public sealed class AvailabilityAdminApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-availability-admin-{Guid.NewGuid():N}.db");

        private readonly ControllableSmokeProbe _claudeProbe = new(AgentKind.Claude);
        private readonly ControllableSmokeProbe _codexProbe = new(AgentKind.Codex);

        public void SetProbeResult(AgentKind kind, bool pass)
        {
            if (kind == AgentKind.Claude) _claudeProbe.ShouldPass = pass;
            else if (kind == AgentKind.Codex) _codexProbe.ShouldPass = pass;
        }

        /// <summary>
        /// Clears registry state between test methods. The factory is shared
        /// across the test class via IClassFixture so cross-test bleed would
        /// otherwise mean, for example, the unknown-agent test could observe
        /// a stale "claude excluded" entry from a prior method.
        /// </summary>
        public void Reset()
        {
            var registry = Services.GetRequiredService<AgentAvailabilityRegistry>();
            registry.Reset(AgentKind.Claude);
            registry.Reset(AgentKind.Codex);
            _claudeProbe.ShouldPass = true;
            _codexProbe.ShouldPass = true;
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
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                // Replace every IAgentSmokeProbe registration so the endpoint
                // exercises a known result without hitting a real API.
                services.RemoveAll<IAgentSmokeProbe>();
                services.AddSingleton<IAgentSmokeProbe>(_claudeProbe);
                services.AddSingleton<IAgentSmokeProbe>(_codexProbe);
                // PeriodicSmokeProbeService.ProbeOneAsync returns null when
                // the credential resolver returns null, which would 404 the
                // /smoke endpoint regardless of the probe's result. Inject a
                // constant credential so the probe is actually invoked.
                services.RemoveAll<ICredentialProvider>();
                services.AddSingleton<ICredentialProvider>(new ConstantCredentialProvider(
                    new AgentCredential(
                        AgentKind.Claude,
                        new Dictionary<string, string> { ["k"] = "v" },
                        new Dictionary<string, string>())));
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

    /// <summary>
    /// Smoke probe whose result can be flipped between tests. Returns a
    /// fixed short duration so the endpoint's durationMs projection is a
    /// known value-kind in JSON.
    /// </summary>
    internal sealed class ControllableSmokeProbe : IAgentSmokeProbe
    {
        public AgentKind Kind { get; }
        public bool ShouldPass { get; set; } = true;
        public ControllableSmokeProbe(AgentKind kind) => Kind = kind;

        public Task<AgentSmokeResult> SmokeTestAsync(AgentCredential credential, CancellationToken ct)
            => Task.FromResult(ShouldPass
                ? new AgentSmokeResult(true, null, TimeSpan.FromMilliseconds(7))
                : new AgentSmokeResult(false, "auth", TimeSpan.FromMilliseconds(7)));
    }
}
