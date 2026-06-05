using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Api;

namespace CodeyBox.Tests;

public sealed class AgentClassesConfigBuilderInstanceTests
{
    [Fact]
    public void Build_AllowsSameKindMembersWhenInstanceIdsAreDistinct()
    {
        var classes = new List<AgentClassOptions>
        {
            new()
            {
                Id = "frontier",
                Members =
                {
                    new AgentMembershipOptions
                    {
                        Agent = "claude",
                        InstanceId = "acct-a",
                        QualityScore = 100,
                    },
                    new AgentMembershipOptions
                    {
                        Agent = "claude",
                        InstanceId = "acct-b",
                        QualityScore = 99,
                    },
                },
            },
        };
        var instances = new List<AgentInstanceOptions>
        {
            new()
            {
                Id = "acct-a",
                Agent = "claude",
                TokenEnvironmentVariable = "CLAUDE_ACCT_A_TOKEN",
            },
            new()
            {
                Id = "acct-b",
                Agent = "claude",
                CredentialFilePath = "/var/lib/codeybox/claude-acct-b.json",
            },
        };

        var catalog = AgentClassesConfigBuilder.Build(classes, instances, NullLogger.Instance);

        var members = catalog[0].Members;
        Assert.Equal("claude/acct-a", members[0].RouteKey);
        Assert.Equal("CLAUDE_ACCT_A_TOKEN", members[0].CredentialReference?.TokenEnvironmentVariable);
        Assert.Equal("claude/acct-b", members[1].RouteKey);
        Assert.Equal("/var/lib/codeybox/claude-acct-b.json", members[1].CredentialReference?.FilePath);
    }

    [Fact]
    public void Build_RejectsDuplicateDefaultSameKindMembersWithoutInstanceIds()
    {
        var classes = new List<AgentClassOptions>
        {
            new()
            {
                Id = "frontier",
                Members =
                {
                    new AgentMembershipOptions
                    {
                        Agent = "claude",
                        QualityScore = 100,
                    },
                    new AgentMembershipOptions
                    {
                        Agent = "claude",
                        QualityScore = 99,
                    },
                },
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AgentClassesConfigBuilder.Build(classes, NullLogger.Instance));
        Assert.Contains("distinct InstanceId", ex.Message);
    }

    [Fact]
    public void Build_AllowsInlineCredentialReferenceWithoutTopLevelInstance()
    {
        var classes = new List<AgentClassOptions>
        {
            new()
            {
                Id = "frontier",
                Members =
                {
                    new AgentMembershipOptions
                    {
                        Agent = "codex",
                        InstanceId = "team-a",
                        QualityScore = 100,
                        AuthJsonEnvironmentVariable = "CODEX_TEAM_A_AUTH_JSON",
                        DestinationPath = "/tmp/codeybox/codex/auth.json",
                    },
                },
            },
        };

        var catalog = AgentClassesConfigBuilder.Build(classes, NullLogger.Instance);

        var member = Assert.Single(catalog[0].Members);
        Assert.Equal("codex/team-a", member.RouteKey);
        Assert.Equal("CODEX_TEAM_A_AUTH_JSON", member.CredentialReference?.AuthJsonEnvironmentVariable);
        Assert.Equal("/tmp/codeybox/codex/auth.json", member.CredentialReference?.DestinationPath);
    }

    [Fact]
    public void Build_RejectsFullRouteKeyWhosePrefixDoesNotMatchAgent()
    {
        var classes = new List<AgentClassOptions>
        {
            new()
            {
                Id = "frontier",
                Members =
                {
                    new AgentMembershipOptions
                    {
                        Agent = "claude",
                        InstanceId = "codex/team-a",
                        QualityScore = 100,
                    },
                },
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AgentClassesConfigBuilder.Build(classes, NullLogger.Instance));
        Assert.Contains("route prefix must match agent 'claude'", ex.Message);
    }
}
