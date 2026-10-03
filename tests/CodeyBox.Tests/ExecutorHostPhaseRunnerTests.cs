using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using RunnerClock = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for executor-side phase execution
/// (<see cref="ExecutorHostPhaseRunner"/>): a dispatched phase provisions one
/// sandbox, runs the phase's agent work, and returns its result; identical
/// redelivery replays without a second sandbox while a differing body
/// conflicts; a mid-phase disconnect under RetainSandboxForResume resumes
/// instead of duplicating; concurrent dispatches never exceed declared
/// capacity; and executor-environment failures surface as infrastructure,
/// never as a verdict on the diff.
/// </summary>
public sealed class ExecutorHostPhaseRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "codeybox-executor-runner-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _stagingRoots = [];

    public void Dispose()
    {
        foreach (var dir in _stagingRoots.Concat([_root]))
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }

    // ── 1. a dispatched phase executes on the executor and returns its result ──

    [Fact]
    public async Task DispatchedPhase_ProvisionsOneSandbox_RunsHandler_ReturnsResult()
    {
        var provider = new ScriptableSandboxProvider();
        var handler = new ScriptablePhaseHandler();
        var runner = NewRunner(provider, handler, out var tracker, out var staging);
        var staged = StageRepo(staging, out var repositoryId);
        var request = NewRequest(phase: "work", repositoryId: repositoryId);

        var result = await runner.ExecutePhaseAsync(request, CancellationToken.None);

        Assert.Same(handler.Result, result);
        Assert.Equal("deadbeef", result.CommitSha!.Substring(0, 8));
        Assert.Equal(ExecutorPhaseOutcome.Succeeded, result.Outcome);
        Assert.Single(provider.CreatedSpecs);
        var sandbox = Assert.Single(provider.CreatedSandboxes);
        Assert.True(sandbox.Disposed);
        Assert.Equal(0, tracker.TrackedCount);
        Assert.Equal(staged, handler.SeenRepoPath);
        Assert.Same(sandbox, handler.SeenSandbox);
        var spec = provider.CreatedSpecs[0];
        Assert.Equal(SandboxPurpose.WorkItem, spec.Purpose);
        Assert.Equal("work", spec.TimingPhase);
    }

    [Theory]
    [InlineData("work")]
    [InlineData("rework")]
    [InlineData("audit")]
    [InlineData("audit-security")]
    [InlineData("merge")]
    public async Task PipelinePhases_AllRouteThroughHandler(string phase)
    {
        var provider = new ScriptableSandboxProvider();
        var handler = new ScriptablePhaseHandler();
        var runner = NewRunner(provider, handler, out _, out var staging);
        var repositoryId = NewRepositoryId();
        StageRepo(staging, repositoryId);

        var result = await runner.ExecutePhaseAsync(NewRequest(phase: phase, repositoryId: repositoryId), CancellationToken.None);

        Assert.Equal(ExecutorPhaseOutcome.Succeeded, result.Outcome);
        Assert.Contains(phase, handler.SeenPhases);
    }

    [Fact]
    public async Task DefaultSpec_CarriesImageKnob_Timing_AndDeniedNetwork()
    {
        var provider = new ScriptableSandboxProvider();
        var handler = new ScriptablePhaseHandler();
        var options = ValidOptions();
        options.PhaseSandboxImageReference = "img:phase-1";
        var runner = new ExecutorHostPhaseRunner(
            provider, new ExecutorSandboxTracker(), handler, () => options,
            () => new ExecutorPhaseDispatchOptions());
        var repositoryId = NewRepositoryId();
        StageRepo(options.PhaseStagingRoot, repositoryId);

        await runner.ExecutePhaseAsync(NewRequest(phase: "merge", repositoryId: repositoryId), CancellationToken.None);

        var spec = Assert.Single(provider.CreatedSpecs);
        Assert.Equal("img:phase-1", spec.ImageReference);
        Assert.Equal("merge", spec.TimingPhase);
        Assert.Equal(SandboxNetworkPolicy.Denied, spec.Network);
        Assert.NotNull(spec.TimingWorkItemId);
    }

    // ── 2. identical redelivery replays; differing body conflicts ──

    [Fact]
    public async Task IdenticalRedelivery_ReplaysOriginalResult_WithoutSecondSandbox()
    {
        var provider = new ScriptableSandboxProvider();
        var handler = new ScriptablePhaseHandler();
        var runner = NewRunner(provider, handler, out _, out var staging);
        var repositoryId = NewRepositoryId();
        StageRepo(staging, repositoryId);
        var request = NewRequest(payload: "{\"v\":1}", repositoryId: repositoryId);

        var first = await runner.ExecutePhaseAsync(request, CancellationToken.None);
        var second = await runner.ExecutePhaseAsync(request, CancellationToken.None);

        Assert.Same(first, second);
        Assert.Single(provider.CreatedSpecs);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RedeliveryWithDifferingBody_Conflicts_AndNeverExecutes()
    {
        var provider = new ScriptableSandboxProvider();
        var handler = new ScriptablePhaseHandler();
        var runner = NewRunner(provider, handler, out _, out var staging);
        var repositoryId = NewRepositoryId();
        StageRepo(staging, repositoryId);
        var item = Guid.NewGuid().ToString("N");

        await runner.ExecutePhaseAsync(NewRequest(item, "work", 0, "{\"v\":1}", repositoryId), CancellationToken.None);
        var conflict = await Assert.ThrowsAsync<ExecutorPhaseConflictException>(
            () => runner.ExecutePhaseAsync(NewRequest(item, "work", 0, "{\"v\":2}", repositoryId), CancellationToken.None));

        Assert.Contains("executor-phase/v1/", conflict.DispatchKey, StringComparison.Ordinal);
        Assert.Single(provider.CreatedSpecs);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ConcurrentIdenticalRedeliveries_SingleFlight_ProvisionsOnce()
    {
        var provider = new ScriptableSandboxProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptablePhaseHandler
        {
            BeforeResult = ct => gate.Task.WaitAsync(ct),
        };
        var runner = NewRunner(provider, handler, out _, out var staging);
        var repositoryId = NewRepositoryId();
        StageRepo(staging, repositoryId);
        var request = NewRequest(repositoryId: repositoryId);

        var executions = Enumerable.Range(0, 5)
            .Select(_ => runner.ExecutePhaseAsync(request, CancellationToken.None))
            .ToList();
        await WaitUntilAsync(() => Volatile.Read(ref handler.CurrentConcurrency) == 1, "handler to start");
        Assert.Single(provider.CreatedSpecs);
        gate.TrySetResult();

        var results = await Task.WhenAll(executions);
        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Single(provider.CreatedSpecs);
        Assert.Equal(1, handler.Calls);
    }

    // ── 3. mid-phase disconnect resumes without duplicating ──

    [Fact]
    public async Task MidPhaseDisconnect_RetainsSandbox_RedeliveryResumes_WithoutDuplicating()
    {
        var provider = new ScriptableSandboxProvider();
        var entered = new CountdownEvent(1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptablePhaseHandler
        {
            BeforeResult = async ct =>
            {
                entered.Signal();
                await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            },
        };
        var runner = NewRunner(provider, handler, out var tracker, out var staging);
        var repositoryId = NewRepositoryId();
        StageRepo(staging, repositoryId);
        var request = NewRequest(repositoryId: repositoryId);

        var first = runner.ExecutePhaseAsync(request, CancellationToken.None);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "phase did not start");
        Assert.Equal(1, runner.ActivePhaseCount);
        var sandbox = Assert.Single(provider.CreatedSandboxes);

        // The disconnect path the client's heartbeat loop reports: retain,
        // never dispose, never cancel local execution.
        var retained = tracker.MarkConnectionLost();
        Assert.True(tracker.IsConnectionLost);
        Assert.Single(retained);
        Assert.False(sandbox.Disposed);
        Assert.Equal(1, tracker.TrackedCount);
        Assert.Equal(1, runner.ActivePhaseCount);

        // Reconnect reconciliation keeps the still-owned phase sandbox and
        // reclaims only the untracked orphan.
        provider.Inventory = [
            new ManagedSandboxInfo(sandbox.Id, null, null, false),
            new ManagedSandboxInfo("vm-orphan", null, null, false),
        ];
        var outcome = await tracker.ReconcileOnReconnectAsync(provider);
        Assert.False(tracker.IsConnectionLost);
        Assert.Equal(1, outcome.RetainedCount);
        Assert.Equal(["vm-orphan"], provider.DisposedLeakedNames);
        Assert.False(sandbox.Disposed);

        // Redelivery while the original still runs attaches to it.
        var second = runner.ExecutePhaseAsync(request, CancellationToken.None);
        await Task.Delay(300, CancellationToken.None);
        Assert.Single(provider.CreatedSpecs);
        Assert.Equal(1, handler.Calls);

        gate.TrySetResult();
        var firstResult = await first;
        var secondResult = await second;

        Assert.Same(firstResult, secondResult);
        Assert.Single(provider.CreatedSpecs);
        Assert.Equal(1, handler.Calls);
        Assert.True(sandbox.Disposed);
        Assert.Equal(0, tracker.TrackedCount);
        Assert.Equal(0, runner.ActivePhaseCount);
    }

    // ── 4. capacity is never exceeded; live load is reported ──

    [Fact]
    public async Task ConcurrentDispatches_NeverExceedDeclaredCapacity()
    {
        var provider = new ScriptableSandboxProvider();
        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptablePhaseHandler
        {
            BeforeResult = async ct =>
            {
                if (Interlocked.Increment(ref started) == 2)
                    readyTcs.TrySetResult();
                await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            },
        };
        var options = ValidOptions();
        options.MaxConcurrentSandboxes = 2;
        var runner = new ExecutorHostPhaseRunner(
            provider, new ExecutorSandboxTracker(), handler, () => options,
            () => new ExecutorPhaseDispatchOptions());
        var staging = options.PhaseStagingRoot;
        var requests = Enumerable.Range(0, 3)
            .Select(_ =>
            {
                var repositoryId = NewRepositoryId();
                StageRepo(staging, repositoryId);
                return NewRequest(repositoryId: repositoryId);
            })
            .ToList();

        var executions = requests.Select(r => runner.ExecutePhaseAsync(r, CancellationToken.None)).ToList();
        await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        await Task.Delay(300, CancellationToken.None);

        Assert.Equal(2, runner.ActivePhaseCount);
        Assert.Equal(2, provider.ProvisionCount);
        Assert.True(handler.MaxObservedConcurrency <= 2);

        gate.TrySetResult();
        await Task.WhenAll(executions);

        Assert.Equal(3, provider.ProvisionCount);
        Assert.True(handler.MaxObservedConcurrency <= 2);
        Assert.Equal(0, runner.ActivePhaseCount);
    }

    [Fact]
    public async Task ZeroCapacity_RefusesFast_AsInfrastructure()
    {
        var provider = new ScriptableSandboxProvider();
        var handler = new ScriptablePhaseHandler();
        var options = ValidOptions();
        options.MaxConcurrentSandboxes = 0;
        var runner = new ExecutorHostPhaseRunner(
            provider, new ExecutorSandboxTracker(), handler, () => options,
            () => new ExecutorPhaseDispatchOptions());
        var repositoryId = NewRepositoryId();
        StageRepo(options.PhaseStagingRoot, repositoryId);

        var thrown = await Assert.ThrowsAsync<ExecutorPhaseTransportException>(
            () => runner.ExecutePhaseAsync(NewRequest(repositoryId: repositoryId), CancellationToken.None));

        Assert.Equal("capacity", thrown.Operation);
        Assert.Empty(provider.CreatedSpecs);
        Assert.Equal(0, handler.Calls);
    }

    // ── 5. environment failures are infrastructure, not diff verdicts ──

    [Fact]
    public async Task ProvisioningFailure_SurfacesAsTransport_NotAgentFailure()
    {
        var provider = new ScriptableSandboxProvider
        {
            FailWith = new InvalidOperationException("no hypervisor on this host"),
        };
        var handler = new ScriptablePhaseHandler();
        var runner = NewRunner(provider, handler, out var tracker, out var staging);
        var repositoryId = NewRepositoryId();
        StageRepo(staging, repositoryId);
        var request = NewRequest(repositoryId: repositoryId);

        var thrown = await Assert.ThrowsAsync<ExecutorPhaseTransportException>(
            () => runner.ExecutePhaseAsync(request, CancellationToken.None));

        Assert.Equal("exec-1", thrown.HostId);
        Assert.Equal("provision-sandbox", thrown.Operation);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, tracker.TrackedCount);

        // Nothing is cached for infrastructure failures, so a redelivery may
        // retry elsewhere instead of replaying the failure.
        provider.FailWith = null;
        var result = await runner.ExecutePhaseAsync(request, CancellationToken.None);
        Assert.Equal(ExecutorPhaseOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task SandboxTransportLoss_SurfacesAsTransport()
    {
        var provider = new ScriptableSandboxProvider();
        var handler = new ScriptablePhaseHandler
        {
            Throw = new SandboxExecutionUnavailableException(137),
        };
        var runner = NewRunner(provider, handler, out _, out var staging);
        var repositoryId = NewRepositoryId();
        StageRepo(staging, repositoryId);

        var thrown = await Assert.ThrowsAsync<ExecutorPhaseTransportException>(
            () => runner.ExecutePhaseAsync(NewRequest(repositoryId: repositoryId), CancellationToken.None));

        Assert.Equal("run-phase", thrown.Operation);
        var sandbox = Assert.Single(provider.CreatedSandboxes);
        Assert.True(sandbox.Disposed);
    }

    [Fact]
    public async Task UnexpectedHandlerFailure_SurfacesAsTransport_NeverAsDiffVerdict()
    {
        var provider = new ScriptableSandboxProvider();
        var handler = new ScriptablePhaseHandler
        {
            Throw = new InvalidOperationException("handler bug"),
        };
        var runner = NewRunner(provider, handler, out _, out var staging);
        var repositoryId = NewRepositoryId();
        StageRepo(staging, repositoryId);

        await Assert.ThrowsAsync<ExecutorPhaseTransportException>(
            () => runner.ExecutePhaseAsync(NewRequest(repositoryId: repositoryId), CancellationToken.None));
    }

    [Fact]
    public async Task AgentFailure_ReturnsAsResult_AndIsReplayed()
    {
        var provider = new ScriptableSandboxProvider();
        var handler = new ScriptablePhaseHandler
        {
            Result = new ExecutorPhaseResult
            {
                Outcome = ExecutorPhaseOutcome.AgentFailed,
                Usage = new ExecutorPhaseUsage(10, 5, 0.001m),
                Findings = ["agent could not complete the phase"],
                ErrorMessage = "simulated agent failure",
            },
        };
        var runner = NewRunner(provider, handler, out _, out var staging);
        var repositoryId = NewRepositoryId();
        StageRepo(staging, repositoryId);
        var request = NewRequest(repositoryId: repositoryId);

        var first = await runner.ExecutePhaseAsync(request, CancellationToken.None);
        var second = await runner.ExecutePhaseAsync(request, CancellationToken.None);

        Assert.Equal(ExecutorPhaseOutcome.AgentFailed, first.Outcome);
        Assert.Same(first, second);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CredentialMissingResult_PassesThrough_ForProxyFailover()
    {
        var provider = new ScriptableSandboxProvider();
        var handler = new ScriptablePhaseHandler
        {
            Result = new ExecutorPhaseResult
            {
                Outcome = ExecutorPhaseOutcome.AgentFailed,
                Usage = new ExecutorPhaseUsage(0, 0, 0),
                ErrorMessage = ExecutorPhaseProxy.CredentialMissingErrorPrefix + "claude",
            },
        };
        var runner = NewRunner(provider, handler, out _, out var staging);
        var repositoryId = NewRepositoryId();
        StageRepo(staging, repositoryId);

        var result = await runner.ExecutePhaseAsync(NewRequest(repositoryId: repositoryId), CancellationToken.None);

        Assert.Equal(ExecutorPhaseOutcome.AgentFailed, result.Outcome);
        Assert.StartsWith(ExecutorPhaseProxy.CredentialMissingErrorPrefix, result.ErrorMessage, StringComparison.Ordinal);
    }

    // ── envelope, staging, cancellation, and cache bounds ──

    [Fact]
    public async Task MissingStagedRepo_SurfacesAsTransport_WithoutProvisioning()
    {
        var provider = new ScriptableSandboxProvider();
        var handler = new ScriptablePhaseHandler();
        var runner = NewRunner(provider, handler, out _, out _);

        var thrown = await Assert.ThrowsAsync<ExecutorPhaseTransportException>(
            () => runner.ExecutePhaseAsync(NewRequest(repositoryId: "never-staged"), CancellationToken.None));

        Assert.Equal("stage", thrown.Operation);
        Assert.Empty(provider.CreatedSpecs);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("bad phase!")]
    [InlineData("")]
    public async Task InvalidPhaseName_RejectedBeforeProvisioning(string phase)
    {
        var provider = new ScriptableSandboxProvider();
        var handler = new ScriptablePhaseHandler();
        var runner = NewRunner(provider, handler, out _, out var staging);
        var repositoryId = NewRepositoryId();
        StageRepo(staging, repositoryId);

        await Assert.ThrowsAsync<ArgumentException>(
            () => runner.ExecutePhaseAsync(NewRequest(phase: phase, repositoryId: repositoryId), CancellationToken.None));

        Assert.Empty(provider.CreatedSpecs);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Cancellation_DoesNotPoison_RedeliveryReexecutes()
    {
        var provider = new ScriptableSandboxProvider();
        var enteredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCall = 1;
        var handler = new ScriptablePhaseHandler
        {
            BeforeResult = ct =>
            {
                enteredTcs.TrySetResult();
                return Interlocked.Exchange(ref firstCall, 0) == 1
                    ? Task.Delay(TimeSpan.FromMinutes(5), ct)
                    : Task.CompletedTask;
            },
        };
        var runner = NewRunner(provider, handler, out var tracker, out var staging);
        var repositoryId = NewRepositoryId();
        StageRepo(staging, repositoryId);
        var request = NewRequest(repositoryId: repositoryId);

        using var cts = new CancellationTokenSource();
        var execution = runner.ExecutePhaseAsync(request, cts.Token);
        await enteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.Equal(0, tracker.TrackedCount);
        var sandbox = Assert.Single(provider.CreatedSandboxes);
        Assert.True(sandbox.Disposed);

        var result = await runner.ExecutePhaseAsync(request, CancellationToken.None);
        Assert.Equal(ExecutorPhaseOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, provider.ProvisionCount);
    }

    [Fact]
    public async Task MalformedHandlerResult_IsNotCached_RedeliveryReexecutes()
    {
        var provider = new ScriptableSandboxProvider();
        var handler = new ScriptablePhaseHandler
        {
            Result = new ExecutorPhaseResult
            {
                Outcome = ExecutorPhaseOutcome.Succeeded,
                CommitSha = "not-a-sha",
                Usage = new ExecutorPhaseUsage(0, 0, 0),
            },
        };
        var runner = NewRunner(provider, handler, out _, out var staging);
        var repositoryId = NewRepositoryId();
        StageRepo(staging, repositoryId);
        var request = NewRequest(repositoryId: repositoryId);

        await Assert.ThrowsAsync<ExecutorPhaseException>(() => runner.ExecutePhaseAsync(request, CancellationToken.None));
        handler.Result = new ExecutorPhaseResult
        {
            Outcome = ExecutorPhaseOutcome.Succeeded,
            Usage = new ExecutorPhaseUsage(1, 1, 0.001m),
        };
        var result = await runner.ExecutePhaseAsync(request, CancellationToken.None);

        Assert.Equal(1L, result.Usage.InputTokens);
        Assert.Equal(2, provider.ProvisionCount);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ExpiredResult_RedeliveryReexecutes()
    {
        var clock = new RunnerClock(DateTimeOffset.UtcNow);
        var provider = new ScriptableSandboxProvider();
        var handler = new ScriptablePhaseHandler();
        var options = ValidOptions();
        options.PhaseResultCacheTtl = TimeSpan.FromHours(1);
        var runner = new ExecutorHostPhaseRunner(
            provider, new ExecutorSandboxTracker(), handler, () => options,
            () => new ExecutorPhaseDispatchOptions(), clock: clock,
            log: NullLogger<ExecutorHostPhaseRunner>.Instance);
        var repositoryId = NewRepositoryId();
        StageRepo(options.PhaseStagingRoot, repositoryId);
        var request = NewRequest(repositoryId: repositoryId);

        await runner.ExecutePhaseAsync(request, CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(2));
        await runner.ExecutePhaseAsync(request, CancellationToken.None);

        Assert.Equal(2, provider.ProvisionCount);
    }

    [Fact]
    public async Task CacheLimit_EvictsOldestCompleted()
    {
        var provider = new ScriptableSandboxProvider();
        var handler = new ScriptablePhaseHandler();
        var options = ValidOptions();
        options.MaxCachedPhaseResults = 1;
        var runner = new ExecutorHostPhaseRunner(
            provider, new ExecutorSandboxTracker(), handler, () => options,
            () => new ExecutorPhaseDispatchOptions());
        var firstRepo = NewRepositoryId();
        StageRepo(options.PhaseStagingRoot, firstRepo);
        var secondRepo = NewRepositoryId();
        StageRepo(options.PhaseStagingRoot, secondRepo);
        var first = NewRequest(item: Guid.NewGuid().ToString("N"), repositoryId: firstRepo);
        var second = NewRequest(item: Guid.NewGuid().ToString("N"), repositoryId: secondRepo);

        await runner.ExecutePhaseAsync(first, CancellationToken.None);
        await runner.ExecutePhaseAsync(second, CancellationToken.None);
        await runner.ExecutePhaseAsync(first, CancellationToken.None);

        Assert.Equal(3, provider.ProvisionCount);
    }

    // ── helpers ──

    private ExecutorHostPhaseRunner NewRunner(
        ScriptableSandboxProvider provider,
        ScriptablePhaseHandler handler,
        out ExecutorSandboxTracker tracker,
        out string staging)
    {
        var options = ValidOptions();
        staging = options.PhaseStagingRoot;
        tracker = new ExecutorSandboxTracker();
        return new ExecutorHostPhaseRunner(
            provider, tracker, handler, () => options,
            () => new ExecutorPhaseDispatchOptions());
    }

    private ExecutorOptions ValidOptions()
    {
        var staging = Path.Combine(_root, "staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        _stagingRoots.Add(staging);
        return new ExecutorOptions
        {
            HostId = "exec-1",
            OrchestratorBaseUrl = "http://127.0.0.1:9/",
            PhaseStagingRoot = staging,
        };
    }

    private static string NewRepositoryId() => Guid.NewGuid().ToString("N");

    private string StageRepo(string staging, out string repositoryId)
    {
        repositoryId = NewRepositoryId();
        return StageRepo(staging, repositoryId);
    }

    private static string StageRepo(string staging, string repositoryId)
    {
        var dir = Path.Combine(staging, repositoryId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "HEAD"), "ref: refs/heads/main\n");
        return dir;
    }

    private static ExecutorPhaseRequest NewRequest(
        string? item = null,
        string phase = "work",
        int attempt = 0,
        string payload = "{}",
        string? repositoryId = null) =>
        new()
        {
            WorkItemId = item ?? Guid.NewGuid().ToString("N"),
            Phase = phase,
            Attempt = attempt,
            RepositoryId = repositoryId ?? Guid.NewGuid().ToString("N"),
            PayloadJson = payload,
        };

    private static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var start = DateTimeOffset.UtcNow;
        while (!condition())
        {
            if ((DateTimeOffset.UtcNow - start).TotalMilliseconds > timeoutMs)
                throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class ScriptableSandbox(string id) : ISandbox
    {
        public string Id { get; } = id;
        public bool Disposed { get; private set; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(new SandboxExecResult(0, "", ""));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptableSandboxProvider : ISandboxProvider
    {
        private int _sequence;
        private readonly object _mutex = new();

        public string Name => "scriptable";
        public List<SandboxSpec> CreatedSpecs { get; } = [];
        public List<ScriptableSandbox> CreatedSandboxes { get; } = [];

        public int ProvisionCount
        {
            get { lock (_mutex) return CreatedSpecs.Count; }
        }
        public Exception? FailWith { get; set; }
        public List<ManagedSandboxInfo> Inventory { get; set; } = [];
        public bool InventoryComplete { get; set; } = true;
        public List<string> DisposedLeakedNames { get; } = [];

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            if (FailWith is not null)
                return Task.FromException<ISandbox>(FailWith);
            var sandbox = new ScriptableSandbox("vm-" + Interlocked.Increment(ref _sequence));
            lock (_mutex)
            {
                CreatedSpecs.Add(spec);
                CreatedSandboxes.Add(sandbox);
            }
            return Task.FromResult<ISandbox>(sandbox);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>(Inventory);

        public Task<ManagedSandboxInventory> ListManagedInventoryAsync(CancellationToken ct) =>
            Task.FromResult(new ManagedSandboxInventory(Inventory, InventoryComplete));

        public Task DisposeLeakedAsync(string name, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DisposeLeakedAsync(ManagedSandboxInfo sandbox, CancellationToken ct)
        {
            DisposedLeakedNames.Add(sandbox.Name);
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptablePhaseHandler : IExecutorPhaseHandler
    {
        private int _currentConcurrency;

        public int Calls;
        public int CurrentConcurrency;
        public int MaxObservedConcurrency;
        public List<string> SeenPhases { get; } = [];
        public string? SeenRepoPath { get; private set; }
        public ISandbox? SeenSandbox { get; private set; }
        public Func<CancellationToken, Task>? BeforeResult { get; set; }
        public Exception? Throw { get; set; }
        public ExecutorPhaseResult Result { get; set; } = new()
        {
            Outcome = ExecutorPhaseOutcome.Succeeded,
            CommitSha = "deadbeef" + new string('d', 32),
            Findings = ["phase complete"],
            Usage = new ExecutorPhaseUsage(100, 50, 0.01m),
        };

        public async Task<ExecutorPhaseResult> ExecuteAsync(
            ExecutorPhaseRequest request,
            string repoPath,
            ISandbox sandbox,
            CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var current = Interlocked.Increment(ref _currentConcurrency);
            Volatile.Write(ref CurrentConcurrency, current);
            var observed = current;
            int priorMax;
            do
            {
                priorMax = MaxObservedConcurrency;
                if (observed <= priorMax)
                    break;
            } while (Interlocked.CompareExchange(ref MaxObservedConcurrency, observed, priorMax) != priorMax);
            lock (SeenPhases) SeenPhases.Add(request.Phase);
            SeenRepoPath = repoPath;
            SeenSandbox = sandbox;
            try
            {
                if (Throw is not null)
                    throw Throw;
                if (BeforeResult is not null)
                    await BeforeResult(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return Result;
            }
            finally
            {
                var left = Interlocked.Decrement(ref _currentConcurrency);
                Volatile.Write(ref CurrentConcurrency, left);
            }
        }
    }
}
