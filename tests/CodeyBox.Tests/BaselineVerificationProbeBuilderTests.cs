using CodeyBox.Agents.Antigravity;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Copilot;
using CodeyBox.Agents.Cursor;
using CodeyBox.Agents.Gemini;
using CodeyBox.Agents.Opencode;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

public sealed class BaselineVerificationProbeBuilderTests
{
    [Fact]
    public void Build_IncludesClassDefaultProjectAndAuditAgents()
    {
        var opts = new CodeyBoxOptions
        {
            AgentClasses =
            [
                new AgentClassOptions
                {
                    Id = "frontier",
                    Members = [new AgentMembershipOptions { Agent = AgentKind.Claude.Value }],
                },
            ],
        };
        var projects = new ProjectsOptions
        {
            Defaults = new ProjectDefaultsConfig
            {
                Agent = AgentKind.Codex.Value,
                Audit = new ProjectAuditConfig
                {
                    AuditAgent = AgentKind.Gemini.Value,
                    PerAuditorAgent = new Dictionary<string, string>
                    {
                        ["security:llm-review"] = AgentKind.Cursor.Value,
                    },
                    Profiles = new Dictionary<string, ProjectAuditConfig>
                    {
                        ["strict"] = new() { AuditAgent = AgentKind.Opencode.Value },
                    },
                },
            },
            Projects =
            [
                new ProjectConfig
                {
                    Id = "p",
                    Agent = AgentKind.Antigravity.Value,
                    Audit = new ProjectAuditConfig
                    {
                        PerAuditorAgent = new Dictionary<string, string>
                        {
                            ["architecture:llm-review"] = AgentKind.Copilot.Value,
                        },
                    },
                },
            ],
        };

        var probes = BaselineVerificationProbeBuilder.Build(
            opts,
            projects,
            AllProbes());

        var byAgent = probes.ToDictionary(p => p.Label, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            [
                AgentKind.Claude.Value,
                AgentKind.Codex.Value,
                AgentKind.Gemini.Value,
                AgentKind.Cursor.Value,
                AgentKind.Opencode.Value,
                AgentKind.Antigravity.Value,
                AgentKind.Copilot.Value,
            ],
            probes.Select(p => p.Label));
        Assert.Equal([AntigravityAgentRunner.DefaultBinary, "--version"], byAgent[AgentKind.Antigravity.Value].Argv);
        Assert.Equal([CodexAgentRunner.DefaultBinary, "--version"], byAgent[AgentKind.Codex.Value].Argv);
        Assert.Equal([GeminiAgentRunner.DefaultBinary, "--version"], byAgent[AgentKind.Gemini.Value].Argv);
        Assert.Equal([CursorAgentRunner.DefaultBinary, "--version"], byAgent[AgentKind.Cursor.Value].Argv);
        Assert.Equal([OpencodeAgentRunner.DefaultBinary, "--version"], byAgent[AgentKind.Opencode.Value].Argv);
        Assert.Equal([CopilotAgentRunner.DefaultBinary, "--version"], byAgent[AgentKind.Copilot.Value].Argv);
        Assert.Equal([ClaudeAgentRunner.DefaultBinary, "--version"], byAgent[AgentKind.Claude.Value].Argv);
    }

