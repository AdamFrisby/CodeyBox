using System.Text;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;

namespace CodeyBox.Api;

/// <summary>
/// Derives the post-bake binary-verification commands handed to a sandbox
/// provider from the configured agent catalog. The bake gate is the
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
    internal const int MaximumConfigurationEntriesInspected = 4096;
    internal const int MaximumProbeStepsInspected =
        BaselineProvisioningLimits.MaximumVerificationCommands;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static IReadOnlyList<BaselineVerificationCommand> Build(
        CodeyBoxOptions codeyBoxOptions,
        ProjectsOptions projectsOptions,
        IEnumerable<IInVmSmokeProbe> probes,
        InVmSmokeOptions? inVmSmokeOptions = null)
    {
        ArgumentNullException.ThrowIfNull(codeyBoxOptions);
        ArgumentNullException.ThrowIfNull(projectsOptions);
        ArgumentNullException.ThrowIfNull(probes);

        var probesByKind = SnapshotProbes(probes);
        var configuredAgents = CollectConfiguredAgents(codeyBoxOptions, projectsOptions);
        if (configuredAgents.Count == 0)
            return [];

        // The exempt list is the operator's "this agent has no first-party CLI
        // to verify" hatch. We read it from InVmSmokeOptions because that is
        // where it is already configured, but we do NOT honour the in-VM
        // Enabled flag — that flag governs runtime dispatch, not bake-image
        // integrity. When no InVmSmokeOptions is supplied we fall back to its
        // built-in defaults (currently exempts copilot for back-compat) rather
        // than treating no-options as "exempt nothing".
        var exempt = SnapshotExemptAgents(
            (inVmSmokeOptions ?? new InVmSmokeOptions()).ExemptAgentsWithoutProbe);

        var result = new List<BaselineVerificationCommand>(
            Math.Min(configuredAgents.Count, BaselineProvisioningLimits.MaximumVerificationCommands));
        long aggregateVerificationTextBytes = 0;
        foreach (var agent in configuredAgents)
        {
            // A registered probe always wins. Checking probe registration BEFORE
            // the exempt list is what makes a configured agent (e.g. Copilot)
            // with a registered IInVmSmokeProbe actually get verified — the
            // exemption is only the escape hatch for agents WITHOUT a probe.
            if (probesByKind.TryGetValue(agent, out var probe))
            {
                var command = BuildVerificationCommand(
                    probe,
                    agent,
                    ref aggregateVerificationTextBytes);
                if (command is null)
                {
                    throw new InvalidOperationException(
                        $"Baseline verification cannot cover configured agent '{agent}': " +
                        "its IInVmSmokeProbe has no credential-independent command.");
                }

                // The agent name is the verification command's diagnostic label.
                // It lets logs and errors identify the contributing agent and remains
                // part of the immutable command contract a provider may hash.
                if (result.Count >= BaselineProvisioningLimits.MaximumVerificationCommands)
                {
                    throw new InvalidOperationException(
                        $"Baseline verification cannot contain more than {BaselineProvisioningLimits.MaximumVerificationCommands} commands.");
                }
                result.Add(command);
                continue;
            }

            // No probe registered for this agent. The exempt list expresses
            // "the operator confirms no CLI to verify for this agent" — skip
            // without failing the bake. Without the exemption, fail before
            // a provider launches a baseline that would otherwise bypass the
            // post-bake binary gate entirely.
            if (exempt.Contains(agent))
                continue;

            throw new InvalidOperationException(
                $"Baseline verification cannot cover configured agent '{agent}': no registered IInVmSmokeProbe. " +
                $"Register an IInVmSmokeProbe for '{agent}', or add it to " +
                "CodeyBox:Smoke:InVm:ExemptAgentsWithoutProbe if it has no sandbox CLI to verify.");
        }

        return Array.AsReadOnly(result.ToArray());
    }

    private static IReadOnlyDictionary<string, IInVmSmokeProbe> SnapshotProbes(
        IEnumerable<IInVmSmokeProbe> probes)
    {
        var result = new Dictionary<string, IInVmSmokeProbe>(StringComparer.OrdinalIgnoreCase);
        var inspected = 0;
        foreach (var probe in probes)
        {
            if (inspected++ >= BaselineProvisioningLimits.MaximumVerificationCommands)
            {
                throw new InvalidOperationException(
                    $"Baseline verification cannot register more than {BaselineProvisioningLimits.MaximumVerificationCommands} in-VM probes.");
            }
            if (probe is null)
                throw new InvalidOperationException("Baseline verification probes cannot contain null entries.");

            var kind = probe.Kind.Value;
            ValidateAgentIdentifier(kind, "in-VM probe kind");
            if (!result.TryAdd(kind, probe))
                throw new InvalidOperationException($"Baseline verification has duplicate in-VM probes for agent '{kind}'.");
        }
        return result;
    }

    private static IReadOnlySet<string> SnapshotExemptAgents(IEnumerable<string> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inspected = 0;
        foreach (var agent in agents)
        {
            if (inspected++ >= BaselineProvisioningLimits.MaximumVerificationCommands)
            {
                throw new InvalidOperationException(
                    $"Baseline verification cannot inspect more than {BaselineProvisioningLimits.MaximumVerificationCommands} exempt agents.");
            }
            ValidateAgentIdentifier(agent, "baseline verification exemption");
            result.Add(agent);
        }
        return result;
    }

    private static BaselineVerificationCommand? BuildVerificationCommand(
        IInVmSmokeProbe probe,
        string agent,
        ref long aggregateVerificationTextBytes)
    {
        var steps = probe.BuildSteps(credential: null)
            ?? throw new InvalidOperationException(
                $"Baseline verification probe for agent '{agent}' returned a null step list.");
        var inspected = 0;
        foreach (var step in steps)
        {
            if (inspected++ >= MaximumProbeStepsInspected)
            {
                throw new InvalidOperationException(
                    $"Baseline verification probe for agent '{agent}' exposes more than {MaximumProbeStepsInspected} steps.");
            }
            if (step is null)
                throw new InvalidOperationException($"Baseline verification probe for agent '{agent}' returned a null step.");
            if (step.Stdin is not null)
                continue;

            var argv = SnapshotVerificationArgv(step.Argv, agent, out var argvTextBytes);
            if (argv.Count == 0)
                continue;

            var commandTextBytes = (long)GetVerificationTextByteCount(
                agent,
                $"Baseline verification label for agent '{agent}'",
                allowEmpty: false)
                + argvTextBytes;
            if (step.FailureHint is { } failureHint)
            {
                commandTextBytes += GetVerificationTextByteCount(
                    failureHint,
                    $"Baseline verification failure hint for agent '{agent}'",
                    allowEmpty: true);
            }
            if (commandTextBytes
                > BaselineProvisioningLimits.MaximumAggregateVerificationTextUtf8Bytes
                    - aggregateVerificationTextBytes)
            {
                throw new InvalidOperationException(
                    $"Baseline verification commands exceed " +
                    $"{BaselineProvisioningLimits.MaximumAggregateVerificationTextUtf8Bytes} UTF-8 bytes in aggregate.");
            }
            aggregateVerificationTextBytes += commandTextBytes;
            return new BaselineVerificationCommand(agent, argv, step.FailureHint);
        }
        return null;
    }

    private static IReadOnlyList<string> SnapshotVerificationArgv(
        IEnumerable<string> arguments,
        string agent,
        out long textBytes)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var result = new List<string>(8);
        textBytes = 0;
        foreach (var argument in arguments)
        {
            if (result.Count >= BaselineProvisioningLimits.MaximumVerificationArguments)
            {
                throw new InvalidOperationException(
                    $"Baseline verification command for agent '{agent}' cannot contain more than " +
                    $"{BaselineProvisioningLimits.MaximumVerificationArguments} arguments.");
            }
            if (argument is null)
                throw new InvalidOperationException($"Baseline verification command for agent '{agent}' contains a null argument.");
            textBytes += GetVerificationTextByteCount(
                argument,
                $"Baseline verification argument for agent '{agent}'",
                allowEmpty: result.Count != 0);
            result.Add(argument);
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static IReadOnlyList<string> CollectConfiguredAgents(
        CodeyBoxOptions codeyBoxOptions,
        ProjectsOptions projectsOptions)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedAudits = new HashSet<ProjectAuditConfig>(ReferenceEqualityComparer.Instance);
        var inspectedEntries = 0;

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            ValidateAgentIdentifier(value, "configured agent");

            var trimmed = value.Trim();
            if (seen.Add(trimmed))
                result.Add(trimmed);
        }

        void Inspect(string source)
        {
            if (inspectedEntries++ >= MaximumConfigurationEntriesInspected)
            {
                throw new InvalidOperationException(
                    $"Baseline verification cannot inspect more than {MaximumConfigurationEntriesInspected} configured entries while reading {source}.");
            }
        }

        void AddAuditAgentsIterative(ProjectAuditConfig? root, string source)
        {
            if (root is null)
                return;

            var pending = new Stack<ProjectAuditConfig>();
            pending.Push(root);
            while (pending.Count != 0)
            {
                var audit = pending.Pop();
                if (!visitedAudits.Add(audit))
                    continue;

                Inspect(source);
                Add(audit.AuditAgent);
                if (audit.PerAuditorAgent is not null)
                {
                    foreach (var agent in audit.PerAuditorAgent.Values)
                    {
                        Inspect($"{source}.PerAuditorAgent");
                        Add(agent);
                    }
                }

                if (audit.Profiles is null)
                    continue;

                // Push in reverse enumeration order to preserve the former
                // recursive depth-first order without recursive call-stack risk.
                var children = new List<ProjectAuditConfig>();
                foreach (var profile in audit.Profiles.Values)
                {
                    Inspect($"{source}.Profiles");
                    if (profile is not null)
                        children.Add(profile);
                }
                for (var i = children.Count - 1; i >= 0; i--)
                    pending.Push(children[i]);
            }
        }

        foreach (var agentClass in codeyBoxOptions.AgentClasses
            ?? throw new InvalidOperationException("CodeyBox:AgentClasses cannot be null."))
        {
            Inspect("CodeyBox:AgentClasses");
            if (agentClass is null)
                throw new InvalidOperationException("CodeyBox:AgentClasses cannot contain null entries.");
            foreach (var member in agentClass.Members
                ?? throw new InvalidOperationException("CodeyBox:AgentClasses members cannot be null."))
            {
                Inspect("CodeyBox:AgentClasses members");
                if (member is null)
                    throw new InvalidOperationException("CodeyBox:AgentClasses members cannot contain null entries.");
                Add(member.Agent);
            }
        }

        var defaults = projectsOptions.Defaults
            ?? throw new InvalidOperationException("CodeyBox project defaults cannot be null.");
        var defaultAgent = string.IsNullOrWhiteSpace(defaults.Agent)
            ? AgentKind.Claude.Value
            : defaults.Agent;
        Add(defaultAgent);
        AddAuditAgentsIterative(defaults.Audit, "CodeyBox project-default audit config");

        foreach (var project in projectsOptions.Projects
            ?? throw new InvalidOperationException("CodeyBox projects cannot be null."))
        {
            Inspect("CodeyBox projects");
            if (project is null)
                throw new InvalidOperationException("CodeyBox projects cannot contain null entries.");
            if (project.Agent is not null)
            {
                Add(string.IsNullOrWhiteSpace(project.Agent)
                    ? AgentKind.Claude.Value
                    : project.Agent);
            }
            AddAuditAgentsIterative(project.Audit, "CodeyBox project audit config");
        }

        return Array.AsReadOnly(result.ToArray());
    }

    private static void ValidateAgentIdentifier(string? value, string fieldName)
    {
        if (value is null)
            throw new InvalidOperationException($"{fieldName} cannot be null.");
        _ = GetVerificationTextByteCount(value, fieldName, allowEmpty: false);
    }

    private static int GetVerificationTextByteCount(
        string value,
        string fieldName,
        bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > BaselineProvisioningLimits.MaximumVerificationTextUtf8Bytes)
        {
            throw new InvalidOperationException(
                $"{fieldName} exceeds {BaselineProvisioningLimits.MaximumVerificationTextUtf8Bytes} UTF-8 bytes.");
        }
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{fieldName} cannot be empty.");
        if (value.Any(char.IsControl))
            throw new InvalidOperationException($"{fieldName} cannot contain control characters.");
        try
        {
            var bytes = StrictUtf8.GetByteCount(value);
            if (bytes > BaselineProvisioningLimits.MaximumVerificationTextUtf8Bytes)
            {
                throw new InvalidOperationException(
                    $"{fieldName} exceeds {BaselineProvisioningLimits.MaximumVerificationTextUtf8Bytes} UTF-8 bytes.");
            }
            return bytes;
        }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidOperationException($"{fieldName} is not valid Unicode.", ex);
        }
    }
}
