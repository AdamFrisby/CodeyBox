using CodeyBox.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="AgentClassesOverrideResolver"/>: enforces REPLACE
/// semantics for <c>CodeyBox:AgentClasses</c> when a higher-precedence
/// configuration layer supplies the section. Regression coverage for the
/// 2026-06-04 incident in which a 3-member operator override silently
/// re-surfaced the base array's 4th member (gemini) because the .NET
/// configuration binder merges arrays by index.
/// </summary>
public sealed class AgentClassesOverrideResolverTests
{
    [Fact]
    public void OverrideWithFewerMembers_DoesNotResurrectBaseMember()
    {
        // Base: 4-member class (claude, codex, cursor, gemini).
        var baseLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier-coding",
            ["CodeyBox:AgentClasses:0:DisplayName"] = "Frontier coding",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:1:Agent"] = "codex",
            ["CodeyBox:AgentClasses:0:Members:1:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:1:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:2:Agent"] = "cursor",
            ["CodeyBox:AgentClasses:0:Members:2:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:2:QualityScore"] = "98",
            ["CodeyBox:AgentClasses:0:Members:3:Agent"] = "gemini",
            ["CodeyBox:AgentClasses:0:Members:3:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:3:QualityScore"] = "95",
            ["CodeyBox:AgentClasses:0:Members:3:ReasoningMode"] = "high",
        };

        // Operator override: 3 members (cursor removed, gemini intentionally
        // not listed). Under positional merge this would re-expose
        // gemini from baseLayer[3]; the resolver must prevent that.
        var overrideLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "codex-xhigh",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "codex",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:1:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:1:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:1:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:2:Agent"] = "opencode",
            ["CodeyBox:AgentClasses:0:Members:2:Billing"] = "PayPerApi",
            ["CodeyBox:AgentClasses:0:Members:2:QualityScore"] = "70",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(baseLayer)
            .AddInMemoryCollection(overrideLayer)
            .Build();

        // Confirm the framework's default bind exhibits the broken behaviour
        // we're guarding against — this is the regression that motivated the
        // fix. If the framework ever changes this, the test still passes
        // because the resolver's REPLACE semantics is what we actually check.
        var defaultBound = new CodeyBoxOptions();
        config.GetSection("CodeyBox").Bind(defaultBound);
        Assert.Contains(defaultBound.AgentClasses[0].Members, m => m.Agent == "gemini");

        // Apply the resolver: the operator's 3-member view fully replaces
        // the base 4-member array. gemini is gone, no positional bleed.
        AgentClassesOverrideResolver.ApplyTo(defaultBound, config);

        var resolved = Assert.Single(defaultBound.AgentClasses);
        Assert.Equal("codex-xhigh", resolved.Id);
        Assert.Equal(new[] { "codex", "claude", "opencode" },
            resolved.Members.Select(m => m.Agent));
        Assert.DoesNotContain(resolved.Members, m => m.Agent == "gemini");
        Assert.DoesNotContain(resolved.Members, m => m.Agent == "cursor");
    }

    [Fact]
    public void NoOverride_LeavesBaseClassesIntact()
    {
        var baseLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:1:Agent"] = "codex",
            ["CodeyBox:AgentClasses:0:Members:1:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:1:QualityScore"] = "100",
        };
        // Override layer touches an unrelated section: must NOT trigger
        // AgentClasses replacement.
        var overrideLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:GitRootDirectory"] = "/tmp/other",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(baseLayer)
            .AddInMemoryCollection(overrideLayer)
            .Build();

        var options = new CodeyBoxOptions();
        config.GetSection("CodeyBox").Bind(options);
        AgentClassesOverrideResolver.ApplyTo(options, config);

        var resolved = Assert.Single(options.AgentClasses);
        Assert.Equal("frontier", resolved.Id);
        Assert.Equal(new[] { "claude", "codex" }, resolved.Members.Select(m => m.Agent));
    }

    [Fact]
    public void OverrideReplacesEntireClassArray_NotJustMembers()
    {
        // Base provides two classes; override provides only one. Under
        // positional merge, base[1] would bleed through. Under REPLACE
        // semantics, the override defines the complete class set.
        var baseLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:1:Id"] = "bulk",
            ["CodeyBox:AgentClasses:1:Members:0:Agent"] = "haiku",
            ["CodeyBox:AgentClasses:1:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:1:Members:0:QualityScore"] = "50",
        };
        var overrideLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "codex-only",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "codex",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(baseLayer)
            .AddInMemoryCollection(overrideLayer)
            .Build();

        var options = new CodeyBoxOptions();
        config.GetSection("CodeyBox").Bind(options);
        AgentClassesOverrideResolver.ApplyTo(options, config);

        var resolved = Assert.Single(options.AgentClasses);
        Assert.Equal("codex-only", resolved.Id);
        Assert.DoesNotContain(options.AgentClasses, c => c.Id == "bulk");
    }

    [Fact]
    public void OverridePreservesNestedCapabilitiesArrayWithoutPositionalMerge()
    {
        var baseLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:0:Capabilities:0"] = "sensitive",
            ["CodeyBox:AgentClasses:0:Members:0:Capabilities:1"] = "architectural",
            ["CodeyBox:AgentClasses:0:Members:0:Capabilities:2"] = "audit",
        };
        // Override drops the 'audit' capability by listing only two entries.
        // Without REPLACE the 'audit' tag would bleed through from base[2].
        var overrideLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:0:Capabilities:0"] = "sensitive",
            ["CodeyBox:AgentClasses:0:Members:0:Capabilities:1"] = "architectural",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(baseLayer)
            .AddInMemoryCollection(overrideLayer)
            .Build();

        var options = new CodeyBoxOptions();
        config.GetSection("CodeyBox").Bind(options);
        AgentClassesOverrideResolver.ApplyTo(options, config);

        var member = options.AgentClasses[0].Members[0];
        Assert.Equal(new[] { "sensitive", "architectural" }, member.Capabilities);
        Assert.DoesNotContain("audit", member.Capabilities);
    }

    [Fact]
    public void IOptionsPipeline_RunsPostConfigureSoOverrideRunsEndToEnd()
    {
        // Mirrors the Program.cs wiring: AddOptions().Bind(...).PostConfigure(...).
        // Confirms the resolver is invoked through the standard
        // IOptions<CodeyBoxOptions> resolution path — i.e. anyone reading
        // .Value gets the REPLACE-applied list, not the positionally-merged one.
        var baseLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:1:Agent"] = "codex",
            ["CodeyBox:AgentClasses:0:Members:1:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:1:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:2:Agent"] = "gemini",
            ["CodeyBox:AgentClasses:0:Members:2:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:2:QualityScore"] = "95",
            ["CodeyBox:AgentClasses:0:Members:2:ReasoningMode"] = "high",
        };
        var overrideLayer = new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "codex",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
            ["CodeyBox:AgentClasses:0:Members:1:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:1:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:1:QualityScore"] = "100",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(baseLayer)
            .AddInMemoryCollection(overrideLayer)
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<CodeyBoxOptions>()
            .Bind(config.GetSection("CodeyBox"))
            .PostConfigure(opts => AgentClassesOverrideResolver.ApplyTo(opts, config));
        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;
        var cls = Assert.Single(resolved.AgentClasses);
        Assert.Equal(new[] { "codex", "claude" }, cls.Members.Select(m => m.Agent));
        Assert.DoesNotContain(cls.Members, m => m.Agent == "gemini");
    }
}
