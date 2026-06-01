using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Projects;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Drives the in-VM smoke gate (<see cref="IInVmSmokeGate"/>) on a schedule: one
/// sweep at startup, then every <see cref="InVmSmokeOptions.SweepIntervalSeconds"/>.
/// Runs in the background so the first (VM-provisioning) sweep never blocks host
/// startup. Depends on the gate abstraction rather than the concrete prober so
/// the sweep path stays decoupled from the implementation.
///
/// <para>A passing agent is cheap to re-sweep — the per-baseline cache means a
/// sweep only provisions a VM when the baseline ref changed or the TTL expired.
/// A <em>failing</em> agent is never cached, so it is re-probed on every sweep
/// regardless of TTL; that is the self-healing path back into routing once its
/// CLI is fixed and a re-probe passes.</para>
/// </summary>
public sealed class InVmSmokeProbeService : BackgroundService
{
    private readonly IInVmSmokeGate _gate;
    private readonly InVmSmokeOptions _opts;
    private readonly ILogger<InVmSmokeProbeService> _log;
    private readonly IProjectRepository? _projects;

    public InVmSmokeProbeService(
        IInVmSmokeGate gate,
        InVmSmokeOptions opts,
        ILogger<InVmSmokeProbeService> log,
        IProjectRepository? projects = null)
    {
        _gate = gate;
        _opts = opts;
        _log = log;
        _projects = projects;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_gate.Enabled)
            return;

        await SafeSweepAsync(stoppingToken);

        if (_opts.SweepIntervalSeconds <= 0)
            return;

        var interval = TimeSpan.FromSeconds(_opts.SweepIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) { return; }

            await SafeSweepAsync(stoppingToken);
        }
    }

    private async Task SafeSweepAsync(CancellationToken ct)
    {
        try
        {
            var targets = await ResolveSweepTargetsAsync(ct);
            if (targets.Count == 0)
            {
                await _gate.ProbeAllAsync(ct);
                return;
            }

            foreach (var target in targets)
                await _gate.ProbeAllAsync(target, ct);
        }
        catch (Exception ex)
        {
            // Warning, not Debug: a sustained sweep failure (bad image ref,
            // provider outage, misconfiguration) leaves agents on their last
            // verdict under the fail-open default and must be visible in prod,
            // not buried at Debug. The sweep still continues next interval.
            _log.LogWarning(ex, "In-VM smoke sweep failed; will retry next interval");
        }
    }

    private async Task<IReadOnlyList<InVmSmokeSandboxTarget>> ResolveSweepTargetsAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_opts.NetworkProfile))
            return [new InVmSmokeSandboxTarget(_opts.NetworkProfile, SandboxProfileFlavor.Headless)];

        if (_projects is null)
            return [];

        var projects = await _projects.ListAsync(ct);
        var targets = new List<InVmSmokeSandboxTarget>();
        var seen = new HashSet<(string Profile, SandboxProfileFlavor Flavor)>(StringTupleComparer.Ordinal);
        foreach (var project in projects)
        {
            var target = SandboxTargetResolver.ToInVmSmokeTarget(
                SandboxTargetResolver.ResolveProjectPhase(project, project.NetworkProfiles.Work));
            if (string.IsNullOrWhiteSpace(target.NetworkProfile))
                continue;
            var key = (target.NetworkProfile, target.Flavor);
            if (seen.Add(key))
                targets.Add(target);
        }

        return targets;
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Profile, SandboxProfileFlavor Flavor)>
    {
        public static readonly StringTupleComparer Ordinal = new();

        public bool Equals(
            (string Profile, SandboxProfileFlavor Flavor) x,
            (string Profile, SandboxProfileFlavor Flavor) y) =>
            string.Equals(x.Profile, y.Profile, StringComparison.Ordinal) && x.Flavor == y.Flavor;

        public int GetHashCode((string Profile, SandboxProfileFlavor Flavor) obj) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(obj.Profile), obj.Flavor);
    }
}
