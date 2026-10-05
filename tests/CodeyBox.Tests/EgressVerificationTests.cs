using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.TartSandboxPlugin;
using Microsoft.Extensions.Logging.Abstractions;
using TestClock = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

/// <summary>
/// Host-owned per-sandbox egress verification: an opted-in kind with a
/// passing canary serves profiled work; anything else stays
/// <see cref="EgressEnforcementLocation.NotEnforced"/>. Failures dispose
/// the sandbox, demote the kind for the cool-down, alert loudly, and
/// re-place elsewhere. Fakes drive argv, probe results, and the clock —
/// no Mac, no network.
/// </summary>
public sealed class EgressVerificationTests
{
    private static EgressVerificationOptions VerificationOptions(
        TimeSpan? reverifyInterval = null) => new()
        {
            Kinds = ["tart"],
            AllowedHost = "192.0.2.10",
            AllowedPort = 443,
            BlockedHost = "198.51.100.7",
            BlockedPort = 443,
            Ipv6Host = "2001:db8::1",
            Ipv6Port = 443,
            LanHost = "192.168.1.2",
            LanPort = 22,
            PerCheckTimeout = TimeSpan.FromSeconds(2),
            Cooldown = TimeSpan.FromMinutes(15),
            ReverifyInterval = reverifyInterval ?? TimeSpan.Zero,
            MaxProbeOutputBytes = 4096,
        };

    private static EgressVerificationOptions TwoKindOptions(
        TimeSpan? reverifyInterval = null)
    {
        var options = VerificationOptions(reverifyInterval);
        options.Kinds.Add("tart2");
        return options;
    }

    private static SandboxSpec ProfiledSpec() => new()
    {
        ImageReference = "test-image",
        Network = new SandboxNetworkPolicy { ProfileName = "llm", AllowedHosts = ["example.com"] },
    };

    private static SandboxPlacementAcquisition ProfiledAcquisition(SandboxSpec? spec = null) =>
        new(WorkItemId.New(), "work", [], null, "llm", spec ?? ProfiledSpec());

    private static SandboxPlacementAcquirer BuildAcquirer(
        SandboxClassesSnapshot snapshot,
        ISandboxProviderRegistry registry,
        EgressVerificationOptions options,
        TestClock clock,
        RecordingVerificationSink sink,
        EgressVerificationGate? gate = null)
    {
        gate ??= new EgressVerificationGate(() => options, clock);
        return new SandboxPlacementAcquirer(
            snapshot,
            registry,
            verificationOptionsAccessor: () => options,
            verificationGate: gate,
            verificationSink: sink,
            clock: clock);
    }

    [Fact]
    public async Task OptedInKind_PassingCanary_PlacesAndRunsThere()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var options = VerificationOptions();
        var sink = new RecordingVerificationSink();
        var tart = new CanaryFakeSandboxProvider("tart");
        var acquirer = BuildAcquirer(
            SandboxPlacementTestMembers.Snapshot(SandboxPlacementTestMembers.Member("tart-member", "tart")),
            new PlacementFakeSandboxProviderRegistry([tart]),
            options, clock, sink);

        await using var sandbox = await acquirer.AcquireAsync(ProfiledAcquisition(), CancellationToken.None);

        Assert.IsType<VerifiedEgressSandbox>(sandbox);
        var work = await sandbox.ExecAsync(
            new SandboxExec { Argv = ["echo", "hello"] }, CancellationToken.None);
        Assert.True(work.Success);
        Assert.Equal("work-output", work.Stdout);

        Assert.Equal(1, tart.CreateCount);
        var created = Assert.Single(tart.Specs);
        Assert.Null(created.Network.ProfileName);
        Assert.Equal(["example.com"], created.Network.AllowedHosts);

        var evt = Assert.Single(sink.Events);
        Assert.True(evt.Passed);
        Assert.False(evt.Alert);
        Assert.Equal(4, evt.Checks.Count);
        Assert.All(evt.Checks, static c => Assert.True(c.Passed));

