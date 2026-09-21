using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Pure placement-eligibility rules for executor hosts: zero capacity,
/// cordoned, and unhealthy executors register but are never selected.
/// </summary>
public sealed class ExecutorEligibilityTests
{
    private static SandboxPlacementMember Host(
        int? capacity = 4,
        bool cordoned = false,
        bool healthy = true,
        string[]? profiles = null,
        string[]? credentials = null,
        string[]? capabilities = null) => new()
        {
            MemberId = "exec-1",
            MaxConcurrentSandboxes = capacity,
            Cordoned = cordoned,
            Healthy = healthy,
            NetworkProfiles = profiles ?? [],
            Credentials = credentials ?? [],
            Capabilities = capabilities ?? [],
        };

    [Fact]
    public void ZeroCapacity_IsNeverSelected()
    {
        Assert.False(ExecutorEligibility.IsEligibleForPlacement(Host(capacity: 0), 0));
    }

    [Fact]
    public void Cordoned_IsNeverSelected()
    {
        Assert.False(ExecutorEligibility.IsEligibleForPlacement(Host(cordoned: true), 0));
    }

    [Fact]
    public void Unhealthy_IsNeverSelected()
    {
        Assert.False(ExecutorEligibility.IsEligibleForPlacement(Host(healthy: false), 0));
    }

    [Fact]
    public void HealthyWithFreeCapacity_IsSelected()
    {
        Assert.True(ExecutorEligibility.IsEligibleForPlacement(Host(capacity: 2), 1));
    }

    [Fact]
    public void AtCapacity_IsNotSelected()
    {
        Assert.False(ExecutorEligibility.IsEligibleForPlacement(Host(capacity: 2), 2));
    }

    [Fact]
    public void UncappedHealthy_IsAlwaysSelected()
    {
        Assert.True(ExecutorEligibility.IsEligibleForPlacement(Host(capacity: null), 10_000));
    }

    [Fact]
    public void NegativeLoad_IsClampedToZero()
    {
        Assert.True(ExecutorEligibility.IsEligibleForPlacement(Host(capacity: 1), -5));
    }

    [Fact]
    public void EmptyProfiles_AcceptsEveryProfile()
    {
        var host = Host(profiles: []);
        Assert.True(ExecutorEligibility.AcceptsNetworkProfile(host, "restricted"));
        Assert.True(ExecutorEligibility.AcceptsNetworkProfile(host, null));
    }

    [Fact]
    public void StarProfile_AcceptsEveryProfile()
    {
        var host = Host(profiles: ["*"]);
        Assert.True(ExecutorEligibility.AcceptsNetworkProfile(host, "restricted"));
    }

    [Fact]
    public void DeclaredProfile_MatchesExactly()
    {
        var host = Host(profiles: ["restricted"]);
        Assert.True(ExecutorEligibility.AcceptsNetworkProfile(host, "restricted"));
        Assert.False(ExecutorEligibility.AcceptsNetworkProfile(host, "restricted-extra"));
        Assert.False(ExecutorEligibility.AcceptsNetworkProfile(host, "restrict"));
    }

    [Fact]
    public void BlankProfile_MatchesDefaultMarker()
    {
        var host = Host(profiles: ["(default)"]);
        Assert.True(ExecutorEligibility.AcceptsNetworkProfile(host, null));
        Assert.True(ExecutorEligibility.AcceptsNetworkProfile(host, "  "));
        Assert.False(ExecutorEligibility.AcceptsNetworkProfile(host, "restricted"));
    }

    [Fact]
    public void Credentials_MatchByExactEqualityOnly()
    {
        var host = Host(credentials: ["codex"]);
        Assert.True(ExecutorEligibility.HoldsCredential(host, "codex"));
        Assert.True(ExecutorEligibility.HoldsCredential(host, "  codex  "));
        Assert.False(ExecutorEligibility.HoldsCredential(host, "codex-admin"));
        Assert.False(ExecutorEligibility.HoldsCredential(host, "cod"));
    }

    [Fact]
    public void Credentials_WildcardHoldsEverything()
    {
        var host = Host(credentials: ["*"]);
        Assert.True(ExecutorEligibility.HoldsCredential(host, "codex"));
        Assert.True(ExecutorEligibility.HoldsCredential(host, "copilot"));
        Assert.True(ExecutorEligibility.HoldsCredential(host, "anything-at-all"));
    }

    [Fact]
    public void Credentials_EmptyDeclarationHoldsNothing()
    {
        // Deliberately NOT read as "holds everything": a remote executor host
        // that declares no credentials must not be handed credential-bearing
        // work. Contrast AcceptsNetworkProfile, where empty means accept-all.
        var host = Host(credentials: []);
        Assert.False(ExecutorEligibility.HoldsCredential(host, "codex"));
        Assert.True(ExecutorEligibility.AcceptsNetworkProfile(host, "restricted"));
    }

    [Fact]
    public void WorkerId_IsStableAndPrefixed()
    {
        var registration = new ExecutorRegistration { HostId = "exec-1" };
        Assert.Equal("executor:exec-1", registration.WorkerId);
        Assert.Equal(registration.WorkerId, ExecutorRegistration.WorkerIdFor("  exec-1 "));
    }

    [Fact]
    public void Capabilities_EmptyRequired_CoveredByAnyHost()
    {
        Assert.True(ExecutorEligibility.CoversRequiredCapabilities(Host(), []));
        Assert.True(ExecutorEligibility.CoversRequiredCapabilities(Host(capabilities: []), []));
    }

    [Fact]
    public void Capabilities_CoveredOnlyByExactCaseInsensitiveMatch()
    {
        var host = Host(capabilities: ["Sensitive"]);
        Assert.True(ExecutorEligibility.CoversRequiredCapabilities(host, ["sensitive"]));
        Assert.True(ExecutorEligibility.CoversRequiredCapabilities(host, ["SENSITIVE"]));
        Assert.False(ExecutorEligibility.CoversRequiredCapabilities(host, ["sensitive-extra"]));
        Assert.False(ExecutorEligibility.CoversRequiredCapabilities(host, ["sens"]));
        Assert.False(ExecutorEligibility.CoversRequiredCapabilities(Host(capabilities: []), ["sensitive"]));
    }

    [Fact]
    public void Capabilities_AllRequiredTagsMustBeCovered()
    {
        var host = Host(capabilities: ["sensitive", "audit"]);
        Assert.True(ExecutorEligibility.CoversRequiredCapabilities(host, ["sensitive", "audit"]));
        Assert.False(ExecutorEligibility.CoversRequiredCapabilities(host, ["sensitive", "architectural"]));
    }

    [Fact]
    public void FindCapabilityNoHostProvides_NamesFirstUnmetTag()
    {
        var hosts = new[]
        {
            new SandboxPlacementMember { MemberId = "a", Capabilities = ["general"] },
            new SandboxPlacementMember { MemberId = "b", Capabilities = ["sensitive"] },
        };
        Assert.Null(ExecutorEligibility.FindCapabilityNoHostProvides(hosts, []));
        Assert.Null(ExecutorEligibility.FindCapabilityNoHostProvides(hosts, ["sensitive"]));
        Assert.Equal("architectural", ExecutorEligibility.FindCapabilityNoHostProvides(hosts, ["sensitive", "architectural"]));
        Assert.Equal("nope", ExecutorEligibility.FindCapabilityNoHostProvides(hosts, ["nope"]));
    }
}
