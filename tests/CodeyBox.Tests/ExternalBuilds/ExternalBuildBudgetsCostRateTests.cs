using CodeyBox.Core.ExternalBuilds;
using CodeyBox.Orchestrator.ExternalBuilds;

namespace CodeyBox.Tests.ExternalBuilds;

/// <summary>
/// Deliverable 7: reserved-versus-actual cost, license-seat capacity, and
/// start rate limits, enforced through the real service/store paths.
/// </summary>
public sealed class ExternalBuildBudgetsCostRateTests
{
    [Fact]
    public async Task ReservedCost_Recorded_ActualSettled_ReleasedOnTerminal()
    {
        var provider = new FakeSnapshotBuildProvider { PollsToTerminal = 1, TerminalActualCost = 2.5m };
        var (service, store, _, _) = ExternalBuildTestKit.BuildService(provider);
        var started = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        Assert.Equal(1m, started.ReservedCost);
        Assert.Null(started.ActualCost);

        var terminal = await service.ReconcileAsync(started.Id);
        Assert.Equal(ExternalBuildState.Succeeded, terminal.State);
        Assert.Equal(2.5m, terminal.ActualCost);

        var stored = await store.GetAsync(started.Id);
        Assert.Equal(2.5m, stored!.ActualCost);

        // Reservation released exactly once: the project can spend again.
        var active = await store.ListActiveAsync("proj");
        Assert.Empty(active);
        var again = await service.StartAsync(
            ExternalBuildTestKit.Request(attempt: "w2"), new ExternalBuildSubmitInput());
        Assert.Equal(1m, again.ReservedCost);
    }

    [Fact]
    public async Task ActualCost_SettlesAtReservation_WhenProviderReportsNone()
    {
        var provider = new FakeSnapshotBuildProvider { PollsToTerminal = 1 };
        var (service, _, _, _) = ExternalBuildTestKit.BuildService(provider);
        var started = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        var terminal = await service.ReconcileAsync(started.Id);
        Assert.Equal(ExternalBuildState.Succeeded, terminal.State);
        Assert.Equal(terminal.ReservedCost, terminal.ActualCost);
    }

    [Fact]
    public async Task ReservedCostBudget_Enforced_AndReleasedOnCompletion()
    {
        var provider = new FakeSnapshotBuildProvider { PollsToTerminal = 1 };
        var (service, _, _, _) = ExternalBuildTestKit.BuildService(provider, o =>
        {
            o.DefaultReservedCost = 10m;
            o.MaxReservedCostPerProject = 10m;
        });
        var first = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        var over = await Assert.ThrowsAsync<ExternalBuildBudgetExceededException>(() =>
            service.StartAsync(ExternalBuildTestKit.Request(attempt: "w2"), new ExternalBuildSubmitInput()));
        Assert.Contains("reserved-cost", over.Message);

        await service.ReconcileAsync(first.Id);
        var second = await service.StartAsync(
            ExternalBuildTestKit.Request(attempt: "w2"), new ExternalBuildSubmitInput());
        Assert.Equal(ExternalBuildState.Queued, second.State);
    }

    [Fact]
    public async Task LicenseSeats_Enforced_DistinctFromConcurrency()
    {
        var provider = new FakeSnapshotBuildProvider();
        var (service, _, _, _) = ExternalBuildTestKit.BuildService(provider, o =>
        {
            o.MaxConcurrentPerProject = 32;
            o.MaxConcurrentPerProvider = 64;
            o.MaxQueuedPerProject = 500;
            o.MaxLicenseSeatsPerProvider = 1;
            o.MaxLicenseSeatsPerProject = 1024;
        });
        await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        var seats = await Assert.ThrowsAsync<ExternalBuildBudgetExceededException>(() =>
            service.StartAsync(ExternalBuildTestKit.Request(attempt: "w2"), new ExternalBuildSubmitInput()));
        Assert.Contains("license-seat", seats.Message);
    }

