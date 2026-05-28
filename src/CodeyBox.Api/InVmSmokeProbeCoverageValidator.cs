using Microsoft.Extensions.Options;
using CodeyBox.Core;

namespace CodeyBox.Api;

/// <summary>
/// Hosted service that, at host start, cross-checks every agent named in a
/// <c>CodeyBox:AgentClasses</c> member against the registered
/// <see cref="IInVmSmokeProbe"/> set.
///
/// <para>A class member whose agent has no in-VM probe is routable but never
/// verified inside the sandbox, so a missing binary / broken auth would only
/// surface on the first real dispatch — exactly the exit-127 cascade the in-VM
/// prober exists to prevent. This validator surfaces that gap as a loud
/// startup warning so adding a new member without a working in-VM CLI is caught
/// at smoke time (AC#1), not hours later in a failed work item.</para>
///
/// <para>Warning-only: some agents legitimately have no first-party CLI driven
/// by this pipeline (e.g. Copilot), and an over-eager fail-fast would block an
/// otherwise valid configuration. Operators who add a CLI-backed member act on
/// the warning by registering the matching probe.</para>
/// </summary>
internal sealed class InVmSmokeProbeCoverageValidator : IHostedService
{
    private readonly IOptions<CodeyBoxOptions> _options;
    private readonly IEnumerable<IInVmSmokeProbe> _probes;
    private readonly ILogger<InVmSmokeProbeCoverageValidator> _log;

    public InVmSmokeProbeCoverageValidator(
        IOptions<CodeyBoxOptions> options,
        IEnumerable<IInVmSmokeProbe> probes,
        ILogger<InVmSmokeProbeCoverageValidator> log)
    {
        _options = options;
        _probes = probes;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        var classes = _options.Value.AgentClasses;
        if (classes.Count == 0) return Task.CompletedTask;

        var covered = new HashSet<string>(_probes.Select(p => p.Kind.Value), StringComparer.OrdinalIgnoreCase);

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
            _log.LogWarning(
                "AgentClass member '{Agent}' has no registered IInVmSmokeProbe (used by class(es): {ClassIds}). " +
                "Its in-sandbox CLI will NOT be smoke-checked, so a missing binary or broken auth will only surface " +
                "on first dispatch. Register an IInVmSmokeProbe for '{Agent}' to close this gap.",
                agent, string.Join(", ", classIds), agent);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
