using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for the SQLite-maintenance-vs-dispatch-escalation defect:
/// a VACUUM holds the global write gate far longer than the dispatch loop's
/// stuck-holder escalation window, which used to stop the host on every
/// routine maintenance run. Maintenance holds are announced up front with an
/// expected budget; dispatch absorbs waits inside that budget as backoff
/// without counting them toward escalation, the lease watchdog stays quiet
/// inside the budget, startup validation rejects a permitted hold that
/// exceeds the window, and genuinely stuck holders still escalate.
/// </summary>
[Collection("Background service timing")]
public sealed class SqliteMaintenanceHoldTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-maint-hold-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ── 1. A maintenance hold past the escalation window does not stop the host ──

    [Fact]
    public async Task MaintenanceHoldBeyondEscalationWindow_DoesNotStopHost()
    {
        using var inner = new SqliteWorkItemStore(_dbPath);
        var blocked = new MaintenanceGateTimeoutInjectingStore(
            inner,
            WriteGateHolderKind.Maintenance,
            withinExpectedHold: true,
            failuresBeforeSuccess: int.MaxValue);
        var log = new CapturingLogger<OrchestratorService>();
        var opts = new OrchestratorOptions
        {
            MaxConcurrentWorkers = 1,
            DispatchGateAcquisitionBackoff = TimeSpan.FromMilliseconds(20),
            MaxConsecutiveDispatchGateTimeoutsBeforeEscalation = 3,
        };
        using var svc = new OrchestratorService(
            new InMemoryTaskQueue(), blocked,
            new OrderedPipelineRunner(blocked),
            new CancellationRegistry(CancellationToken.None), opts, log);

        await svc.StartAsync(CancellationToken.None);
        try
        {
            // Ten consecutive maintenance timeouts: more than triple the
            // escalation window of three. Before the fix every third call
            // threw SqliteWriteGatePersistentlyUnavailableException, which
            // stops the host under BackgroundServiceExceptionBehavior.StopHost.
            for (var i = 0; i < 10; i++)
                Assert.Null(await svc.PickNextEligibleResilientAsync(CancellationToken.None));

            Assert.Equal(0, log.Count(LogLevel.Critical, "escalating"));
            Assert.True(
                log.Count(LogLevel.Warning, "expected SQLite maintenance hold") >= 10,
                "Expected every maintenance timeout to be absorbed as maintenance backoff.");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    // ── 2. A maintenance hold past its budget still escalates ──

    [Fact]
    public async Task OverdueMaintenanceHold_StillEscalates()
    {
        using var inner = new SqliteWorkItemStore(_dbPath);
        var blocked = new MaintenanceGateTimeoutInjectingStore(
            inner,
            WriteGateHolderKind.Maintenance,
            withinExpectedHold: false,
            failuresBeforeSuccess: int.MaxValue);
        var log = new CapturingLogger<OrchestratorService>();
        var opts = new OrchestratorOptions
        {
            MaxConcurrentWorkers = 1,
            DispatchGateAcquisitionBackoff = TimeSpan.Zero,
            MaxConsecutiveDispatchGateTimeoutsBeforeEscalation = 3,
        };
        using var svc = new OrchestratorService(
            new InMemoryTaskQueue(), blocked,
            new OrderedPipelineRunner(blocked),
            new CancellationRegistry(CancellationToken.None), opts, log);

        Assert.Null(await svc.PickNextEligibleResilientAsync(CancellationToken.None));
        Assert.Null(await svc.PickNextEligibleResilientAsync(CancellationToken.None));
        var escalation = await Assert.ThrowsAsync<SqliteWriteGatePersistentlyUnavailableException>(
            () => svc.PickNextEligibleResilientAsync(CancellationToken.None));
        Assert.Equal(3, escalation.ConsecutiveTimeouts);
        Assert.Equal(1, log.Count(LogLevel.Critical, "consecutive times"));
    }

    // ── 3. Real gate wiring: lease kind flows into the timeout ──

    [Fact]
    public async Task RealMaintenanceLease_TimeoutCarriesExpectedHold()
    {
        var factory = new SqliteDatabaseWriteGateFactory(
            static () => new SqliteWriteGateOptions
            {
                AcquisitionTimeout = TimeSpan.FromMilliseconds(50),
                MaxHoldDuration = TimeSpan.FromSeconds(30),
            },
            NullLoggerFactory.Instance);
        using var holder = factory.ForPath(_dbPath);
        using var waiter = factory.ForPath(_dbPath);

        // Acquire on a background flow: the gate's re-entrancy guard is
        // AsyncLocal-scoped, and in production the maintenance holder and the
        // dispatch waiter never share an async context.
        await Task.Run(async () => await holder.WaitForMaintenanceAsync(TimeSpan.FromMinutes(10)));
        try
        {
            var ex = await Assert.ThrowsAsync<SqliteWriteGateAcquisitionTimeoutException>(
                async () => await waiter.WaitAsync(CancellationToken.None));
            Assert.Equal(WriteGateHolderKind.Maintenance, ex.CurrentHolderKind);
            Assert.True(ex.IsExpectedMaintenanceHold);
            Assert.Contains(nameof(SqliteMaintenanceHoldTests), ex.CurrentHolder);
        }
        finally
        {
            holder.Release();
        }
    }

    [Fact]
    public async Task RealMaintenanceLease_PastBudget_NoLongerExpectedHold()
    {
        var factory = new SqliteDatabaseWriteGateFactory(
            static () => new SqliteWriteGateOptions
            {
                AcquisitionTimeout = TimeSpan.FromMilliseconds(50),
                MaxHoldDuration = TimeSpan.FromSeconds(30),
            },
            NullLoggerFactory.Instance);
        using var holder = factory.ForPath(_dbPath);
        using var waiter = factory.ForPath(_dbPath);

        // Acquire on a background flow (see above): production holder and
        // waiter never share an async context.
        await Task.Run(async () => await holder.WaitForMaintenanceAsync(TimeSpan.FromMilliseconds(100)));
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            var ex = await Assert.ThrowsAsync<SqliteWriteGateAcquisitionTimeoutException>(
                async () => await waiter.WaitAsync(CancellationToken.None));
            Assert.Equal(WriteGateHolderKind.Maintenance, ex.CurrentHolderKind);
            Assert.False(ex.IsExpectedMaintenanceHold);
        }
        finally
        {
            holder.Release();
        }
    }

    // ── 4. Configuration validation ──

    [Fact]
    public void ValidateMaintenanceHold_RejectsVacuumTimeoutExceedingWindow()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OrchestratorOptionsFactory.ValidateMaintenanceHoldAgainstDispatchWindow(
                new SqliteMaintenanceOptions
                {
                    Enabled = true,
                    VacuumTimeout = TimeSpan.FromMinutes(30),
                },
                new SqliteWriteGateOptions
                {
                    AcquisitionTimeout = TimeSpan.FromSeconds(5),
                },
                new WorkerPoolOptions
                {
                    MaxConsecutiveDispatchGateTimeoutsBeforeEscalation = 10,
                }));

        Assert.Contains("CodeyBox:SqliteMaintenance:VacuumTimeout", ex.Message);
        Assert.Contains("CodeyBox:SqliteWriteGate:AcquisitionTimeout", ex.Message);
        Assert.Contains(
            "CodeyBox:WorkerPool:MaxConsecutiveDispatchGateTimeoutsBeforeEscalation",
            ex.Message);
    }

    [Theory]
    // Exactly at the window: permitted (only exceeding fails).
    [InlineData(50, 5, 10, true)]
    // Inside the window: permitted.
    [InlineData(45, 5, 10, true)]
    // One second over the window: rejected.
    [InlineData(51, 5, 10, false)]
    public void ValidateMaintenanceHold_EnforcesWindowBoundary(
        int vacuumSeconds,
        int acquisitionSeconds,
        int maxConsecutive,
        bool valid)
    {
        var act = () => OrchestratorOptionsFactory.ValidateMaintenanceHoldAgainstDispatchWindow(
            new SqliteMaintenanceOptions
            {
                Enabled = true,
                VacuumTimeout = TimeSpan.FromSeconds(vacuumSeconds),
            },
            new SqliteWriteGateOptions
            {
                AcquisitionTimeout = TimeSpan.FromSeconds(acquisitionSeconds),
            },
            new WorkerPoolOptions
            {
                MaxConsecutiveDispatchGateTimeoutsBeforeEscalation = maxConsecutive,
            });

        if (valid)
            act();
        else
            Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void ValidateMaintenanceHold_DisabledMaintenanceIsExempt()
    {
        OrchestratorOptionsFactory.ValidateMaintenanceHoldAgainstDispatchWindow(
            new SqliteMaintenanceOptions
            {
                Enabled = false,
                VacuumTimeout = TimeSpan.FromHours(2),
            },
            new SqliteWriteGateOptions
            {
                AcquisitionTimeout = TimeSpan.FromSeconds(5),
            },
            new WorkerPoolOptions
            {
                MaxConsecutiveDispatchGateTimeoutsBeforeEscalation = 10,
            });
    }

    [Fact]
    public void ValidateMaintenanceHold_StockDefaultsPass()
    {
        OrchestratorOptionsFactory.ValidateMaintenanceHoldAgainstDispatchWindow(
            new SqliteMaintenanceOptions(),
            new SqliteWriteGateOptions(),
            new WorkerPoolOptions());
    }

    [Fact]
    public void OptionsValidator_RejectsVacuumTimeoutExceedingWindow_NamingAllPaths()
    {
        var options = new CodeyBoxOptions
        {
            AuditLog = new AuditLogOptions
            {
                RetainedDays = 30,
                Path = Path.Combine("logs", "codeybox-.json"),
                AuditPath = Path.Combine("logs", "audit-.json"),
            },
        };
        options.SqliteMaintenance.VacuumTimeout = TimeSpan.FromMinutes(30);

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed, "Expected startup validation to reject the oversized maintenance hold.");
        Assert.Contains("CodeyBox:SqliteMaintenance:VacuumTimeout", result.FailureMessage);
        Assert.Contains("CodeyBox:SqliteWriteGate:AcquisitionTimeout", result.FailureMessage);
        Assert.Contains(
            "CodeyBox:WorkerPool:MaxConsecutiveDispatchGateTimeoutsBeforeEscalation",
            result.FailureMessage);
    }

    [Fact]
    public void OptionsValidator_AcceptsStockMaintenanceAgainstDispatchWindow()
    {
        var options = new CodeyBoxOptions
        {
            AuditLog = new AuditLogOptions
            {
                RetainedDays = 30,
                Path = Path.Combine("logs", "codeybox-.json"),
                AuditPath = Path.Combine("logs", "audit-.json"),
            },
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.False(result.Failed, result.FailureMessage);
    }

    // ── 5. Lease watchdog stays quiet inside the budget ──

    [Fact]
    public async Task MaintenanceHoldWithinBudget_DoesNotEmitWatchdogError()
    {
        var time = new ControllableTimeProvider();
        var logs = new LevelRecordingLoggerFactory();
        var gateOptions = new SqliteWriteGateOptions
        {
            AcquisitionTimeout = TimeSpan.FromSeconds(5),
            MaxHoldDuration = TimeSpan.FromSeconds(5),
        };
        var factory = new SqliteDatabaseWriteGateFactory(() => gateOptions, logs, time);
        using var holder = factory.ForPath(_dbPath);

        await holder.WaitForMaintenanceAsync(TimeSpan.FromMinutes(5));
        try
        {
            // Past MaxHoldDuration (5s) but inside the announced 5-minute
            // maintenance budget: the old watchdog armed at MaxHoldDuration
            // and logged an Error here on every successful VACUUM.
            time.Advance(TimeSpan.FromSeconds(30));
            holder.Release();

            Assert.DoesNotContain(
                logs.Entries,
                e => e.Level == LogLevel.Error
                    && e.Message.Contains(
                        "exceeded the configured maximum hold duration",
                        StringComparison.Ordinal));
        }
        finally
        {
            try { holder.Release(); } catch (SynchronizationLockException) { }
        }
    }

    [Fact]
    public async Task MaintenanceHoldPastBudget_EmitsWatchdogError()
    {
        var time = new ControllableTimeProvider();
        var logs = new LevelRecordingLoggerFactory();
        var gateOptions = new SqliteWriteGateOptions
        {
            AcquisitionTimeout = TimeSpan.FromSeconds(5),
            MaxHoldDuration = TimeSpan.FromSeconds(5),
        };
        var factory = new SqliteDatabaseWriteGateFactory(() => gateOptions, logs, time);
        using var holder = factory.ForPath(_dbPath);

        await holder.WaitForMaintenanceAsync(TimeSpan.FromMinutes(5));
        try
        {
            time.Advance(TimeSpan.FromMinutes(6));
            holder.Release();

            var errors = logs.Entries.Where(
                e => e.Level == LogLevel.Error
                    && e.Message.Contains(
                        "exceeded the configured maximum hold duration",
                        StringComparison.Ordinal)).ToList();
            Assert.Single(errors);
            Assert.Contains(
                nameof(MaintenanceHoldPastBudget_EmitsWatchdogError),
                errors[0].Message,
                StringComparison.Ordinal);
        }
        finally
        {
            try { holder.Release(); } catch (SynchronizationLockException) { }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public int Count(LogLevel level, string substring) =>
            _entries.Count(e => e.Level == level && e.Message.Contains(substring, StringComparison.Ordinal));

        IDisposable ILogger.BeginScope<TState>(TState state) => null!;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception)));
    }

    private sealed class LevelRecordingLoggerFactory : ILoggerFactory, ILogger
    {
        private readonly object _sync = new();
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_sync)
                    return _entries.ToArray();
            }
        }

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider) { }

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null!;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_sync)
                _entries.Add((logLevel, formatter(state, exception)));
        }

        public void Dispose() { }
    }

    /// <summary>
    /// <see cref="IWorkItemStore"/> decorator over a real
    /// <see cref="SqliteWorkItemStore"/> that throws a genuine
    /// <see cref="SqliteWriteGateAcquisitionTimeoutException"/> with the
    /// configured holder classification from the dispatch-list query a fixed
    /// number of times before delegating.
    /// </summary>
    private sealed class MaintenanceGateTimeoutInjectingStore(
        IWorkItemStore inner,
        WriteGateHolderKind holderKind,
        bool withinExpectedHold,
        int failuresBeforeSuccess)
        : IWorkItemStore
    {
        private int _remaining = failuresBeforeSuccess;

        public async IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(
            IReadOnlySet<WorkItemId> skipIds,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (Interlocked.Decrement(ref _remaining) >= 0)
                throw new SqliteWriteGateAcquisitionTimeoutException(
                    "dispatch pickup (test)",
                    "SqliteDatabaseMaintenanceService.RunMaintenanceOnceAsync (test)",
                    TimeSpan.FromMilliseconds(1),
                    holderKind,
                    withinExpectedHold);
            await foreach (var item in inner.ListDispatchEligibleByPriorityAsync(skipIds, ct))
                yield return item;
        }

        public Task CreateAsync(WorkItem item, CancellationToken ct = default) => inner.CreateAsync(item, ct);
        public Task UpdateAsync(WorkItem item, CancellationToken ct = default) => inner.UpdateAsync(item, ct);
        public Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default) =>
            inner.TryUpdateIfStateAsync(item, onlyIfState, ct);
        public Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            inner.UpdatePriorityAsync(id, priority, updatedAt, ct);
        public Task<DependsOnUpdateResult> UpdateDependsOnAsync(WorkItemId id, IReadOnlyList<WorkItemId> dependsOn, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            inner.UpdateDependsOnAsync(id, dependsOn, updatedAt, ct);
        public Task<AuditBudgetUpdateResult> UpdateAuditBudgetAsync(WorkItemId id, int? auditMaxIterations, string? auditComplexity, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            inner.UpdateAuditBudgetAsync(id, auditMaxIterations, auditComplexity, updatedAt, ct);
        public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) => inner.ListByStateAsync(state, ct);
        public Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default) => inner.CountByStateAsync(state, ct);
        public Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) => inner.ReorderAsync(orderedIds, ct);
        public Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) =>
            inner.CountStartedInWindowAsync(projectId, since, ct);
        public Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) => inner.CountInFlightAsync(projectId, ct);
        public Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) =>
            inner.GetByExternalIdAsync(projectId, externalId, ct);
        public Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default) =>
            inner.GetByNamespacedExternalIdAsync(projectId, @namespace, externalId, ct);
        public Task<WorkItem?> ReplaceExternalIdsAsync(WorkItemId id, IReadOnlyDictionary<string, string> externalIds, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            inner.ReplaceExternalIdsAsync(id, externalIds, updatedAt, ct);
        public Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default) =>
            inner.GetFleetStateCountsAsync(ct);
        public Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default) =>
            inner.GetFleetRecentOutcomesAsync(perProject, ct);
        public Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default) => inner.GetFleetPauseStatesAsync(ct);
        public IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) =>
            inner.ListByReplaySourceAsync(sourceId, ct);
        public IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default) => inner.ListSuspendedAsync(ct);
        public Task<IReadOnlySet<string>> GetActiveBaselineImageRefsAsync(CancellationToken ct = default) => inner.GetActiveBaselineImageRefsAsync(ct);
        public Task<IReadOnlyList<(WorkItemId Id, string Title, WorkItemState State)>> ListWorkItemsForBaselineAsync(string baselineImageRef, CancellationToken ct = default) =>
            inner.ListWorkItemsForBaselineAsync(baselineImageRef, ct);
        public Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) => inner.OrphanReplaysAsync(sourceId, ct);
        public IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) =>
            inner.ListByReleaseAsync(releaseId, ct);
        public Task<PromptReplaceResult> TryReplacePromptAsync(WorkItemId id, string newPrompt, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            inner.TryReplacePromptAsync(id, newPrompt, updatedAt, ct);
        public Task RecordIterationDispatchAsync(WorkItemId workItemId, int iteration, int promptRevisionAtDispatch, DateTimeOffset dispatchedAt, CancellationToken ct = default) =>
            inner.RecordIterationDispatchAsync(workItemId, iteration, promptRevisionAtDispatch, dispatchedAt, ct);
        public Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(WorkItemId workItemId, CancellationToken ct = default) =>
            inner.GetIterationsAsync(workItemId, ct);
    }
}
