using Microsoft.Extensions.Options;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

/// <summary>
/// Hosted service that, at host start, cross-checks every agent named in a
/// <c>CodeyBox:AgentClasses</c> member against the registered
/// <see cref="IInVmSmokeProbe"/> set.
///
/// <para>A class member whose agent has no in-VM probe can never be verified
/// inside the sandbox, so a missing binary / broken auth would only surface on
/// the first real dispatch — exactly the exit-127 cascade the in-VM prober
/// exists to prevent. To honour AC#1 ("caught at smoke time, not first
/// dispatch"), when the prober is active this validator <b>benches</b> each
/// uncovered member in <see cref="AgentAvailabilityRegistry"/> under
/// <see cref="SmokeExclusionSource.MissingProbe"/>, so
/// <see cref="AgentClassRouter"/> routes work past it to a working alternative
/// rather than dispatching to an unverified CLI. The bench is also surfaced as
/// a loud startup warning.</para>
///
/// <para>Agents that legitimately have no first-party sandbox CLI driven by
/// this pipeline (e.g. copilot) are listed in
/// <see cref="InVmSmokeOptions.ExemptAgentsWithoutProbe"/> — they are warned
/// but never benched. When the prober is disabled, or no probes are registered
/// at all, enforcement is inactive (benching every member would be a
/// self-inflicted outage) and the validator only warns.</para>
/// </summary>
internal sealed class InVmSmokeProbeCoverageValidator : IHostedService
{
    private readonly IOptions<CodeyBoxOptions> _options;
    private readonly IEnumerable<IInVmSmokeProbe> _probes;
    private readonly AgentAvailabilityRegistry _availability;
    private readonly InVmSmokeOptions _smokeOptions;
    private readonly ILogger<InVmSmokeProbeCoverageValidator> _log;

    public InVmSmokeProbeCoverageValidator(
        IOptions<CodeyBoxOptions> options,
        IEnumerable<IInVmSmokeProbe> probes,
        AgentAvailabilityRegistry availability,
        InVmSmokeOptions smokeOptions,
        ILogger<InVmSmokeProbeCoverageValidator> log)
    {
        _options = options;
        _probes = probes;
        _availability = availability;
        _smokeOptions = smokeOptions;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        var classes = _options.Value.AgentClasses;
        if (classes.Count == 0) return Task.CompletedTask;

        var probeList = _probes.ToList();
        var covered = new HashSet<string>(probeList.Select(p => p.Kind.Value), StringComparer.OrdinalIgnoreCase);
        var exempt = new HashSet<string>(_smokeOptions.ExemptAgentsWithoutProbe, StringComparer.OrdinalIgnoreCase);

        // Enforcement (benching) only makes sense when the in-VM prober is
        // actually active. With it disabled, or no probes registered at all,
        // benching every member would be a self-inflicted outage — fall back to
        // warning-only so the gap is still visible.
        var enforce = _smokeOptions.Enabled && probeList.Count > 0;

        // Distinct (agent -> the classes that name it) so each uncovered agent
        // is reported once with the full blast radius.
        var uncovered = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cls in classes)
        {
            foreach (var m in cls.Members)
            {
                if (string.IsNullOrWhiteSpace(m.Agent)) continue;
                if (covered.Contains(m.Agent)) continue;
                if (!uncovered.TryGetValue(m.Agent, out var ids))
                {
                    ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    uncovered[m.Agent] = ids;
                }
                ids.Add(cls.Id);
            }
        }

        foreach (var (agent, classIds) in uncovered)
        {
            var classList = string.Join(", ", classIds);

            if (enforce && !exempt.Contains(agent))
            {
                // The AgentKind value is the raw config string — exactly what
                // AgentClassesConfigBuilder feeds the router as AgentMembership.Agent,
                // so this bench is keyed identically to the router's availability read.
                _availability.ExcludeForMissingProbe(
                    new AgentKind(agent),
                    $"no registered IInVmSmokeProbe — in-sandbox CLI cannot be verified (used by class(es): {classList})");
                _log.LogWarning(
                    "AgentClass member '{Agent}' has no registered IInVmSmokeProbe (used by class(es): {ClassIds}). " +
                    "BENCHED at startup so work routes past it instead of hitting exit-127/auth at first dispatch (AC#1). " +
                    "Register an IInVmSmokeProbe for '{Agent}', or add it to " +
                    "CodeyBox:Smoke:InVm:ExemptAgentsWithoutProbe if it has no sandbox CLI.",
                    agent, classList, agent);
            }
            else
            {
                _log.LogWarning(
                    "AgentClass member '{Agent}' has no registered IInVmSmokeProbe (used by class(es): {ClassIds}). " +
                    "Its in-sandbox CLI is NOT smoke-checked.{Detail}",
                    agent, classList,
                    enforce ? " Exempted from benching by configuration." : " In-VM smoke prober inactive; warning only.");
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