    [Fact]
    public async Task LicenseSeatsPerProject_Enforced()
    {
        var provider = new FakeSnapshotBuildProvider();
        var (service, _, _, _) = ExternalBuildTestKit.BuildService(provider, o =>
        {
            o.MaxQueuedPerProject = 500;
            o.MaxLicenseSeatsPerProvider = 1024;
            o.MaxLicenseSeatsPerProject = 1;
        });
        await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        var seats = await Assert.ThrowsAsync<ExternalBuildBudgetExceededException>(() =>
            service.StartAsync(ExternalBuildTestKit.Request(attempt: "w2"), new ExternalBuildSubmitInput()));
        Assert.Contains("license-seat", seats.Message);
    }

    [Fact]
    public async Task Cancel_ReleasesSeatAndCostReservation()
    {
        var provider = new FakeSnapshotBuildProvider();
        var (service, store, _, _) = ExternalBuildTestKit.BuildService(provider, o =>
        {
            o.DefaultReservedCost = 10m;
            o.MaxReservedCostPerProject = 10m;
            o.MaxLicenseSeatsPerProvider = 1;
        });
        var first = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        var cancelled = await service.CancelAsync(first.Id, ExternalBuildTerminalCause.UserCancelled);
        Assert.Equal(ExternalBuildState.Cancelled, cancelled.State);
        Assert.Equal(cancelled.ReservedCost, cancelled.ActualCost);

        var second = await service.StartAsync(
            ExternalBuildTestKit.Request(attempt: "w2"), new ExternalBuildSubmitInput());
        Assert.Equal(ExternalBuildState.Queued, second.State);
        Assert.Equal(1, await store.CountActiveForProviderAsync("fake-snapshot"));
    }

    [Fact]
    public async Task StartRateLimit_PerProvider_RejectsThenRecovers()
    {
        var provider = new FakeSnapshotBuildProvider();
        var (service, _, clock, _) = ExternalBuildTestKit.BuildService(provider, o =>
        {
            o.MaxQueuedPerProject = 500;
            o.MaxLicenseSeatsPerProvider = 1024;
            o.MaxLicenseSeatsPerProject = 1024;
            o.MaxReservedCostPerProject = 1_000_000m;
            o.MaxStartsPerMinutePerProvider = 1;
            o.MaxStartsPerMinutePerProject = 1000;
        });
        await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        var limited = await Assert.ThrowsAsync<ExternalBuildRateLimitedException>(() =>
            service.StartAsync(ExternalBuildTestKit.Request(attempt: "w2"), new ExternalBuildSubmitInput()));
        Assert.True(limited.RetryAfter > TimeSpan.Zero);
        Assert.IsAssignableFrom<ExternalBuildBudgetExceededException>(limited);

        clock.Advance(TimeSpan.FromSeconds(61));
        var recovered = await service.StartAsync(
            ExternalBuildTestKit.Request(attempt: "w3"), new ExternalBuildSubmitInput());
        Assert.Equal(ExternalBuildState.Queued, recovered.State);
    }

    [Fact]
    public async Task StartRateLimit_PerProject_RejectsBurst()
    {
        var provider = new FakeSnapshotBuildProvider();
        var (service, _, _, _) = ExternalBuildTestKit.BuildService(provider, o =>
        {
            o.MaxQueuedPerProject = 500;
            o.MaxLicenseSeatsPerProvider = 1024;
            o.MaxLicenseSeatsPerProject = 1024;
            o.MaxReservedCostPerProject = 1_000_000m;
            o.MaxStartsPerMinutePerProvider = 1000;
            o.MaxStartsPerMinutePerProject = 1;
        });
        await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        await Assert.ThrowsAsync<ExternalBuildRateLimitedException>(() =>
            service.StartAsync(ExternalBuildTestKit.Request(attempt: "w2"), new ExternalBuildSubmitInput()));
    }

    [Fact]
    public async Task ProviderRateLimitedSubmit_SurfacesTypedError_IntentStaysReconciliable()
    {
        var provider = new FakeSnapshotBuildProvider
        {
            FailNextSubmitRateLimited = true,
            RateLimitedRetryAfter = TimeSpan.FromSeconds(45),
            PollsToTerminal = 1,
        };
        var (service, store, _, _) = ExternalBuildTestKit.BuildService(provider);
        var limited = await Assert.ThrowsAsync<ExternalBuildRateLimitedException>(() =>
            service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput()));
        Assert.Equal(TimeSpan.FromSeconds(45), limited.RetryAfter);

