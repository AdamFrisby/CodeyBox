using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Pure placement-decider verification: credential, network profile,
/// capability, capacity, cordon/health and observability rules compose in
/// <see cref="ExecutorPlacement.Decide"/> without any I/O.
/// </summary>
public sealed class ExecutorPlacementTests
{
    private static SandboxPlacementMember Host(
        string id = "exec-1",
        int? capacity = 4,
        bool cordoned = false,
        bool healthy = true,
        string[]? profiles = null,
        string[]? credentials = null,
        string[]? capabilities = null) => new()
        {
            MemberId = id,
            MaxConcurrentSandboxes = capacity,
            Cordoned = cordoned,
            Healthy = healthy,
            NetworkProfiles = profiles ?? [],
            Credentials = credentials ?? [],
            Capabilities = capabilities ?? [],
        };

    private static ExecutorPlacementRequirements NoRequirements() => new()
    {
        RequiredCredential = null,
        RequiredNetworkProfile = null,
        RequiredCapabilities = [],
    };

    [Fact]
    public void CredentialHeldByOneHost_SelectsThatHost()
    {
        var hosts = new[]
        {
            Host("exec-1", credentials: ["claude"]),
            Host("exec-2", credentials: ["codex"]),
        };
        var decision = ExecutorPlacement.Decide(hosts, NoRequirements() with { RequiredCredential = "codex" });

        Assert.Equal("exec-2", decision.SelectedHostId);
        Assert.False(decision.IsUnplaceable);
        Assert.Contains(decision.Candidates, c => c.HostId == "exec-1" && c.Reason == "missing-credential:codex");
        Assert.Contains(decision.Candidates, c => c.HostId == "exec-2" && c.Reason == "selected");
    }

    [Fact]
    public void Credential_MatchesByExactEqualityOnly()
    {
        var hosts = new[] { Host(credentials: ["codex"]) };
        var decision = ExecutorPlacement.Decide(hosts, NoRequirements() with { RequiredCredential = "codex-admin" });

        Assert.Null(decision.SelectedHostId);
        Assert.Contains(decision.Candidates, c => c.Reason == "missing-credential:codex-admin");
    }

    [Fact]
    public void NetworkProfileAbsentFromHost_NeverSelected()
    {
        var hosts = new[]
        {
            Host("exec-1", profiles: ["open"]),
            Host("exec-2", profiles: ["open", "restricted"]),
        };
        var decision = ExecutorPlacement.Decide(hosts, NoRequirements() with { RequiredNetworkProfile = "restricted" });

        Assert.Equal("exec-2", decision.SelectedHostId);
        Assert.Contains(decision.Candidates, c => c.HostId == "exec-1" && c.Reason == "network-profile:restricted");
    }

    [Fact]
    public void HostAtCapacity_NotSelected()
    {
        var hosts = new[]
        {
            Host("exec-1", capacity: 1),
            Host("exec-2", capacity: 1),
        };
        var loads = new Dictionary<string, int>(StringComparer.Ordinal) { ["exec-1"] = 1 };

        var full = ExecutorPlacement.Decide(hosts, NoRequirements(), loads);
        Assert.Equal("exec-2", full.SelectedHostId);
        Assert.Contains(full.Candidates, c => c.HostId == "exec-1" && c.Reason == "at-capacity(1/1)");

        var freed = ExecutorPlacement.Decide(hosts, NoRequirements(), new Dictionary<string, int>(StringComparer.Ordinal));
        Assert.Equal("exec-1", freed.SelectedHostId);
    }

    [Fact]
    public void CordonedAndUnhealthyHosts_Excluded()
    {
        var hosts = new[]
        {
            Host("exec-1", cordoned: true),
            Host("exec-2", healthy: false),
            Host("exec-3"),
        };
        var decision = ExecutorPlacement.Decide(hosts, NoRequirements());

        Assert.Equal("exec-3", decision.SelectedHostId);
        Assert.Contains(decision.Candidates, c => c.HostId == "exec-1" && c.Reason == "cordoned");
        Assert.Contains(decision.Candidates, c => c.HostId == "exec-2" && c.Reason == "unhealthy");
    }

