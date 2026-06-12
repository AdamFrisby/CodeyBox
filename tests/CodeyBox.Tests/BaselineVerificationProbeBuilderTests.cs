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
}