        Assert.Equal(4, tart.Created[0].ProbeArgvs.Count);
        Assert.Equal(
            [EgressCanaryVerifier.CheckAllow, EgressCanaryVerifier.CheckBlockCanary,
                EgressCanaryVerifier.CheckBlockIpv6, EgressCanaryVerifier.CheckBlockLan],
            tart.Created[0].ProbeArgvs.Select(static a => a[3]).ToList());

        Assert.Equal(EgressEnforcementLocation.NotEnforced, HostPlatformSupport.GetEgressEnforcement("tart"));
        Assert.False(SandboxEgressPolicy.IsEnforced("tart"));
        Assert.Equal(
            EgressEnforcementLocation.EnforcedOnProviderHostVerified,
            SandboxEgressPolicy.EffectiveEnforcement("tart", new EgressVerificationGate(() => options, clock)));
    }

    [Fact]
    public async Task NotOptedIn_PassingCanary_StaysNotEnforced()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var options = VerificationOptions();
        options.Kinds.Clear();
        var sink = new RecordingVerificationSink();
        var tart = new CanaryFakeSandboxProvider("tart");
        var gate = new EgressVerificationGate(() => options, clock);
        var acquirer = BuildAcquirer(
            SandboxPlacementTestMembers.Snapshot(SandboxPlacementTestMembers.Member("tart-member", "tart")),
            new PlacementFakeSandboxProviderRegistry([tart]),
            options, clock, sink, gate);

        var ex = await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(() =>
            acquirer.AcquireAsync(ProfiledAcquisition(), CancellationToken.None));

        Assert.Equal("enforced-egress", ex.UnmetCapability);
        Assert.Equal(0, tart.CreateCount);
        Assert.Empty(sink.Events);
        Assert.False(gate.IsKindEligible("tart"));
        Assert.Equal(EgressEnforcementLocation.NotEnforced, SandboxEgressPolicy.EffectiveEnforcement("tart", gate));
    }

    [Theory]
    [InlineData(EgressCanaryVerifier.CheckBlockCanary)]
    [InlineData(EgressCanaryVerifier.CheckBlockIpv6)]
    [InlineData(EgressCanaryVerifier.CheckBlockLan)]
    public async Task FailingBlockCheck_DisposesDemotesReplacesAndAlerts(string failingCheck)
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var options = TwoKindOptions();
        var sink = new RecordingVerificationSink();
        var gate = new EgressVerificationGate(() => options, clock);
        var probeExits = CanaryFakeSandbox.PassingExits();
        probeExits[failingCheck] = 0;
        var tart = new CanaryFakeSandboxProvider("tart", probeExits);
        var tart2 = new CanaryFakeSandboxProvider("tart2");
        var acquirer = BuildAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("tart-member", "tart", preferenceScore: 100),
                SandboxPlacementTestMembers.Member("tart2-member", "tart2", preferenceScore: 90)),
            new PlacementFakeSandboxProviderRegistry([tart, tart2]),
            options, clock, sink, gate);

        await using var sandbox = await acquirer.AcquireAsync(ProfiledAcquisition(), CancellationToken.None);

        Assert.Equal(1, tart.CreateCount);
        Assert.Equal(1, tart2.CreateCount);
        var failedSandbox = Assert.Single(tart.Created);
        Assert.True(failedSandbox.Disposed);
        Assert.Equal(4, failedSandbox.ProbeArgvs.Count);
        Assert.True(gate.IsDemoted("tart"));
        Assert.NotNull(gate.DemotedUntil("tart"));
        Assert.False(gate.IsDemoted("tart2"));

        Assert.Equal(2, sink.Events.Count);
        var alert = Assert.Single(sink.Events, e => !e.Passed);
        Assert.True(alert.Alert);
        Assert.Equal("tart", alert.ProviderKind);
        Assert.Equal("work", alert.Phase);
        var failed = Assert.Single(alert.Checks, c => string.Equals(c.Name, failingCheck, StringComparison.Ordinal));
        Assert.False(failed.Passed);
        var pass = Assert.Single(sink.Events, e => e.Passed);
        Assert.Equal("tart2", pass.ProviderKind);
        Assert.False(pass.Alert);

        var work = await sandbox.ExecAsync(
            new SandboxExec { Argv = ["echo", "hello"] }, CancellationToken.None);
        Assert.True(work.Success);
        Assert.Equal("work-output", work.Stdout);
    }

    [Fact]
    public async Task AllowedUnreachable_FailsClosed()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var options = TwoKindOptions();
        var sink = new RecordingVerificationSink();
        var gate = new EgressVerificationGate(() => options, clock);
        var probeExits = CanaryFakeSandbox.PassingExits();
        probeExits[EgressCanaryVerifier.CheckAllow] = 1;
        var tart = new CanaryFakeSandboxProvider("tart", probeExits);
        var tart2 = new CanaryFakeSandboxProvider("tart2");
        var acquirer = BuildAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("tart-member", "tart", preferenceScore: 100),
                SandboxPlacementTestMembers.Member("tart2-member", "tart2", preferenceScore: 90)),
            new PlacementFakeSandboxProviderRegistry([tart, tart2]),
            options, clock, sink, gate);

        await using var sandbox = await acquirer.AcquireAsync(ProfiledAcquisition(), CancellationToken.None);

        Assert.Equal(1, tart2.CreateCount);
        Assert.True(gate.IsDemoted("tart"));
        var alert = Assert.Single(sink.Events, e => !e.Passed);
        Assert.True(alert.Alert);
        Assert.True((await sandbox.ExecAsync(new SandboxExec { Argv = ["echo", "hi"] }, CancellationToken.None)).Success);
    }

    [Fact]
    public async Task DemotedKind_ExcludedAndStaticEnforcedServes()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var options = VerificationOptions();
        var sink = new RecordingVerificationSink();
        var gate = new EgressVerificationGate(() => options, clock);
        gate.RecordFailure("tart");
        var tart = new CanaryFakeSandboxProvider("tart");
        var incus = new PlacementFakeSandboxProvider("incus");
        var acquirer = BuildAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("tart-member", "tart", preferenceScore: 100),
                SandboxPlacementTestMembers.Member("incus-member", "incus", preferenceScore: 10)),
            new PlacementFakeSandboxProviderRegistry([tart, incus]),
            options, clock, sink, gate);

        await using var sandbox = await acquirer.AcquireAsync(ProfiledAcquisition(), CancellationToken.None);

        Assert.Equal(1, incus.CreateCount);
        Assert.Equal(0, tart.CreateCount);
        Assert.Empty(sink.Events);
        Assert.True((await sandbox.ExecAsync(new SandboxExec { Argv = ["echo", "hi"] }, CancellationToken.None)).Success);
    }

    [Fact]
    public async Task CooldownExpiry_RestoresEligibilityOnlyAfterFreshPassingCanary()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var options = VerificationOptions();
        var sink = new RecordingVerificationSink();
        var gate = new EgressVerificationGate(() => options, clock);
        var probeExits = CanaryFakeSandbox.PassingExits();
        probeExits[EgressCanaryVerifier.CheckBlockLan] = 0;
        var tart = new CanaryFakeSandboxProvider("tart", probeExits);
        var acquirer = BuildAcquirer(
            SandboxPlacementTestMembers.Snapshot(SandboxPlacementTestMembers.Member("tart-member", "tart")),
            new PlacementFakeSandboxProviderRegistry([tart]),
            options, clock, sink, gate);

        await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(() =>
            acquirer.AcquireAsync(ProfiledAcquisition(), CancellationToken.None));
        Assert.True(gate.IsDemoted("tart"));
        var probesAfterFirst = tart.Created[0].ProbeArgvs.Count;
        Assert.Equal(4, probesAfterFirst);

        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.False(gate.IsDemoted("tart"));
        Assert.True(gate.IsKindEligible("tart"));

        await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(() =>
            acquirer.AcquireAsync(ProfiledAcquisition(), CancellationToken.None));
        Assert.True(gate.IsDemoted("tart"));
        Assert.Equal(2, tart.CreateCount);

        clock.Advance(TimeSpan.FromMinutes(16));
        probeExits[EgressCanaryVerifier.CheckBlockLan] = 1;
        await using var sandbox = await acquirer.AcquireAsync(ProfiledAcquisition(), CancellationToken.None);
        Assert.False(gate.IsDemoted("tart"));
        Assert.True((await sandbox.ExecAsync(new SandboxExec { Argv = ["echo", "hi"] }, CancellationToken.None)).Success);
        Assert.Equal(2, sink.Events.Count(e => !e.Passed && e.Alert));
        Assert.Single(sink.Events, e => e.Passed && !e.Alert);
    }

    [Fact]
    public async Task PeriodicReverify_CatchesFilterThatDiesAfterCreation()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var options = VerificationOptions(reverifyInterval: TimeSpan.FromMinutes(1));
        var sink = new RecordingVerificationSink();
        var gate = new EgressVerificationGate(() => options, clock);
        var probeExits = CanaryFakeSandbox.PassingExits();
        var tart = new CanaryFakeSandboxProvider("tart", probeExits);
        var acquirer = BuildAcquirer(
            SandboxPlacementTestMembers.Snapshot(SandboxPlacementTestMembers.Member("tart-member", "tart")),
            new PlacementFakeSandboxProviderRegistry([tart]),
            options, clock, sink, gate);

        var sandbox = await acquirer.AcquireAsync(ProfiledAcquisition(), CancellationToken.None);
        Assert.True((await sandbox.ExecAsync(new SandboxExec { Argv = ["echo", "one"] }, CancellationToken.None)).Success);
        Assert.Equal(4, tart.Created[0].ProbeArgvs.Count);

        probeExits[EgressCanaryVerifier.CheckBlockLan] = 0;
        clock.Advance(TimeSpan.FromMinutes(2));
        var ex = await Assert.ThrowsAsync<EgressVerificationFailedException>(() =>
            sandbox.ExecAsync(new SandboxExec { Argv = ["echo", "two"] }, CancellationToken.None));

        Assert.Equal("tart", ex.ProviderKind);
        Assert.True(tart.Created[0].Disposed);
        Assert.True(gate.IsDemoted("tart"));
        Assert.Equal(8, tart.Created[0].ProbeArgvs.Count);
        var alert = Assert.Single(sink.Events, e => !e.Passed && e.Alert && string.Equals(e.Phase, "work", StringComparison.Ordinal));
        Assert.Contains(EgressCanaryVerifier.CheckBlockLan, alert.FailureReason ?? string.Empty, StringComparison.Ordinal);
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task DeadFilterProcess_FailsFastWithoutGuestProbes()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var options = VerificationOptions(reverifyInterval: TimeSpan.FromMinutes(1));
        var sink = new RecordingVerificationSink();
        var gate = new EgressVerificationGate(() => options, clock);
        var tart = new CanaryFakeSandboxProvider("tart");
        var acquirer = BuildAcquirer(
            SandboxPlacementTestMembers.Snapshot(SandboxPlacementTestMembers.Member("tart-member", "tart")),
            new PlacementFakeSandboxProviderRegistry([tart]),
            options, clock, sink, gate);

        var sandbox = await acquirer.AcquireAsync(ProfiledAcquisition(), CancellationToken.None);
        tart.Created[0].FilterAlive = false;

        await Assert.ThrowsAsync<EgressVerificationFailedException>(() =>
            sandbox.ExecAsync(new SandboxExec { Argv = ["echo", "hi"] }, CancellationToken.None));

        Assert.Equal(4, tart.Created[0].ProbeArgvs.Count);
        Assert.True(tart.Created[0].Disposed);
        Assert.True(gate.IsDemoted("tart"));
        Assert.Single(sink.Events, e => !e.Passed && e.Alert);
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task UnconfiguredEndpoints_TreatedAsNotEnforcedWithoutDoomedCreates()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var options = VerificationOptions();
        options.AllowedHost = string.Empty;
        options.BlockedHost = string.Empty;
        options.LanHost = string.Empty;
        var sink = new RecordingVerificationSink();
        var gate = new EgressVerificationGate(() => options, clock);
        var tart = new CanaryFakeSandboxProvider("tart");
        var acquirer = BuildAcquirer(
            SandboxPlacementTestMembers.Snapshot(SandboxPlacementTestMembers.Member("tart-member", "tart")),
            new PlacementFakeSandboxProviderRegistry([tart]),
            options, clock, sink, gate);

        await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(() =>
            acquirer.AcquireAsync(ProfiledAcquisition(), CancellationToken.None));

        Assert.Equal(0, tart.CreateCount);
        Assert.Empty(sink.Events);
        Assert.False(gate.IsDemoted("tart"));
        Assert.True(gate.IsKindEligible("tart"));
    }

    [Fact]
    public async Task StaticEnforced_PreferredOverVerified()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var options = VerificationOptions();
        var sink = new RecordingVerificationSink();
        var tart = new CanaryFakeSandboxProvider("tart");
        var incus = new PlacementFakeSandboxProvider("incus");
        var acquirer = BuildAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("tart-member", "tart", preferenceScore: 100),
                SandboxPlacementTestMembers.Member("incus-member", "incus", preferenceScore: 10)),
            new PlacementFakeSandboxProviderRegistry([tart, incus]),
            options, clock, sink);

        await using var sandbox = await acquirer.AcquireAsync(ProfiledAcquisition(), CancellationToken.None);

        Assert.Equal(1, incus.CreateCount);
        Assert.Equal(0, tart.CreateCount);
        Assert.Empty(sink.Events);
        Assert.IsNotType<VerifiedEgressSandbox>(sandbox);
    }

    [Fact]
    public async Task UnprofiledWork_OnOptedInKind_NeedsNoCanary()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var options = VerificationOptions();
        var sink = new RecordingVerificationSink();
        var tart = new CanaryFakeSandboxProvider("tart");
        var acquirer = BuildAcquirer(
            SandboxPlacementTestMembers.Snapshot(SandboxPlacementTestMembers.Member("tart-member", "tart")),
            new PlacementFakeSandboxProviderRegistry([tart]),
            options, clock, sink);

        await using var sandbox = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None);

        Assert.IsNotType<VerifiedEgressSandbox>(sandbox);
        Assert.Empty(tart.Created[0].ProbeArgvs);
        Assert.True((await sandbox.ExecAsync(new SandboxExec { Argv = ["echo", "hi"] }, CancellationToken.None)).Success);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void Gate_DemotionExpiresAndSuccessClears()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var options = VerificationOptions();
        var gate = new EgressVerificationGate(() => options, clock);

        Assert.True(gate.IsKindEligible("tart"));
        var until = gate.RecordFailure("tart");
        Assert.True(gate.IsDemoted("tart"));
        Assert.False(gate.IsKindEligible("tart"));
        Assert.Equal(until, gate.DemotedUntil("tart"));

        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.False(gate.IsDemoted("tart"));
        Assert.True(gate.IsKindEligible("tart"));

        gate.RecordFailure("tart");
        gate.RecordSuccess("tart");
        Assert.False(gate.IsDemoted("tart"));
    }

    [Fact]
    public void Options_Validate_RejectsMisconfiguration()
    {
        var blankKind = VerificationOptions();
        blankKind.Kinds.Add("  ");
        Assert.Throws<InvalidOperationException>(() => blankKind.Validate());

        var badPort = VerificationOptions();
        badPort.AllowedPort = 0;
        Assert.Throws<InvalidOperationException>(() => badPort.Validate());

        var badHost = VerificationOptions();
        badHost.BlockedHost = "https://example.com/x";
        Assert.Throws<InvalidOperationException>(() => badHost.Validate());

        var badTimeout = VerificationOptions();
        badTimeout.PerCheckTimeout = TimeSpan.FromMinutes(5);
        Assert.Throws<InvalidOperationException>(() => badTimeout.Validate());

        var badCooldown = VerificationOptions();
        badCooldown.Cooldown = TimeSpan.Zero;
        Assert.Throws<InvalidOperationException>(() => badCooldown.Validate());
    }

    [Fact]
    public void ProbeExec_ArgvShape_IsStableForFakes()
    {
        var exec = EgressCanaryVerifier.BuildProbeExec(
            EgressCanaryVerifier.CheckBlockLan, "block", "192.168.1.2", 22,
            TimeSpan.FromSeconds(2), 4096);

        Assert.Equal("python3", exec.Argv[0]);
        Assert.Equal("-c", exec.Argv[1]);
        Assert.Equal(EgressCanaryVerifier.ProbeScript, exec.Argv[2]);
        Assert.Equal(EgressCanaryVerifier.CheckBlockLan, exec.Argv[3]);
        Assert.Equal("block", exec.Argv[4]);
        Assert.Equal("192.168.1.2", exec.Argv[5]);
        Assert.Equal("22", exec.Argv[6]);
        Assert.Equal(4096, exec.MaxStdoutBytes);
        Assert.Throws<ArgumentException>(() =>
            EgressCanaryVerifier.BuildProbeExec("x", "sometimes", "h", 80, TimeSpan.FromSeconds(1), 64));
    }

    [Fact]
    public void Policy_Ranking_NeverPrefersVerifiedOverOrchestratorHost()
    {
        Assert.True(
            SandboxEgressPolicy.EnforcementRank(EgressEnforcementLocation.EnforcedOnProviderHostVerified) >
            SandboxEgressPolicy.EnforcementRank(EgressEnforcementLocation.EnforcedOnOrchestratorHost));
        Assert.True(SandboxEgressPolicy.IsEffectivelyEnforced("incus", gate: null));
        Assert.False(SandboxEgressPolicy.IsEffectivelyEnforced("tart", gate: null));
        Assert.False(SandboxEgressPolicy.IsVerificationEligible("tart", gate: null));
        Assert.Contains("NOT enforced", SandboxEgressPolicy.DescribeEgressEnforcement("tart"), StringComparison.Ordinal);

        var clock = new TestClock(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var options = VerificationOptions();
        var gate = new EgressVerificationGate(() => options, clock);
        Assert.Equal(
            EgressEnforcementLocation.EnforcedOnProviderHostVerified,
            SandboxEgressPolicy.EffectiveEnforcement("tart", gate));
        Assert.Contains(
            "never stronger than orchestrator-host enforcement",
            SandboxEgressPolicy.DescribeEffectiveEnforcement("tart", gate),
            StringComparison.Ordinal);
    }
}

