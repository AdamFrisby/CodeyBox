using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Pure placement-eligibility rules for executor hosts: zero capacity,
/// cordoned, and unhealthy executors register but are never selected.
/// </summary>
public sealed class ExecutorEligibilityTests
{
    private static ExecutorRegistration Host(
        int? capacity = 4,
        bool cordoned = false,
        bool healthy = true,
        string[]? profiles = null,
        string[]? credentials = null) => new()
        {
            HostId = "exec-1",
            MaxConcurrentSandboxes = capacity,
            Cordoned = cordoned,
            Healthy = healthy,
            AllowedNetworkProfiles = profiles ?? [],
            DeclaredCredentials = credentials ?? [],
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
    public void WorkerId_IsStableAndPrefixed()
    {
        var host = Host();
        Assert.Equal("executor:exec-1", host.WorkerId);
        Assert.Equal(host.WorkerId, ExecutorRegistration.WorkerIdFor("  exec-1 "));
    }
}
