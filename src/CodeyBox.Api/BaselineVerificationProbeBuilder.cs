using CodeyBox.Core;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Multipass;

namespace CodeyBox.Api;

internal static class BaselineVerificationProbeBuilder
{
    public static IReadOnlyList<MultipassBaselineVerificationCommand> Build(
        CodeyBoxOptions codeyBoxOptions,
        ProjectsOptions projectsOptions,
        IEnumerable<IInVmSmokeProbe> probes)
    {
        var configuredAgents = CollectConfiguredAgents(codeyBoxOptions, projectsOptions);
        if (configuredAgents.Count == 0)
            return [];

        var probesByKind = probes.ToDictionary(p => p.Kind.Value, StringComparer.OrdinalIgnoreCase);
        var result = new List<MultipassBaselineVerificationCommand>(configuredAgents.Count);
        foreach (var agent in configuredAgents)
        {
            if (!probesByKind.TryGetValue(agent, out var probe))
            {
                throw new InvalidOperationException(
                    $"Baseline verification cannot cover configured agent '{agent}': " +
                    "no IInVmSmokeProbe is registered. Register a credential-independent " +
                    "in-VM probe for this agent or remove it from the active configuration.");
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
