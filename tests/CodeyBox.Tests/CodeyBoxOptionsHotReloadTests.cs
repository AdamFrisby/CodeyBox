using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies hot-reload propagation for the per-consumer paths that were
/// migrated off <c>IOptions&lt;CodeyBoxOptions&gt;.Value</c> in the audit:
///
/// <list type="bullet">
/// <item><see cref="SandboxLeakReaper"/> with a <c>Func&lt;SandboxLeakOptions&gt;</c>
///   accessor must observe per-sweep edits to thresholds and policy fields.</item>
/// <item><see cref="AuditReportRetentionService"/> with a <c>Func&lt;int&gt;</c>
///   accessor must observe per-sweep edits to <c>AuditLog.RetainedDays</c>.</item>
/// <item><see cref="AgentCostCalculator.ApplyConfigReload"/> must swap the held
///   pricing snapshot so subsequent calls use the new rates, and reject negative
///   rates without mutating the prior snapshot.</item>
/// </list>
///
/// Tests use real propagation through the consumer's published surface — no
/// SUT mocks are introduced just to pass.
/// </summary>
public sealed class CodeyBoxOptionsHotReloadTests
{
    // ── SandboxLeakReaper: live-accessor for threshold/policy fields ────────

    [Fact]
    public async Task SandboxLeakReaper_AccessorReadsLatestThresholdOnEachSweep()
    {
        var provider = new FakeSandboxProvider();
        var fiveMinAgo = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
        provider.AddSandbox(new ManagedSandboxInfo(
            "codeybox-hotreload00000", fiveMinAgo, DiskBytes: null, IsTrackedActive: false));

        // First sweep: threshold = 60 min → 5-min-old sandbox is too young to be a leak.
        var opts = new SandboxLeakOptions
        {
            Enabled = true,
            CheckInterval = TimeSpan.FromHours(1),
            LeakAgeThreshold = TimeSpan.FromMinutes(60),
            AutoDispose = false,
            MaxConcurrentAutoDispose = 4,
        };
        var reaper = new SandboxLeakReaper(
            provider,
            new NullWebhookDispatcher(),
            () => opts,
            NullLogger<SandboxLeakReaper>.Instance);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestLeaks());

        // Drop the threshold below the sandbox age. The accessor returns the
        // mutated reference on the next sweep — without the live accessor the
        // reaper would still gate against the prior 60-min threshold.
        opts.LeakAgeThreshold = TimeSpan.FromMinutes(1);

