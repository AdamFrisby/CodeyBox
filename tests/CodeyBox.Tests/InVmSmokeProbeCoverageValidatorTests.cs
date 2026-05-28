using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CodeyBox.Agents.Claude;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="InVmSmokeProbeCoverageValidator"/> — the startup guard
/// for AC#1: an AgentClass member with no registered in-VM smoke probe must be
/// benched (routed past) at smoke time, not discovered at first dispatch. Covers
/// the uncovered-member bench, the covered-member silent path, the exempt list,
/// and the warning-only fallback when the prober is inactive.
/// </summary>
public sealed class InVmSmokeProbeCoverageValidatorTests
{
    private static readonly AgentKind Cursor = AgentKind.Cursor;
    private static readonly AgentKind Claude = AgentKind.Claude;
    private static readonly AgentKind Copilot = AgentKind.Copilot;

    [Fact]
    public async Task UncoveredCliMember_IsBenched_SoRouterRoutesPast()
    {
        var reg = NewRegistry();
        // claude has a probe (covered); cursor is named but has no probe (uncovered).
        var validator = Build(
            classes: [ClassWith("frontier", "claude", "cursor")],
            probes: [new ClaudeInVmSmokeProbe()],
            reg: reg,
            out var capture);

        await validator.StartAsync(CancellationToken.None);

        // Uncovered member benched under MissingProbe so the router skips it.
        var cursorAv = reg.GetAvailability(Cursor);
        Assert.False(cursorAv.Available);
        Assert.Contains("no registered IInVmSmokeProbe", cursorAv.Reason);
        // Covered member untouched — stays routable, no exclusion entry created.
        Assert.True(reg.GetAvailability(Claude).Available);
        Assert.Contains(capture.Warnings, w => w.Contains("BENCHED") && w.Contains("cursor"));
    }

    [Fact]
    public async Task CoveredMember_StaysAvailable_AndSilent()
    {
        var reg = NewRegistry();
        var validator = Build(
            classes: [ClassWith("frontier", "claude")],
            probes: [new ClaudeInVmSmokeProbe()],
            reg: reg,
            out var capture);

        await validator.StartAsync(CancellationToken.None);

        Assert.True(reg.GetAvailability(Claude).Available);
        Assert.Empty(capture.Warnings);
    }

    [Fact]
    public async Task ExemptAgent_IsNotBenched_OnlyWarned()
    {
        var reg = NewRegistry();
        // copilot has no probe but is exempt by default (no sandbox CLI).
        var validator = Build(
            classes: [ClassWith("frontier", "copilot", "claude")],
            probes: [new ClaudeInVmSmokeProbe()],
            reg: reg,
            out var capture);

        await validator.StartAsync(CancellationToken.None);

        Assert.True(reg.GetAvailability(Copilot).Available);
        Assert.Contains(capture.Warnings, w => w.Contains("copilot") && w.Contains("Exempted"));
    }

    [Fact]
    public async Task ProberDisabled_WarnsOnly_DoesNotBench()
    {
        var reg = NewRegistry();
        var validator = Build(
            classes: [ClassWith("frontier", "cursor")],
            probes: [new ClaudeInVmSmokeProbe()],
            reg: reg,
            out var capture,
            opts: new InVmSmokeOptions { Enabled = false });

        await validator.StartAsync(CancellationToken.None);

        Assert.True(reg.GetAvailability(Cursor).Available);
        Assert.Contains(capture.Warnings, w => w.Contains("prober inactive"));
    }

    [Fact]
    public async Task NoProbesRegistered_WarnsOnly_DoesNotBench()
    {
        var reg = NewRegistry();
        var validator = Build(
            classes: [ClassWith("frontier", "cursor")],
            probes: [],
            reg: reg,
            out var capture);

        await validator.StartAsync(CancellationToken.None);

        Assert.True(reg.GetAvailability(Cursor).Available);
        Assert.Contains(capture.Warnings, w => w.Contains("prober inactive"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static AgentAvailabilityRegistry NewRegistry() =>
        new(new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);

    private static AgentClassOptions ClassWith(string id, params string[] agents)
    {
        var cls = new AgentClassOptions { Id = id, DisplayName = id };
        foreach (var a in agents)
            cls.Members.Add(new AgentMembershipOptions { Agent = a, QualityScore = 100 });
        return cls;
    }

    private static InVmSmokeProbeCoverageValidator Build(
        IEnumerable<AgentClassOptions> classes,
        IEnumerable<IInVmSmokeProbe> probes,
        AgentAvailabilityRegistry reg,
        out WarningCapturingLogger capture,
        InVmSmokeOptions? opts = null)
    {
        var cbOpts = new CodeyBoxOptions { AgentClasses = classes.ToList() };
        capture = new WarningCapturingLogger();
        // The validator drives benching through the IInVmSmokeCoveragePolicy
        // abstraction (the pure coverage policy, split out of the runtime prober),
        // so the coverage policy (enablement, exempt list, registered-probe set,
        // availability mutation) is exercised end-to-end here. The policy never
        // provisions, so it needs no sandbox/resolver/credential/cache deps.
        var coverage = new InVmSmokeCoveragePolicy(
            probes,
            reg,
            opts ?? new InVmSmokeOptions { Enabled = true });
        return new InVmSmokeProbeCoverageValidator(
            Options.Create(cbOpts),
            coverage,
            capture);
    }

    private sealed class WarningCapturingLogger : ILogger<InVmSmokeProbeCoverageValidator>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