/// <summary>Probe-driven sandbox fake: routes canary argv by check name, tracks disposal.</summary>
internal sealed class CanaryFakeSandbox : ISandbox, IEgressFilterHealth
{
    private readonly Dictionary<string, int> _probeExits;

    public CanaryFakeSandbox(string id, Dictionary<string, int>? probeExits = null)
    {
        Id = id;
        _probeExits = probeExits ?? PassingExits();
    }

    public static Dictionary<string, int> PassingExits() => new(StringComparer.Ordinal)
    {
        [EgressCanaryVerifier.CheckAllow] = 0,
        [EgressCanaryVerifier.CheckBlockCanary] = 1,
        [EgressCanaryVerifier.CheckBlockIpv6] = 1,
        [EgressCanaryVerifier.CheckBlockLan] = 1,
    };

    public string Id { get; }

    public List<IReadOnlyList<string>> ProbeArgvs { get; } = [];

    public int WorkExecCount { get; private set; }

    public bool Disposed { get; private set; }

    public bool FilterAlive { get; set; } = true;

    bool IEgressFilterHealth.IsFilterAlive => FilterAlive;

    public void SetProbeExit(string check, int exit) => _probeExits[check] = exit;

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exec);
        if (exec.Argv.Count >= 8
            && string.Equals(exec.Argv[0], "python3", StringComparison.Ordinal)
            && string.Equals(exec.Argv[1], "-c", StringComparison.Ordinal))
        {
            ProbeArgvs.Add(exec.Argv.ToList());
            var exit = _probeExits.TryGetValue(exec.Argv[3], out var configured) ? configured : 2;
            return Task.FromResult(new SandboxExecResult(exit, string.Empty, exit == 2 ? "probe error" : string.Empty));
        }
        WorkExecCount++;
        return Task.FromResult(new SandboxExecResult(0, "work-output", string.Empty));
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Provider fake handing out <see cref="CanaryFakeSandbox"/> instances and recording specs.</summary>
internal sealed class CanaryFakeSandboxProvider : ISandboxProvider
{
    private readonly Func<CanaryFakeSandbox> _factory;
    private readonly List<SandboxSpec> _specs = new();
    private readonly List<CanaryFakeSandbox> _created = new();

