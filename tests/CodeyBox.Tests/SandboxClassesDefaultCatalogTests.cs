using CodeyBox.Core;
using CodeyBox.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// With no <c>SandboxClasses</c> configured, the catalog synthesizes a
/// default single-member class from <c>CodeyBox:SandboxProvider</c> so the
/// placement path selects the configured provider without requiring new
/// configuration.
/// </summary>
public sealed class SandboxClassesDefaultCatalogTests
{
    [Fact]
    public void Synthesize_SingleMemberFromConfiguredKind()
    {
        var catalog = SandboxClassesDefaultCatalog.Synthesize(
            "multipass",
            ["baseline-bake", "teardown"],
            NullLogger.Instance,
            6);

        var sandboxClass = Assert.Single(catalog);
        Assert.Equal(SandboxClassesDefaultCatalog.DefaultClassId, sandboxClass.Id);
        var member = Assert.Single(sandboxClass.Members);
        Assert.Equal(SandboxClassesDefaultCatalog.DefaultMemberId, member.MemberId);
        Assert.Equal("multipass", member.ProviderKind);
    }

    [Fact]
    public void Synthesize_NormalizesKindAndCapabilities()
    {
        var catalog = SandboxClassesDefaultCatalog.Synthesize(
            "  Multipass ",
            [" suspend-resume ", "SUSPEND-RESUME", "", "  "],
            NullLogger.Instance,
            4);

        var member = Assert.Single(Assert.Single(catalog).Members);
        Assert.Equal("multipass", member.ProviderKind);
        Assert.Equal(["suspend-resume"], member.Capabilities.ToList());
    }

    [Fact]
    public void Synthesize_AcceptsEveryProfileAndCredential()
    {
        // Asserted through the eligibility gate the decider actually calls,
        // not by shape: an empty Credentials list reads as "holds nothing",
        // so declaring none excluded the only member from every
        // credential-bearing phase and left the fleet unplaceable.
        var catalog = SandboxClassesDefaultCatalog.Synthesize(
            "incus", [], NullLogger.Instance, 2);

        var member = Assert.Single(Assert.Single(catalog).Members).ToPlacementMember();
        Assert.True(ExecutorEligibility.AcceptsNetworkProfile(member, "restricted"));
        Assert.True(ExecutorEligibility.HoldsCredential(member, "copilot"));
        Assert.True(ExecutorEligibility.HoldsCredential(member, "claude"));
    }

    [Fact]
    public void Synthesize_CapacityCarriesResolvedSandboxCeiling()
    {
        var catalog = SandboxClassesDefaultCatalog.Synthesize(
            "process", [], NullLogger.Instance, 8);

        var member = Assert.Single(Assert.Single(catalog).Members);
        Assert.Equal(8, member.Capacity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Synthesize_NonPositiveCapacity_Throws(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SandboxClassesDefaultCatalog.Synthesize("process", [], NullLogger.Instance, capacity));
    }

    [Fact]
    public void Synthesize_BlankKind_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            SandboxClassesDefaultCatalog.Synthesize("  ", [], NullLogger.Instance, 2));
    }
}
