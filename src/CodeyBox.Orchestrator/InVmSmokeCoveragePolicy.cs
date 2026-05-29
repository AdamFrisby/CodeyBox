using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Pure (no-provisioning) coverage policy for the in-VM smoke subsystem
/// (<see cref="IInVmSmokeCoveragePolicy"/>). Owns the startup/hot-reload decision
/// of what to do with an <c>AgentClass</c> member whose agent has no registered
/// <see cref="IInVmSmokeProbe"/>: bench it under the missing-probe source so the
/// router routes past it (AC#1), exempt it, or warn only.
///
/// <para>Deliberately split out of <see cref="InVmSmokeProber"/>: coverage
/// enforcement is config-driven routing policy with no VM provisioning, whereas
/// the prober's dispatch gate and background sweep are runtime hooks that clone
/// sandboxes. Keeping them in separate types lets the startup validator and the
/// hot-reload bridge depend only on this narrow port.</para>
/// </summary>
public sealed class InVmSmokeCoveragePolicy : IInVmSmokeCoveragePolicy
{
    private readonly IReadOnlyList<IInVmSmokeProbe> _probes;
    private readonly ISmokeAvailabilityRegistry _availability;
    private readonly InVmSmokeOptions _opts;

    public InVmSmokeCoveragePolicy(
        IEnumerable<IInVmSmokeProbe> probes,
        ISmokeAvailabilityRegistry availability,
        InVmSmokeOptions opts)
    {
        _probes = probes.ToList();
        _availability = availability;
        _opts = opts;
    }

    private bool Enabled => _opts.Enabled && _probes.Count > 0;

    /// <inheritdoc />
    public IReadOnlyList<InVmSmokeCoverageOutcome> EnforceMissingProbeCoverage(
        IReadOnlyList<InVmSmokeClassCoverage> classes)
    {
        var covered = new HashSet<string>(_probes.Select(p => p.Kind.Value), StringComparer.OrdinalIgnoreCase);
        var exempt = new HashSet<string>(_opts.ExemptAgentsWithoutProbe, StringComparer.OrdinalIgnoreCase);

        // Distinct (agent -> the classes that name it) so each uncovered agent is
        // reported once with the full blast radius.
        var uncovered = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cls in classes)
        {
            foreach (var agent in cls.Agents)
            {
                if (string.IsNullOrWhiteSpace(agent)) continue;
                if (covered.Contains(agent)) continue;
                if (!uncovered.TryGetValue(agent, out var ids))
                {
                    ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    uncovered[agent] = ids;
                }
                ids.Add(cls.ClassId);
            }
        }

        var outcomes = new List<InVmSmokeCoverageOutcome>(uncovered.Count);
        foreach (var (agent, classIds) in uncovered)
        {
            InVmSmokeCoverageAction action;
            // Benching only makes sense when the prober is active: with it
            // disabled or no probes registered, benching every member would be a
            // self-inflicted outage, so fall back to warn-only.
            if (!Enabled)
            {
                action = InVmSmokeCoverageAction.ProberInactive;
            }
            else if (exempt.Contains(agent))
            {
                action = InVmSmokeCoverageAction.Exempt;
            }
            else
            {
                var classList = string.Join(", ", classIds);
                _availability.ExcludeForMissingProbe(
                    new AgentKind(agent),
                    $"no registered IInVmSmokeProbe — in-sandbox CLI cannot be verified (used by class(es): {classList})");
                action = InVmSmokeCoverageAction.Benched;
            }
            outcomes.Add(new InVmSmokeCoverageOutcome(agent, classIds.ToList(), action));
        }
        return outcomes;
    }
}
