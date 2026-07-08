using CodeyBox.Agents.Cursor;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="InVmSmokeProbeService"/> — the background driver of the
/// in-VM smoke sweeps. Covers the Enabled gate, the single-sweep
/// (<c>SweepIntervalSeconds &lt;= 0</c>) path, the repeating interval loop
/// (<c>SweepIntervalSeconds &gt; 0</c>), and that a sweep fault is swallowed
/// instead of escaping <c>ExecuteAsync</c>.
/// </summary>
// Serialised with other BackgroundService timing-sensitive tests because the
// periodic cases exercise short BackgroundService delay/cancellation loops.
[Collection("Background service timing")]
public sealed class InVmSmokeProbeServiceTests : IDisposable
{
    private static readonly TimeSpan TestHangGuard = TimeSpan.FromMinutes(1);
    private const int PeriodicSweepIntervalSeconds = 120;

    private static readonly AgentCredential CursorCred = new(
        AgentKind.Cursor,
        new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = "{\"token\":\"t\"}" },
        new Dictionary<string, string>());

    private readonly CancellationTokenSource _testHangGuard = new(TestHangGuard);

    public void Dispose()
    {
        _testHangGuard.Cancel();
        _testHangGuard.Dispose();
    }

    private static InVmSmokeProber BuildProber(
        ScriptedSandboxProvider provider,
        AgentAvailabilityRegistry registry,
        StubBaselineResolver resolver,
        bool enabled)
    {
        return new InVmSmokeProber(
            provider,
            resolver,
            resolver,
            new ConstantCredentialProvider(CursorCred),
            [new CursorInVmSmokeProbe()],
            registry,
            new InVmSmokeCache(TimeSpan.FromMinutes(60)),
            new NullWebhookDispatcher(),
            new InVmSmokeOptions { Enabled = enabled, ImageReference = "img", NetworkProfile = "work-profile", SweepIntervalSeconds = 0 },
            NullLogger<InVmSmokeProber>.Instance);
    }

    private static AgentAvailabilityRegistry NewRegistry() =>
        new(new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);

    private static InVmSmokeProbeService BuildService(
        InVmSmokeProber prober,
        int sweepIntervalSeconds = 0,
        TimeProvider? timeProvider = null) =>
        new(prober,
            new InVmSmokeOptions { Enabled = true, ImageReference = "img", NetworkProfile = "work-profile", SweepIntervalSeconds = sweepIntervalSeconds },
            NullLogger<InVmSmokeProbeService>.Instance,
            timeProvider: timeProvider);

    private async Task AwaitExecute(InVmSmokeProbeService service)
    {
        var done = service.ExecuteTask ?? Task.CompletedTask;
        try
        {
            await done.WaitAsync(_testHangGuard.Token); // surface any exception that escaped (there must be none)
        }
        catch (OperationCanceledException) when (_testHangGuard.IsCancellationRequested)
        {
            throw new Xunit.Sdk.XunitException(
                "InVmSmokeProbeService.ExecuteTask did not complete before the test hang guard fired.");
        }
    }

    [Fact]
    public async Task StartupSweep_Runs_AndExcludesBrokenAgent()
    {
        var provider = new ScriptedSandboxProvider(exec =>
            exec.Argv.Count >= 2 && exec.Argv[1] == "--version"
                ? new SandboxExecResult(127, "", "command not found")
                : new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        var prober = BuildProber(provider, registry, new StubBaselineResolver("base-A"), enabled: true);
        var service = BuildService(prober);

        await service.StartAsync(CancellationToken.None);
        await AwaitExecute(service);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, provider.CreateCount);
        Assert.False(registry.GetAvailability(AgentKind.Cursor).Available);
    }

    [Fact]
    public async Task Disabled_RunsNoSweep()
    {
        var provider = new ScriptedSandboxProvider(_ => new SandboxExecResult(0, "", ""));
        var registry = NewRegistry();
        var prober = BuildProber(provider, registry, new StubBaselineResolver("base-A"), enabled: false);
        var service = BuildService(prober);

        await service.StartAsync(CancellationToken.None);
        await AwaitExecute(service);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, provider.CreateCount);
    }

    [Fact]
    public async Task SweepException_IsSwallowed_ExecuteAsyncCompletes()
    {
        // The prober now handles expected resolver/provisioning faults internally,
        // so use a gate that throws directly from ProbeAllAsync. This pins the
        // service boundary catch itself: removing SafeSweepAsync's catch would make
        // ExecuteAsync fault here.
        var gate = new ThrowingGate();
        var service = new InVmSmokeProbeService(
            gate,
            new InVmSmokeOptions { Enabled = true, ImageReference = "img", NetworkProfile = "work-profile", SweepIntervalSeconds = 0 },
            NullLogger<InVmSmokeProbeService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await AwaitExecute(service); // would throw here if SafeSweepAsync let it escape
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, gate.ThrowCount);
    }

    [Fact]
    public async Task PeriodicSweep_RepeatsOnInterval_UntilStopped()
    {
        // SweepIntervalSeconds > 0 takes the while-loop path: a startup sweep
        // plus at least one interval-driven re-invocation of SafeSweepAsync. The
        // one-shot tests above never exercise the Task.Delay loop, so this test
        // observes the fake-time timer before advancing it.
        var gate = new CountingGate();
        var time = new DelayTrackingTimeProvider();
        var service = new InVmSmokeProbeService(
            gate,
            new InVmSmokeOptions { Enabled = true, ImageReference = "img", NetworkProfile = "work-profile", SweepIntervalSeconds = PeriodicSweepIntervalSeconds },
            NullLogger<InVmSmokeProbeService>.Instance,
            timeProvider: time);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var startupCount = await gate.WaitForAtLeastAsync(1, _testHangGuard.Token);
            Assert.True(startupCount >= 1, $"Expected at least one startup sweep, observed {startupCount}.");
            await time.WaitForTimerCountAsync(1, _testHangGuard.Token);
            Assert.Equal(1, gate.SweepCount);

            time.Advance(TimeSpan.FromSeconds(PeriodicSweepIntervalSeconds));
            var intervalCount = await gate.WaitForAtLeastAsync(2, _testHangGuard.Token);
            Assert.True(intervalCount >= 2, $"Expected at least two sweeps after advancing fake time, observed {intervalCount}.");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PeriodicSweep_StaysAlive_WhenGateStartsDisabledAndLaterEnables()
    {
        var gate = new CountingGate { Enabled = false };
        var time = new DelayTrackingTimeProvider();
        var service = new InVmSmokeProbeService(
            gate,
            new InVmSmokeOptions { Enabled = true, ImageReference = "img", NetworkProfile = "work-profile", SweepIntervalSeconds = PeriodicSweepIntervalSeconds },
            NullLogger<InVmSmokeProbeService>.Instance,
            timeProvider: time);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await time.WaitForTimerCountAsync(1, _testHangGuard.Token);
            Assert.Equal(0, gate.SweepCount);

            gate.Enabled = true;
            time.Advance(TimeSpan.FromSeconds(PeriodicSweepIntervalSeconds));
            var enabledCount = await gate.WaitForAtLeastAsync(1, _testHangGuard.Token);
            Assert.True(enabledCount >= 1, $"Expected the re-enabled periodic gate to sweep, observed {enabledCount}.");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartupSweep_UsesProjectWorkTarget_WhenSmokeProfileUnset()
    {
        var gate = new CountingGate();
        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.invalid/repo.git",
            NetworkProfiles = new ProjectNetworkProfiles { Work = "internet-only" },
        };
        var service = new InVmSmokeProbeService(
            gate,
            new InVmSmokeOptions { Enabled = true, ImageReference = "img", SweepIntervalSeconds = 0 },
            NullLogger<InVmSmokeProbeService>.Instance,
            new InMemoryProjectRepository(project));

        await service.StartAsync(CancellationToken.None);
        await AwaitExecute(service);
        await service.StopAsync(CancellationToken.None);

        var target = Assert.Single(gate.Targets);
        Assert.Equal("internet-only", target.NetworkProfile);
        Assert.Equal(SandboxProfileFlavor.Headless, target.Flavor);
    }

    [Fact]
    public async Task StartupSweep_ExplicitSmokeProfile_UsesTargetAwareGate_AndWinsOverProjectProfile()
    {
        var gate = new CountingGate();
        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.invalid/repo.git",
            NetworkProfiles = new ProjectNetworkProfiles { Work = "project-work" },
        };
        var service = new InVmSmokeProbeService(
            gate,
            new InVmSmokeOptions
            {
                Enabled = true,
                ImageReference = "img",
                NetworkProfile = "smoke-explicit",
                SweepIntervalSeconds = 0,
            },
            NullLogger<InVmSmokeProbeService>.Instance,
            new InMemoryProjectRepository(project));

        await service.StartAsync(CancellationToken.None);
        await AwaitExecute(service);
        await service.StopAsync(CancellationToken.None);

        var target = Assert.Single(gate.Targets);
        Assert.Equal("smoke-explicit", target.NetworkProfile);
        Assert.Equal(SandboxProfileFlavor.Headless, target.Flavor);
    }

    [Fact]
    public async Task StartupSweep_SkipsBlankProjectProfiles()
    {
        var gate = new CountingGate();
        var blank = new Project
        {
            Id = new ProjectId("blank"),
            DisplayName = "Blank",
            RepositoryUrl = "https://example.invalid/blank.git",
            NetworkProfiles = new ProjectNetworkProfiles { Work = "" },
        };
        var valid = new Project
        {
            Id = new ProjectId("valid"),
            DisplayName = "Valid",
            RepositoryUrl = "https://example.invalid/valid.git",
            NetworkProfiles = new ProjectNetworkProfiles { Work = "internet-only" },
        };
        var service = new InVmSmokeProbeService(
            gate,
            new InVmSmokeOptions { Enabled = true, ImageReference = "img", SweepIntervalSeconds = 0 },
            NullLogger<InVmSmokeProbeService>.Instance,
            new InMemoryProjectRepository(blank, valid));

        await service.StartAsync(CancellationToken.None);
        await AwaitExecute(service);
        await service.StopAsync(CancellationToken.None);

        var target = Assert.Single(gate.Targets);
        Assert.Equal("internet-only", target.NetworkProfile);
    }

    [Fact]
    public async Task StartupSweep_DeDuplicatesProjectProfiles()
    {
        var gate = new CountingGate();
        Project Project(string id) => new()
        {
            Id = new ProjectId(id),
            DisplayName = id,
            RepositoryUrl = $"https://example.invalid/{id}.git",
            NetworkProfiles = new ProjectNetworkProfiles { Work = "internet-only" },
        };
        var service = new InVmSmokeProbeService(
            gate,
            new InVmSmokeOptions { Enabled = true, ImageReference = "img", SweepIntervalSeconds = 0 },
            NullLogger<InVmSmokeProbeService>.Instance,
            new InMemoryProjectRepository(Project("alpha"), Project("beta")));

        await service.StartAsync(CancellationToken.None);
        await AwaitExecute(service);
        await service.StopAsync(CancellationToken.None);

        var target = Assert.Single(gate.Targets);
        Assert.Equal("internet-only", target.NetworkProfile);
    }


    /// <summary>
    /// In-VM smoke gate stub that counts <see cref="ProbeAllAsync"/> invocations
    /// and lets a test await a target count, so the repeating-interval loop can
    /// be observed without sleeping a fixed duration.
    /// </summary>
    private sealed class CountingGate : IInVmSmokeGate
    {
        private readonly object _sync = new();
        private readonly List<Waiter> _waiters = [];
        private volatile bool _enabled = true;
        private int _count;

        public bool Enabled { get => _enabled; set => _enabled = value; }
        public int SweepCount { get { lock (_sync) return _count; } }
        public List<InVmSmokeSandboxTarget> Targets { get; } = [];

        public Task ProbeAllAsync(CancellationToken ct)
        {
            lock (_sync)
            {
                _count++;
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    var waiter = _waiters[i];
                    if (_count < waiter.Target) continue;
                    _waiters.RemoveAt(i);
                    waiter.Tcs.TrySetResult(_count);
                }
            }
            return Task.CompletedTask;
        }

        public Task ProbeAllAsync(InVmSmokeSandboxTarget target, CancellationToken ct)
        {
            lock (_sync) Targets.Add(target);
            return ProbeAllAsync(ct);
        }

        public Task<AgentAvailability> EnsureAvailableAsync(
            AgentKind kind,
            InVmSmokeSandboxTarget target,
            CancellationToken ct)
            => Task.FromResult(new AgentAvailability(true, null, null));

        public Task<AgentAvailability?> ForceProbeAsync(AgentKind kind, CancellationToken ct)
            => Task.FromResult<AgentAvailability?>(new AgentAvailability(true, null, null));

        public async Task<int> WaitForAtLeastAsync(int target, CancellationToken ct)
        {
            Waiter waiter;
            lock (_sync)
            {
                if (_count >= target) return _count;
                waiter = new Waiter(target);
                _waiters.Add(waiter);
            }
            try
            {
                return await waiter.Tcs.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lock (_sync) _waiters.Remove(waiter);
                throw new Xunit.Sdk.XunitException(
                    $"Timed out waiting for at least {target} in-VM smoke sweeps; observed {SweepCount}.");
            }
        }

        private sealed class Waiter
        {
            public Waiter(int target) => Target = target;

            public int Target { get; }
            public TaskCompletionSource<int> Tcs { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class DelayTrackingTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private readonly ControllableTimeProvider _inner = new();
        private readonly List<Waiter> _waiters = [];
        private int _timerCount;

        public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;
        public override long TimestampFrequency => _inner.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

        public override long GetTimestamp() => _inner.GetTimestamp();

        public void Advance(TimeSpan delta) => _inner.Advance(delta);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _inner.CreateTimer(callback, state, dueTime, period);
            lock (_sync)
            {
                _timerCount++;
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    var waiter = _waiters[i];
                    if (_timerCount < waiter.Target) continue;
                    _waiters.RemoveAt(i);
                    waiter.Tcs.TrySetResult(_timerCount);
                }
            }

            return timer;
        }

        public async Task<int> WaitForTimerCountAsync(int target, CancellationToken ct)
        {
            Waiter waiter;
            lock (_sync)
            {
                if (_timerCount >= target) return _timerCount;
                waiter = new Waiter(target);
                _waiters.Add(waiter);
            }

            try
            {
                return await waiter.Tcs.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lock (_sync) _waiters.Remove(waiter);
                throw new Xunit.Sdk.XunitException(
                    $"Timed out waiting for at least {target} fake-time timers; observed {_timerCount}.");
            }
        }

        private sealed class Waiter
        {
            public Waiter(int target) => Target = target;

            public int Target { get; }
            public TaskCompletionSource<int> Tcs { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class ThrowingGate : IInVmSmokeGate
    {
        public bool Enabled => true;
        public int ThrowCount { get; private set; }

        public Task ProbeAllAsync(CancellationToken ct)
        {
            ThrowCount++;
            throw new InvalidOperationException("sweep failed");
        }

        public Task ProbeAllAsync(InVmSmokeSandboxTarget target, CancellationToken ct) =>
            ProbeAllAsync(ct);

        public Task<AgentAvailability> EnsureAvailableAsync(
            AgentKind kind,
            InVmSmokeSandboxTarget target,
            CancellationToken ct)
            => Task.FromResult(new AgentAvailability(true, null, null));

        public Task<AgentAvailability?> ForceProbeAsync(AgentKind kind, CancellationToken ct)
            => Task.FromResult<AgentAvailability?>(new AgentAvailability(true, null, null));
    }
}