        await reaper.RunSweepAsync(CancellationToken.None);
        var leak = Assert.Single(reaper.GetLatestLeaks());
        Assert.Equal("codeybox-hotreload00000", leak.Name);
    }

    [Fact]
    public async Task SandboxLeakReaper_AccessorReadsLatestAutoDisposeFlag()
    {
        var threshold = TimeSpan.FromMinutes(30);
        var oldEnough = DateTimeOffset.UtcNow - threshold - TimeSpan.FromMinutes(1);
        var provider = new FakeSandboxProvider();
        provider.AddSandbox(new ManagedSandboxInfo(
            "codeybox-autodispose0", oldEnough, DiskBytes: null, IsTrackedActive: false));

        var opts = new SandboxLeakOptions
        {
            Enabled = true,
            CheckInterval = TimeSpan.FromHours(1),
            LeakAgeThreshold = threshold,
            AutoDispose = false,
            MaxConcurrentAutoDispose = 4,
        };
        var reaper = new SandboxLeakReaper(
            provider,
            new NullWebhookDispatcher(),
            () => opts,
            NullLogger<SandboxLeakReaper>.Instance);

        // First sweep with AutoDispose=false: leak is recorded but no dispose call.
        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Single(reaper.GetLatestLeaks());
        Assert.Empty(provider.DisposedNames);

        // Flip AutoDispose on. The accessor returns the mutated value on the
        // next sweep — the previously-detected leak now gets disposed.
        opts.AutoDispose = true;
        provider.AddSandbox(new ManagedSandboxInfo(
            "codeybox-autodispose0", oldEnough, DiskBytes: null, IsTrackedActive: false));

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Contains("codeybox-autodispose0", provider.DisposedNames);
    }

    // ── AuditReportRetentionService: live-accessor for RetainedDays ─────────

    [Fact]
    public async Task AuditReportRetentionService_AccessorReadsLatestRetainedDaysEachSweep()
    {
        // Two rows: one ~10 days old, one ~40 days old.
        var store = new FakeAuditReportStore();
        var now = DateTimeOffset.UtcNow;
        store.Add(now - TimeSpan.FromDays(10));
        store.Add(now - TimeSpan.FromDays(40));

        var retainedDays = 60;

        // Use reflection to invoke the private sweep helper, since
        // ExecuteAsync is on a 1-day PeriodicTimer and we want deterministic
        // per-sweep behaviour. The Func accessor is exercised exactly once
        // per sweep.
        var service = new AuditReportRetentionService(
            store,
            () => retainedDays,
            NullLogger<AuditReportRetentionService>.Instance);

        // Sweep 1: retain 60 days → nothing is deleted (40 < 60).
        await InvokeSweepAsync(service, CancellationToken.None);
        Assert.Equal(2, store.Count);

        // Operator edit: tighten retention to 30 days. Next sweep deletes
        // the 40-day row only.
        retainedDays = 30;
        await InvokeSweepAsync(service, CancellationToken.None);
        Assert.Equal(1, store.Count);
        Assert.True(store.AllStartedAt.All(d => d > now - TimeSpan.FromDays(30)));
    }

    private static Task InvokeSweepAsync(AuditReportRetentionService service, CancellationToken ct)
    {
        var method = typeof(AuditReportRetentionService)
            .GetMethod("RunSweepAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (Task)method!.Invoke(service, new object[] { ct })!;
    }

    private sealed class FakeAuditReportStore : IAuditReportStore
    {
        private readonly List<DateTimeOffset> _startedAt = new();

        public void Add(DateTimeOffset startedAt) => _startedAt.Add(startedAt);
        public int Count => _startedAt.Count;
        public IReadOnlyList<DateTimeOffset> AllStartedAt => _startedAt;

        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        {
            var removed = _startedAt.RemoveAll(d => d < cutoff);
            return Task.FromResult(removed);
        }

        public Task CreateAsync(AuditReport report, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<AuditReport>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<string?> GetRawOutputAsync(string workItemId, int iteration, string auditorName, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    // ── AgentCostCalculator.ApplyConfigReload ───────────────────────────────

    [Fact]
    public void AgentCostCalculator_ApplyConfigReload_SwapsRatesForSubsequentCalls()
    {
        var initial = new AgentPricingOptions
        {
            Rates = new()
            {
                ["claude"] = new()
                {
                    ["claude-opus-4-7"] = new ModelRateConfig
                    {
                        InputPerMillion = 15.0,
                        CachedInputPerMillion = 1.50,
                        OutputPerMillion = 75.0,
                    },
                },
            },
        };
        var calculator = new AgentCostCalculator(initial);
        var snapshot = new AgentCostSnapshot(
            InputTokens: 1000, CachedInputTokens: 0, OutputTokens: 1000, ModelId: "claude-opus-4-7");

        // Initial pricing: 1000 * 15.0 / 1e6 + 1000 * 75.0 / 1e6 = 0.015 + 0.075 = 0.090
        Assert.Equal(0.090000m, calculator.Calculate(snapshot, AgentKind.Claude));

        var doubled = new AgentPricingOptions
        {
            Rates = new()
            {
                ["claude"] = new()
                {
                    ["claude-opus-4-7"] = new ModelRateConfig
                    {
                        InputPerMillion = 30.0,
                        CachedInputPerMillion = 3.00,
                        OutputPerMillion = 150.0,
                    },
                },
            },
        };
        calculator.ApplyConfigReload(doubled);

        // After reload, same call should now cost 2x.
        Assert.Equal(0.180000m, calculator.Calculate(snapshot, AgentKind.Claude));
    }

    [Fact]
    public void AgentCostCalculator_ApplyConfigReload_RejectsNegativeRates_KeepsPrior()
    {
        var initial = new AgentPricingOptions
        {
            Rates = new()
            {
                ["claude"] = new()
                {
                    ["claude-opus-4-7"] = new ModelRateConfig
                    {
                        InputPerMillion = 15.0,
                        CachedInputPerMillion = 1.50,
                        OutputPerMillion = 75.0,
                    },
                },
            },
        };
        var calculator = new AgentCostCalculator(initial);
        var snapshot = new AgentCostSnapshot(
            InputTokens: 1000, CachedInputTokens: 0, OutputTokens: 1000, ModelId: "claude-opus-4-7");
        var priorCost = calculator.Calculate(snapshot, AgentKind.Claude);

        var bad = new AgentPricingOptions
        {
            Rates = new()
            {
                ["claude"] = new()
                {
                    ["claude-opus-4-7"] = new ModelRateConfig
                    {
                        InputPerMillion = -1.0,
                        CachedInputPerMillion = 0,
                        OutputPerMillion = 75.0,
                    },
                },
            },
        };
        Assert.Throws<InvalidOperationException>(() => calculator.ApplyConfigReload(bad));

        // Prior rate must still be in effect after the rejected reload.
        Assert.Equal(priorCost, calculator.Calculate(snapshot, AgentKind.Claude));
    }

    [Fact]
    public void AgentCostCalculator_ApplyConfigReload_RejectsNegativeDefaultRate_KeepsPrior()
    {
        // Exercises the second validation loop in ApplyConfigReload — the one
        // that scans next.DefaultRates rather than next.Rates. A regression
        // that inverted the comparison, dropped the loop, or copy-pasted the
        // wrong field would let a negative default rate swap in silently.
        var initial = new AgentPricingOptions
        {
            DefaultRates = new()
            {
                ["claude"] = new ModelRateConfig
                {
                    InputPerMillion = 3.0,
                    CachedInputPerMillion = 0.30,
                    OutputPerMillion = 15.0,
                },
            },
        };
        var calculator = new AgentCostCalculator(initial);
        var snapshot = new AgentCostSnapshot(
            InputTokens: 1000, CachedInputTokens: 0, OutputTokens: 1000, ModelId: "model-not-in-rates");
        // Falls through to DefaultRates: 1000*3/1e6 + 1000*15/1e6 = 0.018
        var priorCost = calculator.Calculate(snapshot, AgentKind.Claude);
        Assert.Equal(0.018000m, priorCost);

        var bad = new AgentPricingOptions
        {
            DefaultRates = new()
            {
                ["claude"] = new ModelRateConfig
                {
                    InputPerMillion = 3.0,
                    CachedInputPerMillion = 0.30,
                    OutputPerMillion = -1.0,
                },
            },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => calculator.ApplyConfigReload(bad));
        Assert.Contains("default rate", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Prior default rate must still be in effect after the rejected reload.
        Assert.Equal(priorCost, calculator.Calculate(snapshot, AgentKind.Claude));
    }

    [Fact]
    public void AgentCostCalculator_ApplyConfigReload_AddsRateForPreviouslyUnknownAgent()
    {
        // Initial config has no rate for codex; calculator returns 0 for codex calls.
        var initial = new AgentPricingOptions
        {
            Rates = new()
            {
                ["claude"] = new()
                {
                    ["claude-opus-4-7"] = new ModelRateConfig
                    {
                        InputPerMillion = 15.0,
                        CachedInputPerMillion = 0,
                        OutputPerMillion = 75.0,
                    },
                },
            },
        };
        var calculator = new AgentCostCalculator(initial);
        var codexSnap = new AgentCostSnapshot(
            InputTokens: 1000, CachedInputTokens: 0, OutputTokens: 1000, ModelId: "gpt-5");

        Assert.Equal(0m, calculator.Calculate(codexSnap, AgentKind.Codex));

        // Operator adds Codex pricing via hot-reload.
        var withCodex = new AgentPricingOptions
        {
            Rates = new()
            {
                ["codex"] = new()
                {
                    ["gpt-5"] = new ModelRateConfig
                    {
                        InputPerMillion = 5.0,
                        CachedInputPerMillion = 0,
                        OutputPerMillion = 25.0,
                    },
                },
            },
        };
        calculator.ApplyConfigReload(withCodex);

        // 1000 * 5.0 / 1e6 + 1000 * 25.0 / 1e6 = 0.005 + 0.025 = 0.030
        Assert.Equal(0.030000m, calculator.Calculate(codexSnap, AgentKind.Codex));
    }
}
