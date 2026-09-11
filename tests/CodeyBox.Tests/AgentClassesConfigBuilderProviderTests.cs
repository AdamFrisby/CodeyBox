using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Copilot;
using CodeyBox.Api;

namespace CodeyBox.Tests;

/// <summary>
/// Configuration validation for per-member Copilot provider references: inline
/// and instance-level mapping, the BYOK-harness + native-subscription pair,
/// and fail-fast startup errors for unresolvable or misplaced references.
/// </summary>
public sealed class AgentClassesConfigBuilderProviderTests
{
    private static Dictionary<string, CopilotProviderOptions> Providers(
        params (string Name, string BaseUrl)[] entries)
    {
        var catalog = new Dictionary<string, CopilotProviderOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, baseUrl) in entries)
            catalog[name] = new CopilotProviderOptions { BaseUrl = baseUrl };
        return catalog;
    }

    private static AgentClassOptions ClassWith(params AgentMembershipOptions[] members)
    {
        var cls = new AgentClassOptions { Id = "frontier" };
        cls.Members.AddRange(members);
        return cls;
    }

    private static AgentMembershipOptions CopilotMember(
        string? instanceId, int qualityScore, string? provider = null) => new()
        {
            Agent = "copilot",
            InstanceId = instanceId,
            QualityScore = qualityScore,
            Provider = provider,
        };

    [Fact]
    public void Build_MapsInlineProviderReference()
    {
        var classes = new List<AgentClassOptions>
        {
            ClassWith(CopilotMember("harness", 100, "byok")),
        };

        var catalog = AgentClassesConfigBuilder.Build(
            classes, [], NullLogger.Instance, Providers(("byok", "https://opencode.ai/zen/go/v1")));

        Assert.Equal("byok", catalog[0].Members[0].ProviderReference?.Name);
        Assert.Equal("copilot/harness", catalog[0].Members[0].RouteKey);
    }

    [Fact]
    public void Build_MapsInstanceLevelProvider_AndInlineWins()
    {
        var classes = new List<AgentClassOptions>
        {
            ClassWith(
                CopilotMember("harness", 100, "byok-inline"),
                CopilotMember("sub", 99)),
        };
        var instances = new List<AgentInstanceOptions>
        {
            new() { Id = "harness", Agent = "copilot", Provider = "byok-instance" },
            new() { Id = "sub", Agent = "copilot", Provider = "byok-instance" },
        };
        var providers = Providers(
            ("byok-inline", "https://inline.example/v1"),
            ("byok-instance", "https://instance.example/v1"));

        var catalog = AgentClassesConfigBuilder.Build(classes, instances, NullLogger.Instance, providers);

        // Member-inline wins over the shared instance entry (credential-field precedence).
        Assert.Equal("byok-inline", catalog[0].Members[0].ProviderReference?.Name);
        // Instance-level entry fills members that name none inline.
        Assert.Equal("byok-instance", catalog[0].Members[1].ProviderReference?.Name);
    }

    [Fact]
    public void Build_AllowsByokHarnessAndNativeSubscriptionSideBySide()
    {
        var classes = new List<AgentClassOptions>
        {
            ClassWith(
                CopilotMember("harness", 100, "byok"),
                CopilotMember("sub", 99)),
        };

        var catalog = AgentClassesConfigBuilder.Build(
            classes, [], NullLogger.Instance, Providers(("byok", "https://opencode.ai/zen/go/v1")));

        var members = catalog[0].Members;
        Assert.Equal(2, members.Count);
        Assert.Equal("copilot/harness", members[0].RouteKey);
        Assert.Equal("byok", members[0].ProviderReference?.Name);
        Assert.Equal("copilot/sub", members[1].RouteKey);
        Assert.Null(members[1].ProviderReference);
    }

    [Fact]
    public void Build_MemberWithoutProvider_NeedsNoCatalog()
    {
        var classes = new List<AgentClassOptions>
        {
            ClassWith(CopilotMember("sub", 100)),
        };

        // No provider anywhere: existing configs build identically with no change.
        var catalog = AgentClassesConfigBuilder.Build(classes, NullLogger.Instance);

        Assert.Null(catalog[0].Members[0].ProviderReference);
    }

    [Fact]
    public void Build_RejectsUnresolvableProviderReference()
    {
        var classes = new List<AgentClassOptions>
        {
            ClassWith(CopilotMember("harness", 100, "missing")),
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AgentClassesConfigBuilder.Build(
                classes, [], NullLogger.Instance, Providers(("byok", "https://opencode.ai/zen/go/v1"))));

        Assert.Contains("missing", ex.Message);
        Assert.Contains("CodeyBox:Copilot:Providers:missing", ex.Message);
    }

    [Fact]
    public void Build_RejectsProviderReferenceWithoutAnyCatalog()
    {
        var classes = new List<AgentClassOptions>
        {
            ClassWith(CopilotMember("harness", 100, "byok")),
        };

        // Fail closed: without a catalog the name cannot resolve, so the
        // member must not silently degrade to another backend.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AgentClassesConfigBuilder.Build(classes, NullLogger.Instance));

        Assert.Contains("byok", ex.Message);
    }

    [Fact]
    public void Build_RejectsProviderReferenceOnNonCopilotMember()
    {
        var cls = new AgentClassOptions { Id = "frontier" };
        cls.Members.Add(new AgentMembershipOptions
        {
            Agent = "codex",
            InstanceId = "team",
            QualityScore = 100,
            Provider = "byok",
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AgentClassesConfigBuilder.Build(
                [cls], [], NullLogger.Instance, Providers(("byok", "https://opencode.ai/zen/go/v1"))));

        Assert.Contains("copilot", ex.Message);
    }
}
