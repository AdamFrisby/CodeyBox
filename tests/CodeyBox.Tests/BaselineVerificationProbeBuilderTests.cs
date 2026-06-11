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
            AllProbes(),
            // Opt copilot back in for this assertion — by default it is exempt.
            new InVmSmokeOptions { ExemptAgentsWithoutProbe = [] });

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
    public void Build_SkipsConfiguredAgentWithNoProbe_RatherThanThrowing()
    {
        // Coverage policy already benches missing-probe agents under the
        // dedicated source so the router routes past them. The builder must
        // match that decision (skip), not pre-empt it by failing the bake.
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

        var probes = BaselineVerificationProbeBuilder.Build(opts, new ProjectsOptions(), AllProbes());

        Assert.Contains(probes, p => p.Label == AgentKind.Claude.Value);
        Assert.DoesNotContain(probes, p => p.Label == "aider");
    }

    [Fact]
    public void Build_SkipsExemptAgentsWithoutProbe()
    {
        // Default ExemptAgentsWithoutProbe includes copilot. The exempt agent
        // must not appear in the verification list even when it is configured
        // — failing the bake on an exempt agent would directly contradict the
        // coverage policy. Other configured agents are still verified.
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
            new InVmSmokeOptions { ExemptAgentsWithoutProbe = [AgentKind.Copilot.Value] });

        var labels = probes.Select(p => p.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(AgentKind.Copilot.Value, labels);
        Assert.Contains(AgentKind.Claude.Value, labels);
    }

    [Fact]
    public void Build_ReturnsEmpty_WhenMasterSmokeSwitchOff()
    {
        var opts = new CodeyBoxOptions
        {
            AgentClasses =
            [
                new AgentClassOptions
                {
                    Id = "x",
                    Members = [new AgentMembershipOptions { Agent = AgentKind.Claude.Value }],
                },
            ],
        };

        var probes = BaselineVerificationProbeBuilder.Build(
            opts,
            new ProjectsOptions(),
            AllProbes(),
            new InVmSmokeOptions(),
            new SmokeOptionsSnapshot(new SmokeOptions { Enabled = false }));

        Assert.Empty(probes);
    }

    [Fact]
    public void Build_ReturnsEmpty_WhenInVmSmokeDisabled()
    {
        var opts = new CodeyBoxOptions
        {
            AgentClasses =
            [
                new AgentClassOptions
                {
                    Id = "x",
                    Members = [new AgentMembershipOptions { Agent = AgentKind.Claude.Value }],
                },
            ],
        };

        var probes = BaselineVerificationProbeBuilder.Build(
            opts,
            new ProjectsOptions(),
            AllProbes(),
            new InVmSmokeOptions { Enabled = false });

        Assert.Empty(probes);
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