    [Fact]
    public void NoEligibleHost_TransientWhenCapabilityHeldSomewhere()
    {
        var hosts = new[]
        {
            Host("exec-1", cordoned: true, capabilities: ["sensitive"]),
        };
        var requirements = NoRequirements() with { RequiredCapabilities = (IReadOnlyList<string>)["sensitive"] };
        var decision = ExecutorPlacement.Decide(hosts, requirements);

        Assert.Null(decision.SelectedHostId);
        Assert.False(decision.IsUnplaceable);
        Assert.Null(decision.UnmetCapability);
    }

    [Fact]
    public void CapabilityNoHostProvides_IsUnplaceableNamingTag()
    {
        var hosts = new[]
        {
            Host("exec-1", capabilities: ["general"]),
            Host("exec-2", capabilities: ["general"]),
        };
        var requirements = NoRequirements() with { RequiredCapabilities = (IReadOnlyList<string>)["sensitive"] };
        var decision = ExecutorPlacement.Decide(hosts, requirements);

        Assert.Null(decision.SelectedHostId);
        Assert.True(decision.IsUnplaceable);
        Assert.Equal("sensitive", decision.UnmetCapability);
        Assert.All(decision.Candidates, c => Assert.Equal($"missing-capability:sensitive", c.Reason));
    }

    [Fact]
    public void Capabilities_MatchCaseInsensitively_InExistingVocabulary()
    {
        var hosts = new[] { Host(capabilities: ["Sensitive"]) };
        var requirements = NoRequirements() with { RequiredCapabilities = (IReadOnlyList<string>)["sensitive"] };
        var decision = ExecutorPlacement.Decide(hosts, requirements);

        Assert.Equal("exec-1", decision.SelectedHostId);
    }

    [Fact]
    public void LeastLoadedHost_Wins_TiesBreakByHostId()
    {
        var hosts = new[]
        {
            Host("exec-b", capacity: 4),
            Host("exec-a", capacity: 4),
        };
        var loads = new Dictionary<string, int>(StringComparer.Ordinal) { ["exec-a"] = 1, ["exec-b"] = 3 };

        var byLoad = ExecutorPlacement.Decide(hosts, NoRequirements(), loads);
        Assert.Equal("exec-a", byLoad.SelectedHostId);

        var tie = ExecutorPlacement.Decide(hosts, NoRequirements());
        Assert.Equal("exec-a", tie.SelectedHostId);
    }

    [Fact]
    public void RuntimeUnhealthyHosts_Skipped()
    {
        var hosts = new[] { Host("exec-1"), Host("exec-2") };
        var backedOff = new HashSet<string>(StringComparer.Ordinal) { "exec-1" };
        var decision = ExecutorPlacement.Decide(hosts, NoRequirements(), runtimeUnhealthy: backedOff);

        Assert.Equal("exec-2", decision.SelectedHostId);
        Assert.Contains(decision.Candidates, c => c.HostId == "exec-1" && c.Reason == "runtime-unhealthy");
    }

    [Fact]
    public void Decision_Describe_NamesSelectionAndRefusals()
    {
        var hosts = new[]
        {
            Host("exec-1", cordoned: true),
            Host("exec-2"),
        };
        var decision = ExecutorPlacement.Decide(hosts, NoRequirements());

        Assert.Contains("selected=exec-2", decision.Describe());
        Assert.Contains("exec-1=cordoned", decision.Describe());
    }

    [Fact]
    public void EmptyHosts_SelectsNothing_WithoutUnplaceable()
    {
        var decision = ExecutorPlacement.Decide([], NoRequirements());

        Assert.Null(decision.SelectedHostId);
        Assert.False(decision.IsUnplaceable);
        Assert.Empty(decision.Candidates);
    }
}