    public CanaryFakeSandboxProvider(string name, Dictionary<string, int>? probeExits = null)
    {
        Name = name;
        _factory = () => new CanaryFakeSandbox(name + "-sandbox-" + (_created.Count + 1), probeExits);
    }

    public string Name { get; }

    public IReadOnlyList<string> DeclaredCapabilities => [];

    public IReadOnlyList<SandboxSpec> Specs
    {
        get { lock (_specs) return _specs.ToList(); }
    }

    public IReadOnlyList<CanaryFakeSandbox> Created
    {
        get { lock (_created) return _created.ToList(); }
    }

    public int CreateCount
    {
        get { lock (_specs) return _specs.Count; }
    }

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var sandbox = _factory();
        lock (_specs)
        {
            _specs.Add(spec);
            _created.Add(sandbox);
        }
        return Task.FromResult<ISandbox>(sandbox);
    }

    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

    public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Recording event sink for verification tests.</summary>
internal sealed class RecordingVerificationSink : IEgressVerificationEventSink
{
    private readonly List<EgressVerificationEvent> _events = new();

    public IReadOnlyList<EgressVerificationEvent> Events
    {
        get { lock (_events) return _events.ToList(); }
    }

    public Task RecordAsync(EgressVerificationEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        lock (_events)
            _events.Add(evt);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The Tart provider's filter-health signal: Softnet mode reports the
/// <c>tart run</c> process liveness, NAT reports alive, and suspend detaches
/// the spent handle so a later resume is not condemned by stale state.
/// </summary>
public sealed class TartFilterHealthTests
{
    private static TartSandbox NewSandbox(TartNetworkMode mode, ITartDetachedProcess? process)
    {
        var runner = new FakeTartProcessRunner();
        var options = new TartSandboxOptions { Enabled = true };
        var transport = new TartSshGuestTransport(runner, () => options, () => "test-password");
        var sandbox = new TartSandbox(
            "codeybox-test",
            "192.0.2.11",
            runner,
            transport,
            () => options,
            new SandboxSpec { ImageReference = "test-image", WorkingDirectory = "/work" },
            TimeProvider.System,
            NullLogger.Instance,
            _ => { });
        sandbox.SetNetworkPolicy(mode, mode == TartNetworkMode.Softnet ? ["192.168.64.1/32"] : []);
        sandbox.AttachVmProcess(process);
        return sandbox;
    }

    [Fact]
    public void Softnet_RunningProcess_ReportsAlive()
    {
        var sandbox = NewSandbox(TartNetworkMode.Softnet, new ControllableDetachedProcess(exited: false));
        Assert.True(((IEgressFilterHealth)sandbox).IsFilterAlive);
    }

    [Fact]
    public void Softnet_ExitedProcess_ReportsDead()
    {
        var sandbox = NewSandbox(TartNetworkMode.Softnet, new ControllableDetachedProcess(exited: true));
        Assert.False(((IEgressFilterHealth)sandbox).IsFilterAlive);
    }

    [Fact]
    public void Nat_ExitedProcess_ReportsAlive()
    {
        var sandbox = NewSandbox(TartNetworkMode.Nat, new ControllableDetachedProcess(exited: true));
        Assert.True(((IEgressFilterHealth)sandbox).IsFilterAlive);
    }

    [Fact]
    public void Softnet_NoProcess_ReportsAlive()
    {
        var sandbox = NewSandbox(TartNetworkMode.Softnet, process: null);
        Assert.True(((IEgressFilterHealth)sandbox).IsFilterAlive);
    }

    [Fact]
    public async Task Softnet_Suspend_DetachesSpentHandle()
    {
        var sandbox = NewSandbox(TartNetworkMode.Softnet, new ControllableDetachedProcess(exited: true));
        Assert.False(((IEgressFilterHealth)sandbox).IsFilterAlive);

        await sandbox.SuspendAsync(CancellationToken.None);

        Assert.True(((IEgressFilterHealth)sandbox).IsFilterAlive);
        await sandbox.DisposeAsync();
    }

    private sealed class ControllableDetachedProcess(bool exited) : ITartDetachedProcess
    {
        public int Id => 4242;

        public bool HasExited { get; set; } = exited;

        public void Kill() => HasExited = true;

        public void Dispose() { }
    }
}
