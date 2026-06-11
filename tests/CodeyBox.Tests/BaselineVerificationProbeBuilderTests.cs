using CodeyBox.Agents.Antigravity;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Copilot;
using CodeyBox.Agents.Cursor;
using CodeyBox.Agents.Gemini;
using CodeyBox.Agents.Opencode;
using CodeyBox.Api;
using CodeyBox.Core;
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

        var probes = BaselineVerificationProbeBuilder.Build(opts, projects, AllProbes());

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
    public void Build_ThrowsWhenConfiguredAgentHasNoProbe()
    {
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
            BaselineVerificationProbeBuilder.Build(opts, new ProjectsOptions(), AllProbes()));

        Assert.Contains("configured agent 'aider'", ex.Message);
        Assert.Contains("no IInVmSmokeProbe is registered", ex.Message);
    }

    [Fact]
    public void Build_ThrowsWhenProbeHasNoCredentialIndependentStep()
    {
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
