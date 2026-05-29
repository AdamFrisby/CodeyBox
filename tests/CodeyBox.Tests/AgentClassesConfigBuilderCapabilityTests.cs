using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Api;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="AgentClassesConfigBuilder"/> capability parsing.
/// Covers normalisation (trim, drop empty, de-dupe case-insensitively) and
/// the default-empty path when the config block omits Capabilities entirely.
/// </summary>
public sealed class AgentClassesConfigBuilderCapabilityTests
{
    [Fact]
    public void Build_PropagatesCapabilitiesOntoMembership()
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
                        Capabilities = new() { "sensitive", "architectural" },
                    },
                },
            },
        };

        var catalog = AgentClassesConfigBuilder.Build(classes, NullLogger.Instance);

        var member = Assert.Single(catalog[0].Members);
        Assert.Equal(new[] { "sensitive", "architectural" }, member.Capabilities);
    }

    [Fact]
    public void Build_TrimsAndDropsEmptyCapabilities()
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
                        Capabilities = new() { "  sensitive  ", "", "  ", "architectural" },
                    },
                },
            },
        };

        var catalog = AgentClassesConfigBuilder.Build(classes, NullLogger.Instance);

        var member = Assert.Single(catalog[0].Members);
        Assert.Equal(new[] { "sensitive", "architectural" }, member.Capabilities);
    }

    [Fact]
    public void Build_DedupesCapabilitiesCaseInsensitive()
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
                        Capabilities = new() { "sensitive", "Sensitive", "SENSITIVE", "architectural" },
                    },
                },
            },
        };

        var catalog = AgentClassesConfigBuilder.Build(classes, NullLogger.Instance);

        var member = Assert.Single(catalog[0].Members);
        Assert.Equal(new[] { "sensitive", "architectural" }, member.Capabilities);
    }

    [Fact]
    public void Build_DefaultsCapabilitiesToEmpty_WhenOmitted()
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
                },
            },
        };

        var catalog = AgentClassesConfigBuilder.Build(classes, NullLogger.Instance);

        var member = Assert.Single(catalog[0].Members);
        Assert.Empty(member.Capabilities);
    }
}