        // Intent is durable: exactly one uncertain record, no provider run yet.
        var active = await store.ListActiveAsync("proj");
        var uncertain = Assert.Single(active);
        Assert.Equal(ExternalBuildState.SubmitUncertain, uncertain.State);
        Assert.Null(uncertain.ProviderRunId);
        Assert.Contains("rate-limited", uncertain.FailureDetail);

        // Reconciliation retries by the same request identity: one paid run.
        var queued = await service.ReconcileAsync(uncertain.Id);
        Assert.Equal(ExternalBuildState.Queued, queued.State);
        Assert.NotNull(queued.ProviderRunId);
        var terminal = await service.ReconcileAsync(uncertain.Id);
        Assert.Equal(ExternalBuildState.Succeeded, terminal.State);
    }

    [Fact]
    public async Task ProviderRateLimitedPoll_IsRetriable_BackoffSurfaced()
    {
        var provider = new FakeSnapshotBuildProvider
        {
            FailNextPollRateLimited = true,
            PollsToTerminal = 1,
        };
        var (service, _, _, _) = ExternalBuildTestKit.BuildService(provider);
        var started = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        var backed = await service.ReconcileAsync(started.Id);
        Assert.False(ExternalBuildLifecycle.IsTerminal(backed.State));
        Assert.Contains("rate-limited", backed.FailureDetail);

        var terminal = await service.ReconcileAsync(started.Id);
        Assert.Equal(ExternalBuildState.Succeeded, terminal.State);
    }

    [Fact]
    public async Task SqliteStore_CostRoundTrip_AndLegacyRowsDefault()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xb-cost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "state.db");
            var clock = new ControllableClock(DateTimeOffset.UtcNow);
            var opts = ExternalBuildTestKit.Options();
            opts.DefaultReservedCost = 7m;
            var provider = new FakeSnapshotBuildProvider { PollsToTerminal = 1, TerminalActualCost = 3.25m };
            string buildId;
            using (var store = new SqliteExternalBuildStore(path))
            {
                var service = new ExternalBuildService(store, [provider], () => opts, clock);
                var started = await service.StartAsync(
                    ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
                Assert.Equal(7m, started.ReservedCost);
                buildId = started.Id;
                await service.ReconcileAsync(buildId);
            }
            using (var reopened = new SqliteExternalBuildStore(path))
            {
                var stored = await reopened.GetAsync(buildId);
                Assert.NotNull(stored);
                Assert.Equal(7m, stored!.ReservedCost);
                Assert.Equal(3.25m, stored.ActualCost);

                // A row written by pre-cost code (columns defaulted) loads
                // with zero reservation and no actual cost.
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                var now = DateTimeOffset.UtcNow.ToString("O");
                cmd.CommandText = """
                    INSERT INTO external_builds (id, project_id, work_item_id, phase, state,
                        target_json, source_json, config_digest, created_at, updated_at)
                    VALUES ('xb-legacy', 'proj', 'w9', 'work', 'Queued',
                        '{"ProviderId":"fake-snapshot","TargetId":"t","Configuration":"","Toolchain":"","Platform":"","CacheClass":""}',
                        '{"SourceDigestSha256":"s","BaseDigestSha256":"b","ByteSize":0,"FileCount":0}',
                        'cfg', $now, $now);
                    """;
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
                var legacy = await reopened.GetAsync("xb-legacy");
                Assert.NotNull(legacy);
                Assert.Equal(0m, legacy!.ReservedCost);
                Assert.Null(legacy.ActualCost);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void OptionsValidation_RejectsBadBudgetKnobs()
    {
        var good = ExternalBuildTestKit.Options();
        Assert.True(ExternalBuildOptions.IsValid(good));

        var badSeats = ExternalBuildTestKit.Options();
        badSeats.MaxLicenseSeatsPerProvider = 0;
        Assert.False(ExternalBuildOptions.IsValid(badSeats));

        var badCost = ExternalBuildTestKit.Options();
        badCost.DefaultReservedCost = -1m;
        Assert.False(ExternalBuildOptions.IsValid(badCost));

        var badRate = ExternalBuildTestKit.Options();
        badRate.MaxStartsPerMinutePerProject = 0;
        Assert.False(ExternalBuildOptions.IsValid(badRate));
    }
}
