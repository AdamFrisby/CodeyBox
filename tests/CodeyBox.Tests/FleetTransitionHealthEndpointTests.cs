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
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level tests for <c>GET /fleet/transition-health</c>. Covers both
/// branches of the endpoint: the 404 fast-path when the metric is disabled in
/// config, and the 200 JSON response shape (overall score, per-stage breakdown,
/// infra-by-kind tally, window envelope). The data source is stubbed so the
/// test exercises the API surface — endpoint registration, auth gate
/// inheritance, JSON shape — without coupling to the SQLite store's row
/// shapes (those are covered by <c>SqliteTransitionHealthDataSourceTests</c>).
/// </summary>
[Collection("GlobalSerilog")]
public sealed class FleetTransitionHealthEndpointTests : IDisposable
{
    private readonly TransitionHealthApiFactory _factory;
    private readonly HttpClient _client;

    public FleetTransitionHealthEndpointTests()
    {
        _factory = new TransitionHealthApiFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task GetTransitionHealth_Disabled_Returns404()
    {
        _factory.Options = new TransitionHealthOptions
        {
            Enabled = false,
            Window = TimeSpan.FromHours(24),
        };

        var resp = await _client.GetAsync("/fleet/transition-health");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("transition-health is disabled", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetTransitionHealth_Enabled_EmptySnapshot_ReturnsHealthyResponse()
    {
        _factory.Options = new TransitionHealthOptions
        {
            Enabled = true,
            Window = TimeSpan.FromHours(24),
        };
        _factory.DataSource.Snapshot = new TransitionDataSnapshot([], [], []);

        var resp = await _client.GetAsync("/fleet/transition-health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1.0, body.GetProperty("score").GetDouble());
        Assert.Equal(0.0, body.GetProperty("infraFailureRate").GetDouble());
        Assert.Equal(0, body.GetProperty("totalTransitions").GetInt32());
        Assert.Equal(0, body.GetProperty("legitimateTransitions").GetInt32());
        Assert.Equal(0, body.GetProperty("infraFailureTransitions").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("worstStage").ValueKind);

        var window = body.GetProperty("window");
        Assert.Equal(TimeSpan.FromHours(24).TotalSeconds, window.GetProperty("durationSeconds").GetDouble());
        Assert.True(window.GetProperty("start").GetDateTimeOffset() < window.GetProperty("end").GetDateTimeOffset());
        // MaxTransitions unset on this run → serialized as null.
        Assert.Equal(JsonValueKind.Null, window.GetProperty("maxTransitions").ValueKind);

        var stages = body.GetProperty("stages").EnumerateArray().ToList();
        Assert.Equal(5, stages.Count);
        Assert.Equal(
            new[] { "Work", "Rework", "Audit", "Merge", "Terminal" },
            stages.Select(s => s.GetProperty("stage").GetString()).ToArray());
        Assert.All(stages, s =>
        {
            Assert.Equal(1.0, s.GetProperty("score").GetDouble());
            Assert.Equal(0, s.GetProperty("total").GetInt32());
            Assert.Equal(0, s.GetProperty("legitimate").GetInt32());
            Assert.Equal(0, s.GetProperty("infraFailure").GetInt32());
            Assert.Equal(JsonValueKind.Object, s.GetProperty("infraByKind").ValueKind);
            Assert.Empty(s.GetProperty("infraByKind").EnumerateObject());
        });

        Assert.Equal(JsonValueKind.Object, body.GetProperty("infraByKind").ValueKind);
        Assert.Empty(body.GetProperty("infraByKind").EnumerateObject());
    }

    [Fact]
    public async Task GetTransitionHealth_Enabled_WithFailures_ReturnsScoredBreakdown()
    {
        var now = DateTimeOffset.UtcNow;
        _factory.Options = new TransitionHealthOptions
        {
            Enabled = true,
            Window = TimeSpan.FromHours(24),
            MaxTransitions = 1000,
        };
        _factory.DataSource.Snapshot = new TransitionDataSnapshot(
            Involvements:
            [
                new TransitionInvolvementRow("wi-1", "work", null, "success", now.AddMinutes(-50)),
                new TransitionInvolvementRow("wi-2", "work", null, "success", now.AddMinutes(-45)),
                new TransitionInvolvementRow("wi-3", "work", null, "failure:agent", now.AddMinutes(-40)),
                new TransitionInvolvementRow("wi-4", "rework", 2, "success", now.AddMinutes(-30)),
            ],
            AuditReports:
            [
                new TransitionAuditReportRow(
                    "wi-5", 1, "review:quality", "Error",
                    now.AddMinutes(-20), ["review agent failed to run"]),
            ],
            TerminalFailures:
            [
                new TransitionTerminalFailureRow(
                    "wi-6", (int)WorkItemState.MergeConflictResolutionFailed, "infrastructure",
                    now.AddMinutes(-10)),
            ]);

        var resp = await _client.GetAsync("/fleet/transition-health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        // 3 legitimate (2 work success + 1 rework success), 3 infra (1 work
        // failure:agent + 1 audit auditor_failed + 1 terminal merge_conflict_resolution_failed).
        Assert.Equal(6, body.GetProperty("totalTransitions").GetInt32());
        Assert.Equal(3, body.GetProperty("legitimateTransitions").GetInt32());
        Assert.Equal(3, body.GetProperty("infraFailureTransitions").GetInt32());
        Assert.Equal(0.5, body.GetProperty("score").GetDouble());
        Assert.Equal(0.5, body.GetProperty("infraFailureRate").GetDouble());

        Assert.Equal(1000, body.GetProperty("window").GetProperty("maxTransitions").GetInt32());

        var infraByKind = body.GetProperty("infraByKind");
        Assert.Equal(1, infraByKind.GetProperty("agent").GetInt32());
        Assert.Equal(1, infraByKind.GetProperty("auditor_failed").GetInt32());
        Assert.Equal(1, infraByKind.GetProperty("merge_conflict_resolution_failed").GetInt32());

        var stages = body.GetProperty("stages")
            .EnumerateArray()
            .ToDictionary(s => s.GetProperty("stage").GetString()!, s => s);

        var work = stages["Work"];
        Assert.Equal(2, work.GetProperty("legitimate").GetInt32());
        Assert.Equal(1, work.GetProperty("infraFailure").GetInt32());
        Assert.Equal(2.0 / 3.0, work.GetProperty("score").GetDouble(), 5);
        Assert.Equal(1, work.GetProperty("infraByKind").GetProperty("agent").GetInt32());

        Assert.Equal(1, stages["Rework"].GetProperty("legitimate").GetInt32());
        Assert.Equal(0, stages["Rework"].GetProperty("infraFailure").GetInt32());

        Assert.Equal(0, stages["Audit"].GetProperty("legitimate").GetInt32());
        Assert.Equal(1, stages["Audit"].GetProperty("infraFailure").GetInt32());

        Assert.Equal(1, stages["Terminal"].GetProperty("infraFailure").GetInt32());

        // Worst stage = highest infra count, tied at 1 across Work/Audit/Terminal.
        // Aggregate orders ties by stage-name ordinal, so the tie-breaker is Audit
        // (alphabetically first among the three).
        Assert.Equal("Audit", body.GetProperty("worstStage").GetString());
    }

    [Fact]
    public async Task GetTransitionHealth_HotReloadedDisabled_NowReturns404()
    {
        // The endpoint reads through the live snapshot, so a hot-reload that
        // flips Enabled→false must immediately gate the endpoint behind 404.
        _factory.Options = new TransitionHealthOptions { Enabled = true, Window = TimeSpan.FromHours(1) };
        var ok = await _client.GetAsync("/fleet/transition-health");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // Mutate the live snapshot (same instance) — simulates a hot reload.
        _factory.OptionsSnapshot.Replace(new TransitionHealthOptions
        {
            Enabled = false,
            Window = TimeSpan.FromHours(1),
        });

        var disabled = await _client.GetAsync("/fleet/transition-health");
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
    }

    /// <summary>
    /// Test double for <see cref="ITransitionHealthDataSource"/>; returns
    /// whatever snapshot the test sets. Avoids coupling endpoint tests to the
    /// real SQLite store's row shapes (that surface is covered separately).
    /// </summary>
    private sealed class StubTransitionHealthDataSource : ITransitionHealthDataSource
    {
        public TransitionDataSnapshot Snapshot { get; set; } =
            new(Array.Empty<TransitionInvolvementRow>(),
                Array.Empty<TransitionAuditReportRow>(),
                Array.Empty<TransitionTerminalFailureRow>());

        public Task<TransitionDataSnapshot> LoadAsync(
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd,
            int maxRowsPerSource,
            CancellationToken ct = default)
            => Task.FromResult(Snapshot);
    }

    /// <summary>
    /// WebApplicationFactory that swaps in a stub <see cref="ITransitionHealthDataSource"/>
    /// and a live <see cref="TransitionHealthOptionsSnapshot"/> the test can mutate.
    /// </summary>
    private sealed class TransitionHealthApiFactory : CodeyBox.Tests.CodeyBoxWebApplicationFactory
    {
        public StubTransitionHealthDataSource DataSource { get; } = new();

        // Initial options written into the snapshot at host build; tests can
        // overwrite via the public setter before issuing the request. Setting
        // the property after host build is a no-op for the snapshot already
        // held in DI, so the snapshot is also exposed for hot-reload tests.
        public TransitionHealthOptions Options
        {
            get => OptionsSnapshot.Current;
            set => OptionsSnapshot.Replace(value);
        }

        public TransitionHealthOptionsSnapshot OptionsSnapshot { get; } =
            new(new TransitionHealthOptions { Enabled = true, Window = TimeSpan.FromHours(24) });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var tmp = Temp.Root;
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = Path.Combine(tmp, $"codeybox-th-{Guid.NewGuid():N}.db"),
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<ITransitionHealthDataSource>();
                services.AddSingleton<ITransitionHealthDataSource>(DataSource);
                services.RemoveAll<TransitionHealthOptionsSnapshot>();
                services.AddSingleton(OptionsSnapshot);
                services.RemoveAll<TransitionHealthService>();
                services.AddSingleton(new TransitionHealthService(DataSource, OptionsSnapshot));
            });
        }
    }
}
