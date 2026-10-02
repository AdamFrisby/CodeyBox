using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Per-provider scoping for leak disposal verification
/// (<see cref="CompositeManagedSandboxProvider"/>): a provider that cannot
/// verify its full inventory defers only disposals it might own, never the
/// healthy providers' leaks.
/// </summary>
public sealed class SandboxLeakReaperPerProviderIsolationTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(30);

    private static DateTimeOffset OldEnough() =>
        DateTimeOffset.UtcNow - Threshold - TimeSpan.FromMinutes(1);

    private static ManagedSandboxInfo Untracked(string name) =>
        new(name, OldEnough(), DiskBytes: null, IsTrackedActive: false);

    private static SandboxLeakReaper BuildReaper(
        IManagedSandboxLifecycle provider,
        bool autoDispose,
        IWebhookDispatcher? webhooks = null)
    {
        var opts = new SandboxLeakOptions
        {
            Enabled = true,
            CheckInterval = TimeSpan.FromHours(1),
            LeakAgeThreshold = Threshold,
            AutoDispose = autoDispose,
        };
        return new SandboxLeakReaper(
            provider,
            webhooks ?? new NullWebhookDispatcher(),
            opts,
            NullLogger<SandboxLeakReaper>.Instance);
    }

    [Fact]
    public async Task HealthyProviderLeak_DisposedWhileUnrelatedProviderInventoryFails()
    {
        var healthy = new FakeSandboxProvider();
        var failing = new FakeSandboxProvider();
        healthy.AddSandbox(Untracked("codeybox-healthy-leak0000"));
        failing.InventoryFailure = new InvalidOperationException("executor host unreachable");
        var composite = new CompositeManagedSandboxProvider([healthy, failing]);
        var webhooks = new CapturingWebhookDispatcher();
        var reaper = BuildReaper(composite, autoDispose: true, webhooks);

        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Contains("codeybox-healthy-leak0000", healthy.DisposedNames);
        Assert.Empty(reaper.GetLatestLeaks());
        Assert.DoesNotContain(webhooks.Events, e => e.Event == "sandbox.leak_dispose_failed");
    }

    [Fact]
    public async Task AmbiguousOwnership_BlockedWhilePreviouslyReportingProviderFails()
    {
        const string name = "codeybox-shared-ambiguous0";
        var first = new FakeSandboxProvider();
        var second = new FakeSandboxProvider();
        first.AddSandbox(Untracked(name));
        second.AddSandbox(Untracked(name));
        var composite = new CompositeManagedSandboxProvider([first, second]);

        // Baseline sweep while both are healthy records that both providers
        // reported this name.
        var idle = BuildReaper(composite, autoDispose: false);
        await idle.RunSweepAsync(CancellationToken.None);
        Assert.NotEmpty(idle.GetLatestLeaks());

        second.InventoryFailure = new InvalidOperationException("executor host unreachable");
        var webhooks = new CapturingWebhookDispatcher();
        var reaper = BuildReaper(composite, autoDispose: true, webhooks);
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(first.DisposedNames);
        Assert.Empty(second.DisposedNames);
        Assert.NotEmpty(reaper.GetLatestLeaks());
        Assert.DoesNotContain(webhooks.Events, e => e.Event == "sandbox.leak_dispose_failed");
    }

    [Fact]
    public async Task UnrelatedHistory_DoesNotBlockOtherNames()
    {
        var first = new FakeSandboxProvider();
        var second = new FakeSandboxProvider();
        first.AddSandbox(Untracked("codeybox-first-leak00000"));
        second.AddSandbox(Untracked("codeybox-second-sandbox0"));
        var composite = new CompositeManagedSandboxProvider([first, second]);

        var idle = BuildReaper(composite, autoDispose: false);
        await idle.RunSweepAsync(CancellationToken.None);

        second.InventoryFailure = new InvalidOperationException("executor host unreachable");
        var reaper = BuildReaper(composite, autoDispose: true);
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Contains("codeybox-first-leak00000", first.DisposedNames);
        Assert.Empty(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task UnscopedDispose_FailingProviderBlocksOnlyNamesItMightOwn()
    {
        var owner = new FakeSandboxProvider();
        var failing = new FakeSandboxProvider();
        owner.AddSandbox(Untracked("codeybox-unscoped-00000"));
        failing.InventoryFailure = new InvalidOperationException("executor host unreachable");
        var composite = new CompositeManagedSandboxProvider([owner, failing]);

        var blocked = await Assert.ThrowsAsync<SandboxInventoryVerificationException>(
            () => composite.DisposeLeakedAsync("codeybox-unscoped-00000", CancellationToken.None));
        Assert.Contains("could not verify", blocked.Message, StringComparison.Ordinal);
        Assert.Empty(owner.DisposedNames);

        failing.MightOwnFunc = static (_, _) => false;
        await composite.DisposeLeakedAsync("codeybox-unscoped-00000", CancellationToken.None);

        Assert.Contains("codeybox-unscoped-00000", owner.DisposedNames);
    }

    [Fact]
    public async Task UnreferencedProvider_SkippedAndGeneratesNoFailures()
    {
        var used = new FakeSandboxProvider();
        var unused = new FakeSandboxProvider();
        used.AddSandbox(Untracked("codeybox-used-leak000000"));
        unused.AddSandbox(Untracked("codeybox-unused-leak0000"));
        unused.InventoryFailure = new InvalidOperationException("executor host unreachable");
        var composite = new CompositeManagedSandboxProvider(
            [used, unused],
            shouldInventory: lifecycle => !ReferenceEquals(lifecycle, unused));

        var listed = await composite.ListAllManagedAsync(CancellationToken.None);

        Assert.Equal(["codeybox-used-leak000000"], listed.Select(static info => info.Name).ToArray());

        var reaper = BuildReaper(composite, autoDispose: true);
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Contains("codeybox-used-leak000000", used.DisposedNames);
        Assert.Empty(reaper.GetLatestLeaks());
        Assert.Equal(0, unused.InventoryListCalls);
    }

    [Fact]
    public async Task InventoryFailures_WarnOncePerInterval()
    {
        const string name = "codeybox-partial-block0000";
        var owner = new FakeSandboxProvider();
        var partial = new FakeSandboxProvider
        {
            InventoryIncomplete = true,
        };
        owner.AddSandbox(Untracked(name));
        partial.AddSandbox(Untracked(name));
        var log = new ListLogger<CompositeManagedSandboxProvider>();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var composite = new CompositeManagedSandboxProvider(
            [owner, partial],
            shouldInventory: null,
            () => new SandboxLeakOptions
            {
                InventoryFailureWarningInterval = TimeSpan.FromMinutes(15),
            },
            log,
            time);

        var listed = await composite.ListAllManagedAsync(CancellationToken.None);
        var snapshot = listed.First(info => string.Equals(info.LifecycleProviderId, "fake", StringComparison.Ordinal));

        // The sweep already warned once for the partial provider; repeated
        // deferred disposals stay quiet until the interval elapses.
        Assert.Single(log.VerificationWarnings);
        await Assert.ThrowsAsync<SandboxInventoryVerificationException>(
            () => composite.DisposeLeakedAsync(snapshot, CancellationToken.None));
        await Assert.ThrowsAsync<SandboxInventoryVerificationException>(
            () => composite.DisposeLeakedAsync(snapshot, CancellationToken.None));
        Assert.Single(log.VerificationWarnings);

        time.Advance(TimeSpan.FromMinutes(16));
        await Assert.ThrowsAsync<SandboxInventoryVerificationException>(
            () => composite.DisposeLeakedAsync(snapshot, CancellationToken.None));
        Assert.Equal(2, log.VerificationWarnings.Count);

        Assert.Empty(owner.DisposedNames);
        Assert.Empty(partial.DisposedNames);
    }

    [Fact]
    public void CompositeMightOwn_ReflectsInventoriedChildren()
    {
        var first = new FakeSandboxProvider { MightOwnFunc = static (_, _) => false };
        var second = new FakeSandboxProvider { MightOwnFunc = static (_, _) => false };
        var composite = new CompositeManagedSandboxProvider([first, second]);

        Assert.False(composite.MightOwnSandbox("codeybox-anything0000", hostId: null));

        second.MightOwnFunc = static (_, _) => true;

        Assert.True(composite.MightOwnSandbox("codeybox-anything0000", hostId: null));
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> VerificationWarnings { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                VerificationWarnings.Add(formatter(state, exception));
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
