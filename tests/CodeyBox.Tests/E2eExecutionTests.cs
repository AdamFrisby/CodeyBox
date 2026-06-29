using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for the E2E replay execution infrastructure:
/// - deterministic replay (pass / fail / readiness probe failure / per-run timeout)
/// - SQLite run store (queue claim semantics, batch listing)
/// - LocalE2eExecutionPool concurrency cap (clone-per-test, no leak past dispose)
/// - dispatcher runs many replays in parallel
/// - the pool is wired to a sandbox provider; the coding-fleet WorkerPool is NEVER
///   touched (architectural separation enforced by a fake provider that asserts no
///   external collaborators reach into it)
/// </summary>
[Collection("Background service timing")]
public sealed class E2eExecutionTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteWorkItemStore _itemStore;
    private readonly SqliteTestCaseStore _testCases;
    private readonly SqliteE2eRunStore _runs;

    public E2eExecutionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-e2e-{Guid.NewGuid():N}.db");
        _itemStore = new SqliteWorkItemStore(_dbPath);
        _testCases = new SqliteTestCaseStore(_dbPath);
        _runs = new SqliteE2eRunStore(_dbPath);
    }

    public void Dispose()
    {
        _runs.Dispose();
        _testCases.Dispose();
        _itemStore.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    // --------------------------------------------------------------------
    // Deterministic replay engine
    // --------------------------------------------------------------------

    [Fact]
    public async Task Replay_passes_when_every_step_and_assertion_succeeds()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["codeybox-e2e-replay"] = _ => new SandboxExecResult(0, "driver ok\n", string.Empty);

        var artifact = new E2eReplayArtifact
        {
            Name = "happy",
            Steps =
            [
                new E2eReplayStep { Action = "navigate", Target = "http://app.local/" },
                new E2eReplayStep { Action = "click", Selector = "#login" },
            ],
            Assertions =
            [
                new E2eReplayAssertion
                {
                    Kind = "selectorVisible",
                    Selector = "#account",
                    Description = "account panel should be visible",
                },
            ],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.True(result.Passed);
        Assert.Null(result.FailedStepIndex);
        Assert.Equal(2, result.StepResults.Count);
        Assert.All(result.StepResults, step => Assert.True(step.Passed));
        Assert.Single(result.AssertionResults);
        Assert.True(result.AssertionResults[0].Passed);
        var exec = Assert.Single(sandbox.ExecRequests);
        Assert.Equal(["codeybox-e2e-replay", "--artifact-json-stdin"], exec.Argv);
        Assert.Equal(16 * 1024, exec.MaxStdoutBytes);
        Assert.Equal(16 * 1024, exec.MaxStderrBytes);
    }

    [Fact]
    public async Task Replay_fails_when_step_exits_nonzero()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["codeybox-e2e-replay"] = _ => new SandboxExecResult(7, string.Empty, "button missing\n");

        var artifact = new E2eReplayArtifact
        {
            Steps = [new E2eReplayStep { Action = "click", Selector = "#missing" }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal(-1, result.FailedStepIndex);
        Assert.Equal("ReplayDriverFailed", result.FailureKind);
        Assert.Equal(7, result.StepResults[0].ExitCode);
    }

    [Fact]
    public async Task Replay_rejects_legacy_argv_without_executing_artifact_command()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["sh"] = _ => new SandboxExecResult(0, "should not run\n", string.Empty);

        var artifact = new E2eReplayArtifact
        {
            Steps = [new E2eReplayStep { Argv = ["sh", "-c", "touch /tmp/pwned"] }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("UnsupportedLegacyArgv", result.FailureKind);
        Assert.Empty(sandbox.ExecLog);
    }

    [Fact]
    public async Task Replay_reports_readiness_failure_distinctly_from_assertion_failure()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["curl"] = _ => new SandboxExecResult(7, string.Empty, "could not connect\n");
        var artifact = new E2eReplayArtifact
        {
            Readiness = new E2eReadinessProbe { Url = "http://app/healthz", MaxAttempts = 2, DelayMs = 0 },
            Steps = [new E2eReplayStep { Action = "navigate", Target = "http://app/items" }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("ReadinessProbe", result.FailureKind);
        Assert.Empty(result.StepResults);
    }

    [Fact]
    public async Task Replay_cancellation_propagates()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["codeybox-e2e-replay"] = ct =>
        {
            ct.WaitHandle.WaitOne();
            ct.ThrowIfCancellationRequested();
            return new SandboxExecResult(0, string.Empty, string.Empty);
        };
        var artifact = new E2eReplayArtifact { Steps = [new E2eReplayStep { Action = "click", Selector = "#slow" }] };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runtime.ExecuteAsync(artifact, sandbox, cts.Token));
    }

    // --------------------------------------------------------------------
    // SQLite run store
    // --------------------------------------------------------------------

    [Fact]
    public async Task RunStore_round_trips_and_indexes_queue_for_dispatch()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());

        var run = new E2eRun
        {
            Id = Guid.NewGuid().ToString("N"),
            TestCaseId = tcId,
            Status = E2eRunStatus.Queued,
        };
        await _runs.CreateAsync(run);

        var fetched = await _runs.GetAsync(run.Id);
        Assert.NotNull(fetched);
        Assert.Equal(E2eRunStatus.Queued, fetched.Status);

        var claimed = await _runs.ClaimNextQueuedAsync("sandbox-A");
        Assert.NotNull(claimed);
        Assert.Equal(run.Id, claimed.Id);
        Assert.Equal(E2eRunStatus.Running, claimed.Status);
        Assert.Equal("sandbox-A", claimed.SandboxId);
        Assert.NotNull(claimed.StartedAt);

        // No second queued row → second claim returns null.
        var noClaim = await _runs.ClaimNextQueuedAsync("sandbox-B");
        Assert.Null(noClaim);

        await _runs.UpdateStatusAsync(run.Id, E2eRunStatus.Passed, null, DateTimeOffset.UtcNow, "result-json");
        var finished = await _runs.GetAsync(run.Id);
        Assert.NotNull(finished);
        Assert.Equal(E2eRunStatus.Passed, finished.Status);
        Assert.Equal("result-json", finished.Result);
    }

    [Fact]
    public async Task RunStore_terminal_update_does_not_overwrite_canceled_running_run()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var run = new E2eRun { Id = Guid.NewGuid().ToString("N"), TestCaseId = tcId, Status = E2eRunStatus.Queued };
        await _runs.CreateAsync(run);
        var claimed = await _runs.ClaimNextQueuedAsync("sandbox-A");
        Assert.NotNull(claimed);

        Assert.True(await _runs.CancelAsync(run.Id));
        Assert.False(await _runs.UpdateStatusAsync(run.Id, E2eRunStatus.Passed, null, DateTimeOffset.UtcNow, "late-pass"));

        var fetched = await _runs.GetAsync(run.Id);
        Assert.NotNull(fetched);
        Assert.Equal(E2eRunStatus.Canceled, fetched.Status);
        Assert.Null(fetched.Result);
    }

    [Fact]
    public async Task RunStore_groups_runs_by_batch_for_aggregate_reporting()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var batch = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 3; i++)
        {
            await _runs.CreateAsync(new E2eRun
            {
                Id = Guid.NewGuid().ToString("N"),
                TestCaseId = tcId,
                Status = E2eRunStatus.Queued,
                BatchId = batch,
            });
        }
        // One unrelated run that must NOT show up in the batch list.
        await _runs.CreateAsync(new E2eRun
        {
            Id = Guid.NewGuid().ToString("N"),
            TestCaseId = tcId,
            Status = E2eRunStatus.Queued,
            BatchId = "other",
        });

        var listed = new List<E2eRun>();
        await foreach (var r in _runs.ListByBatchAsync(batch)) listed.Add(r);
        Assert.Equal(3, listed.Count);
        Assert.All(listed, r => Assert.Equal(batch, r.BatchId));
    }

    [Fact]
    public async Task RunStore_cancel_is_a_no_op_on_terminal_runs()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var run = new E2eRun { Id = Guid.NewGuid().ToString("N"), TestCaseId = tcId, Status = E2eRunStatus.Queued };
        await _runs.CreateAsync(run);
        Assert.True(await _runs.CancelAsync(run.Id));

        // Re-cancel: already terminal, returns false.
        Assert.False(await _runs.CancelAsync(run.Id));
        var fetched = await _runs.GetAsync(run.Id);
        Assert.NotNull(fetched);
        Assert.Equal(E2eRunStatus.Canceled, fetched.Status);
    }

    // --------------------------------------------------------------------
    // Pool concurrency / clone-per-test / isolation
    // --------------------------------------------------------------------

    [Fact]
    public async Task LocalPool_caps_concurrent_leases_at_MaxConcurrent()
    {
        var provider = new CountingSandboxProvider();
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { MaxConcurrent = 2 });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);

        var slot1 = await pool.LeaseAsync();
        var slot2 = await pool.LeaseAsync();
        Assert.Equal(2, pool.InFlight);

        var thirdTask = pool.LeaseAsync(); // should block until a slot is released
        Assert.False(thirdTask.IsCompleted);

        await slot1.DisposeAsync();
        var slot3 = await thirdTask;
        Assert.Equal(2, pool.InFlight);

        await slot2.DisposeAsync();
        await slot3.DisposeAsync();
        Assert.Equal(0, pool.InFlight);
        // Provider was hit once per lease — clone-per-test, NOT slot reuse.
        Assert.Equal(3, provider.CreateCount);
        // Every leased sandbox was disposed exactly once.
        Assert.True(provider.AllSandboxesDisposed);
    }

    [Fact]
    public async Task LocalPool_builds_spec_from_e2e_options_and_falls_back_to_global_image()
    {
        var provider = new CountingSandboxProvider();
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            MaxConcurrent = 1,
            NetworkProfile = "e2e-net",
            BaselineImageRef = "baseline-e2e",
        });
        var pool = new LocalE2eExecutionPool(
            provider,
            monitor,
            NullLogger<LocalE2eExecutionPool>.Instance,
            fallbackImageReference: () => "global-image");

        await using var slot = await pool.LeaseAsync();

        var spec = Assert.Single(provider.Specs);
        Assert.Equal("global-image", spec.ImageReference);
        Assert.Equal("baseline-e2e", spec.BaselineImageRef);
        Assert.Equal("e2e-net", spec.Network.ProfileName);
    }

    [Fact]
    public async Task LocalPool_releases_gate_when_provider_throws_during_lease()
    {
        var provider = new CountingSandboxProvider { ThrowOnCreate = true };
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { MaxConcurrent = 1 });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.LeaseAsync());
        Assert.Equal(0, pool.InFlight);

        // Recover — the next lease must NOT block on the orphaned slot.
        provider.ThrowOnCreate = false;
        var slot = await pool.LeaseAsync();
        await slot.DisposeAsync();
    }

    [Fact]
    public async Task LocalPool_does_not_reference_the_coding_fleet_WorkerPool()
    {
        // ARCHITECTURAL CONTRACT — the brief requires E2E load NEVER to compete with the
        // coding-worker fleet for sandbox slots. The pool's constructor takes only an
        // ISandboxProvider + IOptionsMonitor + ILogger. Reflection asserts there is no
        // hidden static / property dependency on WorkerPool or its options. If a future
        // refactor reintroduces a dependency on the coding fleet, this test fails and the
        // brief's hard rule is re-surfaced before the change lands.
        var deps = typeof(LocalE2eExecutionPool).GetConstructors().SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType).ToList();
        Assert.DoesNotContain(deps, t => t.Name == "WorkerPool");
        Assert.DoesNotContain(deps, t => t.Name == "WorkerPoolOptions");
        Assert.DoesNotContain(deps, t => t.Name == "IWorkerPoolOccupancy");

        // Same contract on the dispatcher.
        var dispDeps = typeof(E2eRunDispatcher).GetConstructors().SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType).ToList();
        Assert.DoesNotContain(dispDeps, t => t.Name == "WorkerPool");
        Assert.DoesNotContain(dispDeps, t => t.Name == "IWorkerPoolOccupancy");

        await Task.CompletedTask;
    }

    // --------------------------------------------------------------------
    // Dispatcher / parallelism end-to-end
    // --------------------------------------------------------------------

    [Fact]
    public async Task Dispatcher_enabled_false_does_not_claim_or_lease()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var runId = Guid.NewGuid().ToString("N");
        await _runs.CreateAsync(new E2eRun { Id = runId, TestCaseId = tcId, Status = E2eRunStatus.Queued });

        var provider = new CountingSandboxProvider();
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { Enabled = false, MaxConcurrent = 1 });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var dispatcher = new E2eRunDispatcher(
            _runs,
            pool,
            new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance),
            _testCases,
            monitor,
            new E2eRunCancellationRegistry(),
            NullLogger<E2eRunDispatcher>.Instance);

        Assert.False(await dispatcher.TryDispatchOneAsync(CancellationToken.None));

        Assert.Equal(0, provider.CreateCount);
        var stored = await _runs.GetAsync(runId);
        Assert.NotNull(stored);
        Assert.Equal(E2eRunStatus.Queued, stored.Status);
    }

    [Fact]
    public async Task Dispatcher_idle_queue_does_not_lease_sandbox()
    {
        var provider = new CountingSandboxProvider();
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { Enabled = true, MaxConcurrent = 1 });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var dispatcher = new E2eRunDispatcher(
            _runs,
            pool,
            new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance),
            _testCases,
            monitor,
            new E2eRunCancellationRegistry(),
            NullLogger<E2eRunDispatcher>.Instance);

        Assert.False(await dispatcher.TryDispatchOneAsync(CancellationToken.None));

        Assert.Equal(0, provider.CreateCount);
    }

    [Fact]
    public async Task Dispatcher_runs_many_replays_concurrently_across_the_pool()
    {
        const int total = 8;
        const int max = 4;
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var batch = Guid.NewGuid().ToString("N");
        for (var i = 0; i < total; i++)
        {
            await _runs.CreateAsync(new E2eRun
            {
                Id = Guid.NewGuid().ToString("N"),
                TestCaseId = tcId,
                Status = E2eRunStatus.Queued,
                BatchId = batch,
            });
        }

        // 100ms per replay step lets us observe parallelism without making the test slow.
        var perStepDelay = TimeSpan.FromMilliseconds(100);
        var provider = new CountingSandboxProvider(perStepDelay);
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            Enabled = true,
            MaxConcurrent = max,
            PollInterval = TimeSpan.FromMilliseconds(5),
            PerRunTimeout = TimeSpan.FromSeconds(30),
        });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);
        var dispatcher = new E2eRunDispatcher(_runs, pool, runtime, _testCases, monitor, new E2eRunCancellationRegistry(), NullLogger<E2eRunDispatcher>.Instance);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < total; i++)
        {
            // Drive the dispatcher one step at a time until everything is in-flight or terminal.
            var dispatched = await dispatcher.TryDispatchOneAsync(CancellationToken.None);
            if (!dispatched)
            {
                await Task.Delay(20);
                i--; // retry — pool busy, no claim yet
            }
        }

        // Wait for all runs to terminalise.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var terminal = 0;
            await foreach (var r in _runs.ListByBatchAsync(batch))
            {
                if (r.Status is E2eRunStatus.Passed or E2eRunStatus.Failed or E2eRunStatus.Error or E2eRunStatus.Canceled)
                    terminal++;
            }
            if (terminal == total) break;
            await Task.Delay(25);
        }
        sw.Stop();

        var results = new List<E2eRun>();
        await foreach (var r in _runs.ListByBatchAsync(batch)) results.Add(r);
        Assert.Equal(total, results.Count);
        Assert.All(results, r => Assert.Equal(E2eRunStatus.Passed, r.Status));

        // Parallelism proof: sequential = total * perStepDelay (~800ms). With max=4 the
        // ideal is two waves (~200ms); give it a generous ceiling so CI jitter doesn't
        // flake — but well below the sequential bound.
        var sequentialBound = total * perStepDelay.TotalMilliseconds;
        Assert.True(sw.Elapsed.TotalMilliseconds < sequentialBound,
            $"Dispatcher took {sw.Elapsed.TotalMilliseconds:F0}ms; sequential would be ~{sequentialBound:F0}ms — parallelism appears broken.");

        // Max observed in-flight on the provider must NOT exceed the configured cap.
        Assert.True(provider.MaxConcurrentSeen <= max,
            $"Observed concurrency {provider.MaxConcurrentSeen} exceeded configured cap {max}.");
    }

    [Fact]
    public async Task Dispatcher_records_replay_driver_unavailable_as_error()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var runId = Guid.NewGuid().ToString("N");
        await _runs.CreateAsync(new E2eRun { Id = runId, TestCaseId = tcId, Status = E2eRunStatus.Queued });

        var provider = new CountingSandboxProvider { ExecResult = new SandboxExecResult(127, string.Empty, "missing driver") };
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            Enabled = true,
            MaxConcurrent = 1,
            PollInterval = TimeSpan.FromMilliseconds(5),
            PerRunTimeout = TimeSpan.FromSeconds(10),
        });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var dispatcher = new E2eRunDispatcher(
            _runs,
            pool,
            new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance),
            _testCases,
            monitor,
            new E2eRunCancellationRegistry(),
            NullLogger<E2eRunDispatcher>.Instance);

        Assert.True(await dispatcher.TryDispatchOneAsync(CancellationToken.None));

        var terminal = await WaitForRunStatusAsync(runId, E2eRunStatus.Error);
        Assert.Contains("ReplayDriverUnavailable", terminal.Result);
    }

    [Fact]
    public async Task Dispatcher_running_cancel_signals_active_replay_and_preserves_canceled_status()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var runId = Guid.NewGuid().ToString("N");
        await _runs.CreateAsync(new E2eRun { Id = runId, TestCaseId = tcId, Status = E2eRunStatus.Queued });

        var provider = new CountingSandboxProvider { BlockExecUntilCanceled = true };
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            Enabled = true,
            MaxConcurrent = 1,
            PollInterval = TimeSpan.FromMilliseconds(5),
            PerRunTimeout = TimeSpan.FromSeconds(30),
        });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var registry = new E2eRunCancellationRegistry();
        var dispatcher = new E2eRunDispatcher(
            _runs,
            pool,
            new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance),
            _testCases,
            monitor,
            registry,
            NullLogger<E2eRunDispatcher>.Instance);

        Assert.True(await dispatcher.TryDispatchOneAsync(CancellationToken.None));
        await provider.ExecStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(registry.Cancel(runId));
        Assert.True(await _runs.CancelAsync(runId));

        var terminal = await WaitForRunStatusAsync(runId, E2eRunStatus.Canceled);
        Assert.Equal(E2eRunStatus.Canceled, terminal.Status);
    }


    [Fact]
    public async Task Dispatcher_records_error_for_artifact_with_no_steps_and_no_assertions()
    {
        var emptyArtifact = JsonSerializer.Serialize(new E2eReplayArtifact());
        var tcId = await SeedE2eTestCaseAsync(emptyArtifact);

        var runId = Guid.NewGuid().ToString("N");
        await _runs.CreateAsync(new E2eRun { Id = runId, TestCaseId = tcId, Status = E2eRunStatus.Queued });

        var provider = new CountingSandboxProvider();
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            Enabled = true,
            MaxConcurrent = 1,
            PollInterval = TimeSpan.FromMilliseconds(5),
            PerRunTimeout = TimeSpan.FromSeconds(10),
        });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);
        var dispatcher = new E2eRunDispatcher(_runs, pool, runtime, _testCases, monitor, new E2eRunCancellationRegistry(), NullLogger<E2eRunDispatcher>.Instance);

        var dispatched = await dispatcher.TryDispatchOneAsync(CancellationToken.None);
        Assert.True(dispatched);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        E2eRun? terminal = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            terminal = await _runs.GetAsync(runId);
            if (terminal is { Status: E2eRunStatus.Passed or E2eRunStatus.Failed or E2eRunStatus.Error })
                break;
            await Task.Delay(20);
        }
        Assert.NotNull(terminal);
        Assert.Equal(E2eRunStatus.Error, terminal.Status);
        Assert.Contains("EmptyArtifact", terminal.Result);
    }

    // --------------------------------------------------------------------
    // Helpers
    // --------------------------------------------------------------------

    private async Task<string> SeedE2eTestCaseAsync(string artifactJson)
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "e2e test fixture",
            Prompt = "n/a",
        };
        await _itemStore.CreateAsync(item);

        var id = Guid.NewGuid().ToString("N");
        var tc = new TestCase
        {
            Id = id,
            Name = "fixture",
            Description = "fixture",
            SourceWorkItemId = item.Id.ToString(),
            AutomationKind = AutomationKind.E2eReplay,
            ExecutableArtifactJson = artifactJson,
        };
        await _testCases.CreateAsync(tc);
        return id;
    }

    private async Task<E2eRun> WaitForRunStatusAsync(string runId, E2eRunStatus status)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        E2eRun? current = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            current = await _runs.GetAsync(runId);
            if (current?.Status == status)
                return current;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Run {runId} did not reach {status}; current status={current?.Status}");
    }


    private static string MakeTrivialPassArtifact()
        => JsonSerializer.Serialize(new E2eReplayArtifact
        {
            Steps = [new E2eReplayStep { Action = "navigate", Target = "http://app.local/" }],
            Assertions = [new E2eReplayAssertion { Kind = "selectorVisible", Selector = "#root" }],
        });

    // --------------------------------------------------------------------
    // Test doubles
    // --------------------------------------------------------------------

    private sealed class SimpleOptionsMonitor<T> : IOptionsMonitor<T> where T : class
    {
        public SimpleOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>
    /// Sandbox that resolves commands by argv[0] against an in-memory dictionary.
    /// Sufficient for asserting replay engine semantics without standing up a real VM.
    /// </summary>
    private sealed class FakeSandbox : ISandbox
    {
        public Dictionary<string, Func<CancellationToken, SandboxExecResult>> Programs { get; } = new(StringComparer.Ordinal);
        public string Id { get; } = "fake-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        public List<IReadOnlyList<string>> ExecLog { get; } = new();
        public List<SandboxExec> ExecRequests { get; } = new();

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            ExecLog.Add(exec.Argv);
            ExecRequests.Add(exec);
            if (exec.Argv.Count == 0)
                return Task.FromResult(new SandboxExecResult(127, string.Empty, "empty"));
            if (Programs.TryGetValue(exec.Argv[0], out var handler))
                return Task.FromResult(handler(ct));
            return Task.FromResult(new SandboxExecResult(127, string.Empty, $"unknown: {exec.Argv[0]}"));
        }

        public ValueTask DisposeAsync() => default;
    }

    private sealed class CountingSandboxProvider : ISandboxProvider
    {
        private readonly TimeSpan _execDelay;
        private readonly List<CountingSandbox> _all = new();
        private int _inFlight;
        public bool ThrowOnCreate { get; set; }
        public int CreateCount;
        public int MaxConcurrentSeen;
        public List<SandboxSpec> Specs { get; } = new();
        public SandboxExecResult? ExecResult { get; set; }
        public bool BlockExecUntilCanceled { get; set; }
        public TaskCompletionSource ExecStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CountingSandboxProvider() : this(TimeSpan.Zero) { }
        public CountingSandboxProvider(TimeSpan execDelay) { _execDelay = execDelay; }

        public string Name => "fake-counting";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            if (ThrowOnCreate) throw new InvalidOperationException("test forced");
            Specs.Add(spec);
            Interlocked.Increment(ref CreateCount);
            var current = Interlocked.Increment(ref _inFlight);
            UpdateMax(current);
            var sb = new CountingSandbox(this, _execDelay);
            lock (_all) _all.Add(sb);
            return Task.FromResult<ISandbox>(sb);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;

        public bool AllSandboxesDisposed { get { lock (_all) return _all.All(s => s.Disposed); } }

        internal void ReleaseSandbox() => Interlocked.Decrement(ref _inFlight);

        private void UpdateMax(int current)
        {
            int existing;
            do { existing = Volatile.Read(ref MaxConcurrentSeen); }
            while (current > existing
                && Interlocked.CompareExchange(ref MaxConcurrentSeen, current, existing) != existing);
        }
    }

    private sealed class CountingSandbox : ISandbox
    {
        private readonly CountingSandboxProvider _owner;
        private readonly TimeSpan _execDelay;
        public bool Disposed;

        public CountingSandbox(CountingSandboxProvider owner, TimeSpan execDelay)
        {
            _owner = owner;
            _execDelay = execDelay;
            Id = "counting-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        public string Id { get; }

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (_execDelay > TimeSpan.Zero)
            {
                try { await Task.Delay(_execDelay, ct); }
                catch (OperationCanceledException) { throw; }
            }
            if (_owner.BlockExecUntilCanceled)
            {
                _owner.ExecStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return _owner.ExecResult ?? new SandboxExecResult(0, string.Empty, string.Empty);
        }

        public ValueTask DisposeAsync()
        {
            if (!Disposed)
            {
                Disposed = true;
                _owner.ReleaseSandbox();
            }
            return default;
        }
    }
}
