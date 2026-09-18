using CodeyBox.Api;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="AgentClassesOverrideResolver.ApplySandboxClassesTo"/>:
/// enforces REPLACE semantics for <c>CodeyBox:SandboxClasses</c> when a
/// higher-precedence configuration layer supplies the section, so a shorter
/// operator override cannot resurrect the base layer's trailing members.
/// </summary>
public sealed class SandboxClassesOverrideResolverTests
{
    [Fact]
    public void OverrideWithFewerMembers_DoesNotResurrectBaseMember()
    {
        var baseLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:SandboxClasses:0:Id"] = "default",
            ["CodeyBox:SandboxClasses:0:Members:0:MemberId"] = "local",
            ["CodeyBox:SandboxClasses:0:Members:0:ProviderKind"] = "incus",
            ["CodeyBox:SandboxClasses:0:Members:0:Capacity"] = "4",
            ["CodeyBox:SandboxClasses:0:Members:0:PreferenceScore"] = "100",
            ["CodeyBox:SandboxClasses:0:Members:1:MemberId"] = "remote",
            ["CodeyBox:SandboxClasses:0:Members:1:ProviderKind"] = "multipass",
            ["CodeyBox:SandboxClasses:0:Members:1:Capacity"] = "4",
            ["CodeyBox:SandboxClasses:0:Members:1:PreferenceScore"] = "10",
        };

        var overrideLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:SandboxClasses:0:Id"] = "default",
            ["CodeyBox:SandboxClasses:0:Members:0:MemberId"] = "local",
            ["CodeyBox:SandboxClasses:0:Members:0:ProviderKind"] = "incus",
            ["CodeyBox:SandboxClasses:0:Members:0:Capacity"] = "4",
            ["CodeyBox:SandboxClasses:0:Members:0:PreferenceScore"] = "100",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(baseLayer)
            .AddInMemoryCollection(overrideLayer)
            .Build();

        var bound = new CodeyBoxOptions();
        config.GetSection("CodeyBox").Bind(bound);
        Assert.Contains(bound.SandboxClasses[0].Members, m => m.MemberId == "remote");

        AgentClassesOverrideResolver.ApplySandboxClassesTo(bound, config);

        var resolved = Assert.Single(bound.SandboxClasses);
        Assert.Equal(new[] { "local" }, resolved.Members.Select(m => m.MemberId));
        Assert.DoesNotContain(resolved.Members, m => m.MemberId == "remote");
    }

    [Fact]
    public void NoSandboxOverride_LeavesBaseClassesIntact()
    {
        var baseLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:SandboxClasses:0:Id"] = "default",
            ["CodeyBox:SandboxClasses:0:Members:0:MemberId"] = "local",
            ["CodeyBox:SandboxClasses:0:Members:0:ProviderKind"] = "incus",
        };
        var overrideLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:GitRootDirectory"] = "/tmp/other",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(baseLayer)
            .AddInMemoryCollection(overrideLayer)
            .Build();

        var bound = new CodeyBoxOptions();
        config.GetSection("CodeyBox").Bind(bound);
        AgentClassesOverrideResolver.ApplySandboxClassesTo(bound, config);

        var resolved = Assert.Single(bound.SandboxClasses);
        Assert.Equal("default", resolved.Id);
        Assert.Equal("local", Assert.Single(resolved.Members).MemberId);
    }

    [Fact]
    public void OverrideWithExplicitlyEmptyArray_ClearsBaseClasses()
    {
        var baseLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:SandboxClasses:0:Id"] = "default",
            ["CodeyBox:SandboxClasses:0:Members:0:MemberId"] = "local",
        };

        // EmptySectionConfigurationSource mimics what JsonConfigurationProvider
        // does for "SandboxClasses": [] — it stores the section key itself
        // (value=null) and no child keys at all.
        var emptyOverride = new EmptySectionConfigurationSource(
            "CodeyBox:SandboxClasses");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(baseLayer)
            .Add(emptyOverride)
            .Build();

        var bound = new CodeyBoxOptions();
        config.GetSection("CodeyBox").Bind(bound);
        // Sanity check: positional merge would keep the base 'default' class.
        Assert.Single(bound.SandboxClasses);

        AgentClassesOverrideResolver.ApplySandboxClassesTo(bound, config);

        Assert.Empty(bound.SandboxClasses);
    }

    private sealed class EmptySectionConfigurationSource(string key) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) =>
            new EmptySectionConfigurationProvider(key);
    }

    private sealed class EmptySectionConfigurationProvider : ConfigurationProvider
    {
        public EmptySectionConfigurationProvider(string key)
        {
            Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [key] = null };
        }
    }
}