    [Fact]
    public void Build_ThrowsForConfiguredAgentWithNoProbe_WhenNotExempt()
    {
        // A configured custom agent with no IInVmSmokeProbe (e.g. "aider") has
        // no first-party verification command we can run. Unless the operator
        // explicitly exempts it as "no sandbox CLI", fail loudly before a
        // baseline is baked without any check for that configured runner.
        var opts = new CodeyBoxOptions
        {
            AgentClasses =
            [
                new AgentClassOptions
                {
                    Id = "custom",
                    Members =
                    [
                        new AgentMembershipOptions { Agent = "aider" },
                        new AgentMembershipOptions { Agent = AgentKind.Claude.Value },
                    ],
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BaselineVerificationProbeBuilder.Build(opts, new ProjectsOptions(), AllProbes()));

        Assert.Contains("configured agent 'aider'", ex.Message);
        Assert.Contains("no registered IInVmSmokeProbe", ex.Message);
        Assert.Contains("ExemptAgentsWithoutProbe", ex.Message);
    }

    [Fact]
    public void Build_RegisteredProbeWins_OverDefaultCopilotExemption()
    {
        // Regression: the default InVmSmokeOptions.ExemptAgentsWithoutProbe
        // still names copilot for back-compat with operators who haven't
        // installed the Copilot CLI. CopilotInVmSmokeProbe IS registered, so
        // the bake gate must still cover Copilot — failing the bake when the
        // copilot binary is missing is exactly what the audit asked for. The
        // exempt list is the escape hatch for agents WITHOUT a probe, not a
        // permission to drop verification when one is registered.
        var opts = new CodeyBoxOptions
        {
            AgentClasses =
            [
                new AgentClassOptions
                {
                    Id = "x",
                    Members =
                    [
                        new AgentMembershipOptions { Agent = AgentKind.Copilot.Value },
                        new AgentMembershipOptions { Agent = AgentKind.Claude.Value },
                    ],
                },
            ],
        };

        var probes = BaselineVerificationProbeBuilder.Build(
            opts,
            new ProjectsOptions(),
            AllProbes(),
            // Defaults: ExemptAgentsWithoutProbe contains "copilot".
            new InVmSmokeOptions());

        var labels = probes.Select(p => p.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(AgentKind.Copilot.Value, labels);
        Assert.Contains(AgentKind.Claude.Value, labels);
    }

    [Fact]
    public void Build_ExemptList_OnlyAppliesWhenNoProbeIsRegistered()
    {
        // A configured agent with NO registered probe AND on the exempt list
        // is skipped silently — the operator's explicit "no first-party CLI"
        // hatch. A configured agent that IS on the exempt list but ALSO has a
        // registered probe is still verified (registered probe wins).
        var opts = new CodeyBoxOptions
        {
            AgentClasses =
            [
                new AgentClassOptions
                {
                    Id = "x",
                    Members =
                    [
                        new AgentMembershipOptions { Agent = "aider" },
                        new AgentMembershipOptions { Agent = AgentKind.Copilot.Value },
                        new AgentMembershipOptions { Agent = AgentKind.Claude.Value },
                    ],
                },
            ],
        };

        var probes = BaselineVerificationProbeBuilder.Build(
            opts,
            new ProjectsOptions(),
            AllProbes(),
            new InVmSmokeOptions
            {
                // Both an agent with NO probe (aider) and one WITH a probe (copilot).
                ExemptAgentsWithoutProbe = ["aider", AgentKind.Copilot.Value],
            });

        var labels = probes.Select(p => p.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // aider has no probe AND is exempt → skipped silently.
        Assert.DoesNotContain("aider", labels);
        // copilot has a registered probe → verified despite being exempt.
        Assert.Contains(AgentKind.Copilot.Value, labels);
        Assert.Contains(AgentKind.Claude.Value, labels);
    }

    [Fact]
    public void Build_RunsRegardlessOfSmokeSettings()
    {
        // The bake gate is the durable contract that a freshly cloned VM has
        // every configured agent CLI on PATH. Runtime smoke toggles
        // (CodeyBox:Smoke:Enabled, CodeyBox:Smoke:InVm:Enabled) only govern
        // dispatch-time routing — they MUST NOT remove configured agents from
        // the post-bake check. Pre-regression, disabling smoke produced an
        // empty verification list and the bake reported "ready to clone" with
        // a missing antigravity binary, reintroducing the exit-127 failure
        // mode this work exists to prevent. The current builder ignores smoke
        // options entirely; this test pins that contract.
        var opts = new CodeyBoxOptions
        {
            AgentClasses =
            [
                new AgentClassOptions
                {
                    Id = "x",
                    Members =
                    [
                        new AgentMembershipOptions { Agent = AgentKind.Antigravity.Value },
                        new AgentMembershipOptions { Agent = AgentKind.Claude.Value },
                    ],
                },
            ],
        };

        // Even with the in-VM probe option flagged disabled (back when it was
        // wired) the gate must still surface verification commands.
        var probes = BaselineVerificationProbeBuilder.Build(
            opts,
            new ProjectsOptions(),
            AllProbes(),
            new InVmSmokeOptions { Enabled = false });

        var labels = probes.Select(p => p.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(AgentKind.Antigravity.Value, labels);
        Assert.Contains(AgentKind.Claude.Value, labels);
    }

    [Fact]
    public void Build_OmitsAntigravityVerification_WhenNotConfigured()
    {
        // The bake gate must be silent for agents the operator hasn't opted into.
        // A bake that included `agy --version` for a class without antigravity
        // would fail the bake on every operator who has never installed agy —
        // exactly the regression the original gating exists to prevent.
        var opts = new CodeyBoxOptions
        {
            AgentClasses =
            [
                new AgentClassOptions
                {
                    Id = "no-agy",
                    Members =
                    [
                        new AgentMembershipOptions { Agent = AgentKind.Claude.Value },
                        new AgentMembershipOptions { Agent = AgentKind.Codex.Value },
                    ],
                },
            ],
        };

        var probes = BaselineVerificationProbeBuilder.Build(
            opts,
            new ProjectsOptions(),
            AllProbes());

        var labels = probes.Select(p => p.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(AgentKind.Antigravity.Value, labels);
        Assert.Contains(AgentKind.Claude.Value, labels);
        Assert.Contains(AgentKind.Codex.Value, labels);
    }

    [Fact]
    public void Build_ThrowsWhenProbeHasNoCredentialIndependentStep()
    {
        // A probe-shape bug is still a hard error: the agent IS in the
        // registered-probe set, so the bake gate cannot silently skip it
        // without making the configuration look healthy when it is not.
        var opts = new CodeyBoxOptions
        {
            AgentClasses =
            [
                new AgentClassOptions
                {
                    Id = "custom",
                    Members = [new AgentMembershipOptions { Agent = "aider" }],
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BaselineVerificationProbeBuilder.Build(opts, new ProjectsOptions(), [new CredentialOnlyProbe()]));

        Assert.Contains("configured agent 'aider'", ex.Message);
        Assert.Contains("no credential-independent command", ex.Message);
    }

    [Fact]
    public void Build_PreservesDepthFirstAuditOrderAndIgnoresReferenceCycles()
    {
        var root = new ProjectAuditConfig { AuditAgent = AgentKind.Gemini.Value };
        var first = new ProjectAuditConfig { AuditAgent = AgentKind.Cursor.Value };
        var nested = new ProjectAuditConfig { AuditAgent = AgentKind.Opencode.Value };
        var second = new ProjectAuditConfig { AuditAgent = AgentKind.Copilot.Value };
        root.Profiles = new Dictionary<string, ProjectAuditConfig>
        {
            ["first"] = first,
            ["second"] = second,
        };
        first.Profiles = new Dictionary<string, ProjectAuditConfig> { ["nested"] = nested };
        nested.Profiles = new Dictionary<string, ProjectAuditConfig> { ["cycle"] = root };

        var commands = BaselineVerificationProbeBuilder.Build(
            new CodeyBoxOptions(),
            new ProjectsOptions
            {
                Defaults = new ProjectDefaultsConfig { Audit = root },
            },
            AllProbes());

        Assert.Equal(
            [
                AgentKind.Claude.Value,
                AgentKind.Gemini.Value,
                AgentKind.Cursor.Value,
                AgentKind.Opencode.Value,
                AgentKind.Copilot.Value,
            ],
            commands.Select(command => command.Label));
    }

    [Fact]
    public void Build_HandlesDeepCyclicAuditGraphWithoutRecursion()
    {
        const int depth = 2000;
        var root = new ProjectAuditConfig { AuditAgent = AgentKind.Codex.Value };
        var current = root;
        for (var i = 1; i < depth; i++)
        {
            var child = new ProjectAuditConfig { AuditAgent = AgentKind.Codex.Value };
            current.Profiles = new Dictionary<string, ProjectAuditConfig> { [$"depth-{i}"] = child };
            current = child;
        }
        current.Profiles = new Dictionary<string, ProjectAuditConfig> { ["cycle"] = root };

        var commands = BaselineVerificationProbeBuilder.Build(
            new CodeyBoxOptions(),
            new ProjectsOptions
            {
                Defaults = new ProjectDefaultsConfig { Audit = root },
            },
            AllProbes());

        Assert.Equal(
            [AgentKind.Claude.Value, AgentKind.Codex.Value],
            commands.Select(command => command.Label));
    }

    [Fact]
    public void Build_RejectsDeceptiveProbeAndExemptionEnumerablesAtObservedLimit()
    {
        var excessiveProbes = new DeceptiveReadOnlyList<IInVmSmokeProbe>(
            reportedCount: 0,
            Enumerable.Range(0, BaselineProvisioningLimits.MaximumVerificationCommands + 1)
                .Select(index => (IInVmSmokeProbe)new StaticProbe(
                    $"probe-{index}",
                    [new InVmSmokeStep(["true"])]))
                .ToArray());

        var probeFailure = Assert.Throws<InvalidOperationException>(() =>
            BaselineVerificationProbeBuilder.Build(
                new CodeyBoxOptions(),
                new ProjectsOptions(),
                excessiveProbes));

        Assert.Contains("more than 64 in-VM probes", probeFailure.Message, StringComparison.Ordinal);
        Assert.Equal(BaselineProvisioningLimits.MaximumVerificationCommands + 1, excessiveProbes.EnumeratedCount);

        var excessiveExemptions = new DeceptiveReadOnlyList<string>(
            reportedCount: 0,
            Enumerable.Range(0, BaselineProvisioningLimits.MaximumVerificationCommands + 1)
                .Select(index => $"exempt-{index}")
                .ToArray());

        var exemptionFailure = Assert.Throws<InvalidOperationException>(() =>
            BaselineVerificationProbeBuilder.Build(
                new CodeyBoxOptions(),
                new ProjectsOptions(),
                AllProbes(),
                new InVmSmokeOptions { ExemptAgentsWithoutProbe = excessiveExemptions }));

        Assert.Contains("more than 64 exempt agents", exemptionFailure.Message, StringComparison.Ordinal);
        Assert.Equal(BaselineProvisioningLimits.MaximumVerificationCommands + 1, excessiveExemptions.EnumeratedCount);
    }

    [Fact]
    public void Build_RejectsDuplicateProbeEnumerableWithoutUnboundedEnumeration()
    {
        var duplicateProbes = new DeceptiveReadOnlyList<IInVmSmokeProbe>(
            reportedCount: 0,
            Enumerable.Range(0, BaselineProvisioningLimits.MaximumVerificationCommands + 1)
                .Select(_ => (IInVmSmokeProbe)new StaticProbe(
                    "duplicate",
                    [new InVmSmokeStep(["true"])]))
                .ToArray());

        var failure = Assert.Throws<InvalidOperationException>(() =>
            BaselineVerificationProbeBuilder.Build(
                new CodeyBoxOptions(),
                new ProjectsOptions(),
                duplicateProbes));

        Assert.Contains("duplicate in-VM probes", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, duplicateProbes.EnumeratedCount);
    }

    [Fact]
    public void Build_BoundsInspectedProbeStepsWithoutTrustingCount()
    {
        var steps = new DeceptiveReadOnlyList<InVmSmokeStep>(
            reportedCount: 0,
            Enumerable.Range(0, BaselineProvisioningLimits.MaximumVerificationCommands + 1)
                .Select(_ => new InVmSmokeStep(["custom", "status"], Stdin: "credential"))
                .ToArray());
        var options = new CodeyBoxOptions
        {
            AgentClasses =
            [
                new AgentClassOptions
                {
                    Id = "custom",
                    Members = [new AgentMembershipOptions { Agent = "custom" }],
                },
            ],
        };

        var failure = Assert.Throws<InvalidOperationException>(() =>
            BaselineVerificationProbeBuilder.Build(
                options,
                new ProjectsOptions(),
                [new StaticProbe("custom", steps)]));

        Assert.Contains("more than 64 steps", failure.Message, StringComparison.Ordinal);
        Assert.Equal(BaselineProvisioningLimits.MaximumVerificationCommands + 1, steps.EnumeratedCount);
    }

    [Fact]
    public void Build_CountsDuplicateConfiguredAgentsAgainstInspectionBudget()
    {
        var options = new CodeyBoxOptions
        {
            AgentClasses =
            [
                new AgentClassOptions
                {
                    Id = "oversized",
                    Members = Enumerable
                        .Repeat(
                            new AgentMembershipOptions { Agent = AgentKind.Claude.Value },
                            BaselineVerificationProbeBuilder.MaximumConfigurationEntriesInspected)
                        .ToList(),
                },
            ],
        };

        var failure = Assert.Throws<InvalidOperationException>(() =>
            BaselineVerificationProbeBuilder.Build(options, new ProjectsOptions(), AllProbes()));

        Assert.Contains(
            $"more than {BaselineVerificationProbeBuilder.MaximumConfigurationEntriesInspected} configured entries",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Build_SnapshotsValidUnicodeArgvAndRejectsUtf8Overflow()
    {
        var unicode = new string('\u00e9', 2048);
        var argv = new List<string> { "custom", unicode };
        var options = OptionsForAgent("custom");

        var commands = BaselineVerificationProbeBuilder.Build(
            options,
            new ProjectsOptions(),
            [
                new StaticProbe("custom", [new InVmSmokeStep(argv)]),
                new ClaudeInVmSmokeProbe(),
            ]);
        argv[1] = "changed";

        Assert.Equal(unicode, commands[0].Argv[1]);

        var overflow = new string('\u00e9', 2049);
        var failure = Assert.Throws<InvalidOperationException>(() =>
            BaselineVerificationProbeBuilder.Build(
                options,
                new ProjectsOptions(),
                [
                    new StaticProbe("custom", [new InVmSmokeStep(["custom", overflow])]),
                    new ClaudeInVmSmokeProbe(),
                ]));
        Assert.Contains("4096 UTF-8 bytes", failure.Message, StringComparison.Ordinal);

        var invalidUnicode = Assert.Throws<InvalidOperationException>(() =>
            BaselineVerificationProbeBuilder.Build(
                options,
                new ProjectsOptions(),
                [
                    new StaticProbe("custom", [new InVmSmokeStep(["custom", "\ud800"])]),
                    new ClaudeInVmSmokeProbe(),
                ]));
        Assert.Contains("not valid Unicode", invalidUnicode.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsControlCharactersAndOversizedFailureHint()
    {
        var options = OptionsForAgent("custom");

        var controlFailure = Assert.Throws<InvalidOperationException>(() =>
            BaselineVerificationProbeBuilder.Build(
                options,
                new ProjectsOptions(),
                [
                    new StaticProbe("custom", [new InVmSmokeStep(["custom", "bad\nargument"])]),
                    new ClaudeInVmSmokeProbe(),
                ]));
        Assert.Contains("control characters", controlFailure.Message, StringComparison.Ordinal);

        var hintFailure = Assert.Throws<InvalidOperationException>(() =>
            BaselineVerificationProbeBuilder.Build(
                options,
                new ProjectsOptions(),
                [
                    new StaticProbe(
                        "custom",
                        [new InVmSmokeStep(["custom", "--version"], FailureHint: new string('h', 4097))]),
                    new ClaudeInVmSmokeProbe(),
                ]));
        Assert.Contains("failure hint", hintFailure.Message, StringComparison.Ordinal);
        Assert.Contains("4096 UTF-8 bytes", hintFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsAggregateVerificationTextAboveSharedLimit()
    {
        var members = Enumerable
            .Range(0, BaselineProvisioningLimits.MaximumVerificationCommands - 1)
            .Select(index => new AgentMembershipOptions { Agent = $"custom-{index}" })
            .ToList();
        var options = new CodeyBoxOptions
        {
            AgentClasses = [new AgentClassOptions { Id = "aggregate", Members = members }],
        };
        var largeArgument = new string('a', BaselineProvisioningLimits.MaximumVerificationTextUtf8Bytes);
        var probes = members
            .Select(member => (IInVmSmokeProbe)new StaticProbe(
                member.Agent,
                [
                    new InVmSmokeStep(
                        [member.Agent, largeArgument],
                        FailureHint: new string('h', 100)),
                ]))
            .Append(new ClaudeInVmSmokeProbe())
            .ToArray();

        var failure = Assert.Throws<InvalidOperationException>(() =>
            BaselineVerificationProbeBuilder.Build(options, new ProjectsOptions(), probes));

        Assert.Contains(
            BaselineProvisioningLimits.MaximumAggregateVerificationTextUtf8Bytes.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            failure.Message,
            StringComparison.Ordinal);
        Assert.Contains("in aggregate", failure.Message, StringComparison.Ordinal);
    }

    private static CodeyBoxOptions OptionsForAgent(string agent) => new()
    {
        AgentClasses =
        [
            new AgentClassOptions
            {
                Id = "custom",
                Members = [new AgentMembershipOptions { Agent = agent }],
            },
        ],
    };

    private static IReadOnlyList<IInVmSmokeProbe> AllProbes() =>
    [
        new ClaudeInVmSmokeProbe(),
        new CopilotInVmSmokeProbe(),
        new CodexInVmSmokeProbe(),
        new GeminiInVmSmokeProbe(),
        new CursorInVmSmokeProbe(),
        new OpencodeInVmSmokeProbe(),
        new AntigravityInVmSmokeProbe(),
    ];

    private sealed class CredentialOnlyProbe : IInVmSmokeProbe
    {
        public AgentKind Kind => new("aider");

        public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential) =>
        [
            new(["aider", "status"], Stdin: "{}"),
        ];
    }

    private sealed class StaticProbe(
        string kind,
        IReadOnlyList<InVmSmokeStep> steps) : IInVmSmokeProbe
    {
        public AgentKind Kind => new(kind);

        public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential) => steps;
    }

    private sealed class DeceptiveReadOnlyList<T>(
        int reportedCount,
        IReadOnlyList<T> values) : IReadOnlyList<T>
    {
        public int Count => reportedCount;
        public T this[int index] => values[index];
        internal int EnumeratedCount { get; private set; }

        public IEnumerator<T> GetEnumerator()
        {
            foreach (var value in values)
            {
                EnumeratedCount++;
                yield return value;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
