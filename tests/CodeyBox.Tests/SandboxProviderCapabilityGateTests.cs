using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// The provider-capability gate drops well-known operation claims the backing
/// provider does not implement, while operator clearance tags pass through on
/// the member declaration alone.
/// </summary>
public sealed class SandboxProviderCapabilityGateTests
{
    [Fact]
    public void ApplyProviderCapabilities_DropsWellKnownTagProviderLacks()
    {
        var member = SandboxPlacementTestMembers.Member("a", "a", capabilities: [SandboxCapabilities.SuspendResume]);
        var provider = new PlacementFakeSandboxProvider("a", declaredCapabilities: []);

        var placement = SandboxProviderCapabilityGate.ApplyProviderCapabilities(member, provider);

        Assert.Empty(placement.Capabilities);
    }

    [Fact]
    public void ApplyProviderCapabilities_KeepsWellKnownTagProviderDeclares()
    {
        var member = SandboxPlacementTestMembers.Member(
            "a", "a",
            capabilities: [SandboxCapabilities.SuspendResume, SandboxCapabilities.DiskGuard]);
        var provider = new PlacementFakeSandboxProvider(
            "a",
            declaredCapabilities: [SandboxCapabilities.SuspendResume, "suspend-resume-extra"]);

        var placement = SandboxProviderCapabilityGate.ApplyProviderCapabilities(member, provider);

        Assert.Equal([SandboxCapabilities.SuspendResume], placement.Capabilities.ToList());
    }

    [Fact]
    public void ApplyProviderCapabilities_MatchesCaseInsensitively()
    {
        var member = SandboxPlacementTestMembers.Member("a", "a", capabilities: ["SUSPEND-RESUME"]);
        var provider = new PlacementFakeSandboxProvider("a", declaredCapabilities: ["Suspend-Resume"]);

        var placement = SandboxProviderCapabilityGate.ApplyProviderCapabilities(member, provider);

        Assert.Equal(["SUSPEND-RESUME"], placement.Capabilities.ToList());
    }

    [Fact]
    public void ApplyProviderCapabilities_PassesThroughClearanceTags()
    {
        var member = SandboxPlacementTestMembers.Member("a", "a", capabilities: ["team-gamma", "gpu"]);
        var provider = new PlacementFakeSandboxProvider("a", declaredCapabilities: []);

        var placement = SandboxProviderCapabilityGate.ApplyProviderCapabilities(member, provider);

        Assert.Equal(["team-gamma", "gpu"], placement.Capabilities.ToList());
    }

    [Fact]
    public void ApplyProviderCapabilities_ProjectsOtherAttributesVerbatim()
    {
        var member = SandboxPlacementTestMembers.Member(
            "a", "a",
            capabilities: ["x"],
            networkProfiles: ["special"],
            credentials: ["codex"],
            preferenceScore: 42,
            capacity: 6);
        var provider = new PlacementFakeSandboxProvider("a");

        var placement = SandboxProviderCapabilityGate.ApplyProviderCapabilities(member, provider);

        Assert.Equal("a", placement.MemberId);
        Assert.Equal(6, placement.MaxConcurrentSandboxes);
        Assert.Equal(["special"], placement.NetworkProfiles.ToList());
        Assert.Equal(["codex"], placement.Credentials.ToList());
    }
}
