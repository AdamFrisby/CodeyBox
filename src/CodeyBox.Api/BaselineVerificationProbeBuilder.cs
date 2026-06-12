using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Multipass;

namespace CodeyBox.Api;

/// <summary>
/// Derives the post-bake binary-verification commands handed to the Multipass
/// sandbox provider from the configured agent catalog. The bake gate is the
/// durable contract that a freshly cloned VM has every configured agent CLI
/// on PATH; it MUST run regardless of runtime dispatch-smoke gating, because
/// disabling smoke at runtime is a routing decision, not a permission to ship
/// an incomplete baseline.
///
/// <para>Rule (each rule below is independent and intentionally simple — the
/// audit history on this builder is a sequence of regressions caused by
/// coupling bake to runtime gates):</para>
///
/// <list type="bullet">
/// <item>For every configured agent with a registered
/// <see cref="IInVmSmokeProbe"/>, emit its credential-independent step. A
/// registered probe ALWAYS supersedes any exemption list — that is what makes
/// the bake check load-bearing for agents like Copilot whose default
/// exemption is back-compat for operators who haven't installed the CLI yet
/// but where a registered probe means the CLI <em>is</em> expected.</item>
/// <item>For a configured agent with NO registered probe, fall back to the
/// operator-controlled exemption list. An agent on that list is the
/// explicit "no first-party sandbox CLI to verify" escape hatch and is
/// skipped silently. An agent not on that list fails loudly: a configured
/// CLI-backed runner that cannot be verified must not produce a baseline
/// that looks ready to clone.</item>
/// </list>
///
/// <para>The builder still surfaces a hard error when a registered probe has
/// no credential-independent step: that is a probe-shape bug, not a coverage
/// decision.</para>
///
/// <para>Smoke options (<c>CodeyBox:Smoke:Enabled</c>,
/// <c>CodeyBox:Smoke:InVm:Enabled</c>) are <em>intentionally</em> not consulted
/// here. They gate runtime dispatch decisions; the bake-image integrity check
/// is a separate concern and must hold even when those switches are off.</para>
/// </summary>
internal static class BaselineVerificationProbeBuilder
{
    public static IReadOnlyList<MultipassBaselineVerificationCommand> Build(
        CodeyBoxOptions codeyBoxOptions,
        ProjectsOptions projectsOptions,
        IEnumerable<IInVmSmokeProbe> probes,
        InVmSmokeOptions? inVmSmokeOptions = null)
    {
        var probeList = probes.ToList();
        var configuredAgents = CollectConfiguredAgents(codeyBoxOptions, projectsOptions);
        if (configuredAgents.Count == 0)
            return [];

        var probesByKind = probeList.ToDictionary(p => p.Kind.Value, StringComparer.OrdinalIgnoreCase);
        // The exempt list is the operator's "this agent has no first-party CLI
        // to verify" hatch. We read it from InVmSmokeOptions because that is
        // where it is already configured, but we do NOT honour the in-VM
        // Enabled flag — that flag governs runtime dispatch, not bake-image
        // integrity. When no InVmSmokeOptions is supplied we fall back to its
        // built-in defaults (currently exempts copilot for back-compat) rather
        // than treating no-options as "exempt nothing".
        var exempt = new HashSet<string>(
            (inVmSmokeOptions ?? new InVmSmokeOptions()).ExemptAgentsWithoutProbe,
            StringComparer.OrdinalIgnoreCase);

        var result = new List<MultipassBaselineVerificationCommand>(configuredAgents.Count);
        foreach (var agent in configuredAgents)
        {
            // A registered probe always wins. Checking probe registration BEFORE
            // the exempt list is what makes a configured agent (e.g. Copilot)
            // with a registered IInVmSmokeProbe actually get verified — the
            // exemption is only the escape hatch for agents WITHOUT a probe.
            if (probesByKind.TryGetValue(agent, out var probe))
            {
                var step = probe.BuildSteps(credential: null)
                    .FirstOrDefault(s => s.Argv.Count > 0 && s.Stdin is null);
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
                continue;
            }

            // No probe registered for this agent. The exempt list expresses
            // "the operator confirms no CLI to verify for this agent" — skip
            // without failing the bake. Without the exemption, fail before
            // Multipass launches a baseline that would otherwise bypass the
            // post-bake binary gate entirely.
            if (exempt.Contains(agent))
                continue;

            throw new InvalidOperationException(
                $"Baseline verification cannot cover configured agent '{agent}': no registered IInVmSmokeProbe. " +
                $"Register an IInVmSmokeProbe for '{agent}', or add it to " +
                "CodeyBox:Smoke:InVm:ExemptAgentsWithoutProbe if it has no sandbox CLI to verify.");
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
