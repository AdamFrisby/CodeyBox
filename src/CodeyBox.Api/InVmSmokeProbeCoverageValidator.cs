using Microsoft.Extensions.Options;
using CodeyBox.Core;

namespace CodeyBox.Api;

/// <summary>
/// Hosted service that, at host start, hands every agent named in a
/// <c>CodeyBox:AgentClasses</c> member to the in-VM smoke coverage policy
/// (<see cref="IInVmSmokeCoveragePolicy.EnforceMissingProbeCoverage"/>), which
/// benches any member whose agent has no registered in-VM probe.
///
/// <para>A class member whose agent has no in-VM probe can never be verified
/// inside the sandbox, so a missing binary / broken auth would only surface on
/// the first real dispatch — exactly the exit-127 cascade the in-VM prober
/// exists to prevent. To honour AC#1 ("caught at smoke time, not first
/// dispatch"), the gate benches each uncovered member so the router routes work
/// past it to a working alternative rather than dispatching to an unverified
/// CLI.</para>
///
/// <para>This service owns no smoke policy of its own: the enablement decision,
/// the exempt list, the registered-probe set, and the availability mutation all
/// live behind <see cref="IInVmSmokeCoveragePolicy"/>, so the host/presentation
/// layer never recomputes enablement or binds to the concrete availability
/// registry. Its only job is to read the configured class catalog and surface
/// the policy's per-agent outcome to operators as a loud startup warning.</para>
/// </summary>
internal sealed class InVmSmokeProbeCoverageValidator : IHostedService
{
    private readonly IOptions<CodeyBoxOptions> _options;
    private readonly IInVmSmokeCoveragePolicy _coverage;
    private readonly ILogger<InVmSmokeProbeCoverageValidator> _log;

    public InVmSmokeProbeCoverageValidator(
        IOptions<CodeyBoxOptions> options,
        IInVmSmokeCoveragePolicy coverage,
        ILogger<InVmSmokeProbeCoverageValidator> log)
    {
        _options = options;
        _coverage = coverage;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        var classes = _options.Value.AgentClasses;
        if (classes.Count == 0) return Task.CompletedTask;

        var coverage = InVmSmokeCoverageRequest.FromAgentClasses(classes);

        foreach (var outcome in _coverage.EnforceMissingProbeCoverage(coverage))
            LogOutcome(outcome);

        return Task.CompletedTask;
    }

    private void LogOutcome(InVmSmokeCoverageOutcome outcome)
    {
        var classList = string.Join(", ", outcome.ClassIds);
        switch (outcome.Action)
        {
            case InVmSmokeCoverageAction.Benched:
                _log.LogWarning(
                    "AgentClass member '{Agent}' has no registered IInVmSmokeProbe (used by class(es): {ClassIds}). " +
                    "BENCHED at startup so work routes past it instead of hitting exit-127/auth at first dispatch (AC#1). " +
                    "Register an IInVmSmokeProbe for '{Agent}', or add it to " +
                    "CodeyBox:Smoke:InVm:ExemptAgentsWithoutProbe if it has no sandbox CLI.",
                    outcome.Agent, classList, outcome.Agent);
                break;
            case InVmSmokeCoverageAction.Exempt:
                _log.LogWarning(
                    "AgentClass member '{Agent}' has no registered IInVmSmokeProbe (used by class(es): {ClassIds}). " +
                    "Its in-sandbox CLI is NOT smoke-checked. Exempted from benching by configuration.",
                    outcome.Agent, classList);
                break;
            case InVmSmokeCoverageAction.ProberInactive:
                _log.LogWarning(
                    "AgentClass member '{Agent}' has no registered IInVmSmokeProbe (used by class(es): {ClassIds}). " +
                    "Its in-sandbox CLI is NOT smoke-checked. In-VM smoke prober inactive; warning only.",
                    outcome.Agent, classList);
                break;
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
