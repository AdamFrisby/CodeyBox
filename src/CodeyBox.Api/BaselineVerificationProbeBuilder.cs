using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Multipass;

namespace CodeyBox.Api;

/// <summary>
/// Derives the post-bake binary-verification commands handed to the Multipass
/// sandbox provider from the configured agent catalog. The set of agents that
/// gets verified is taken from the same inputs the in-VM smoke coverage policy
/// (<see cref="IInVmSmokeCoveragePolicy"/>) uses so the bake gate matches the
/// router's exclusion/exemption decisions instead of installing a second,
/// divergent rule. Concretely:
///
/// <list type="bullet">
/// <item>An agent on <see cref="InVmSmokeOptions.ExemptAgentsWithoutProbe"/> is
/// skipped — the policy already lets it route past coverage; failing the bake
/// on its absence would contradict the policy.</item>
/// <item>An agent with no registered <see cref="IInVmSmokeProbe"/> is skipped —
/// the coverage policy already benches it under the missing-probe source so the
/// router does not dispatch to it. Hard-failing the bake on the same input
/// would short-circuit that policy before it can warn or route around it.</item>
/// <item>When smoke is globally disabled (<c>CodeyBox:Smoke:Enabled=false</c>)
/// or in-VM smoke is off / has no probes, no verification commands are
/// derived — the bake stays out of the dispatch gate's way.</item>
/// </list>
///
/// <para>The builder still surfaces a hard error when a registered probe has
/// no credential-independent step: that is a probe-shape bug, not a coverage
/// decision.</para>
/// </summary>
internal static class BaselineVerificationProbeBuilder
{
    public static IReadOnlyList<MultipassBaselineVerificationCommand> Build(
        CodeyBoxOptions codeyBoxOptions,
        ProjectsOptions projectsOptions,
        IEnumerable<IInVmSmokeProbe> probes,
        InVmSmokeOptions? inVmSmokeOptions = null,
        SmokeOptionsSnapshot? smokeOptions = null)
    {
        // Master smoke switch off → don't build any verification commands. The
        // dispatch gate is the canonical owner of "no smoke means we're not
        // verifying anything"; mirroring it here keeps bakes coherent with
        // dispatch.
        if (smokeOptions is not null && !smokeOptions.Enabled)
            return [];

        var probeList = probes.ToList();
        var inVm = inVmSmokeOptions ?? new InVmSmokeOptions();

        // In-VM smoke disabled or no probes registered → the coverage policy
        // does not bench uncovered agents, so we should not fail the bake on
        // them either.
        if (!inVm.Enabled || probeList.Count == 0)
            return [];

        var configuredAgents = CollectConfiguredAgents(codeyBoxOptions, projectsOptions);
        if (configuredAgents.Count == 0)
            return [];

        var probesByKind = probeList.ToDictionary(p => p.Kind.Value, StringComparer.OrdinalIgnoreCase);
        var exempt = new HashSet<string>(inVm.ExemptAgentsWithoutProbe, StringComparer.OrdinalIgnoreCase);

        var result = new List<MultipassBaselineVerificationCommand>(configuredAgents.Count);
        foreach (var agent in configuredAgents)
        {
            // The exempt list is the policy's escape hatch for agents with no
            // first-party sandbox CLI. Skip them so the bake does not require a
            // binary the coverage policy says we don't need.
            if (exempt.Contains(agent))
                continue;

            if (!probesByKind.TryGetValue(agent, out var probe))
            {
                // Coverage policy already benches missing-probe agents under the
                // dedicated source so the router routes past them. Failing the
                // bake here would pre-empt that policy and prevent every
                // Multipass launch — exactly the regression the audit flagged.
                continue;
            }

            var step = probe.BuildSteps(credential: null).FirstOrDefault(s => s.Argv.Count > 0 && s.Stdin is null);
            if (step is null)
            {
                throw new InvalidOperationException(
                    $"Baseline verification cannot cover configured agent '{agent}': " +
                    "its IInVmSmokeProbe has no credential-independent command.");
            }

            // The agent name is used as the verification command's diagnostic label.
            // The sandbox layer does not interpret the label — it is surfaced in log
            // lines and error messages so an operator can map a bake failure back to
            // the agent that contributed the command.
            result.Add(new MultipassBaselineVerificationCommand(agent, step.Argv, step.FailureHint));
        }

        return result;
    }

    private static IReadOnlyList<string> CollectConfiguredAgents(
        CodeyBoxOptions codeyBoxOptions,
        ProjectsOptions projectsOptions)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            var trimmed = value.Trim();
            if (seen.Add(trimmed))
                result.Add(trimmed);
        }

        foreach (var agent in codeyBoxOptions.AgentClasses
            .SelectMany(c => c.Members)
            .Select(m => m.Agent))
        {
            Add(agent);
        }

        var defaultAgent = string.IsNullOrWhiteSpace(projectsOptions.Defaults.Agent)
            ? AgentKind.Claude.Value
            : projectsOptions.Defaults.Agent;
        Add(defaultAgent);
        AddAuditAgents(projectsOptions.Defaults.Audit, Add);

        foreach (var project in projectsOptions.Projects)
        {
            if (project.Agent is not null)
            {
                Add(string.IsNullOrWhiteSpace(project.Agent)
                    ? AgentKind.Claude.Value
                    : project.Agent);
            }
            AddAuditAgents(project.Audit, Add);
        }

        return result;
    }

    private static void AddAuditAgents(ProjectAuditConfig? audit, Action<string?> add)
    {
        if (audit is null)
            return;

        add(audit.AuditAgent);
        if (audit.PerAuditorAgent is not null)
        {
            foreach (var agent in audit.PerAuditorAgent.Values)
                add(agent);
        }

        if (audit.Profiles is not null)
        {
            foreach (var profile in audit.Profiles.Values)
                AddAuditAgents(profile, add);
        }
    }
}
