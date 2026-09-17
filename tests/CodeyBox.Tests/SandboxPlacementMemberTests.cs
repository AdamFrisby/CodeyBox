using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Adapter and contract verification for <see cref="SandboxPlacementMember"/>:
/// the <see cref="ExecutorRegistration"/> projection preserves every
/// attribute, zero-capacity members register but are never selected, and a
/// capability no member declares is unplaceable rather than transient.
/// </summary>
public sealed class SandboxPlacementMemberTests
{
    private static ExecutorPlacementRequirements NoRequirements() => new()
    {
        RequiredCredential = null,
        RequiredNetworkProfile = null,
        RequiredCapabilities = [],
    };

    [Fact]
    public void FromExecutorRegistration_PreservesEachAttribute()
    {
        var registration = new ExecutorRegistration
        {
            HostId = "exec-7",
            MaxConcurrentSandboxes = 3,
            AllowedNetworkProfiles = ["open", "restricted"],
            DeclaredCredentials = ["codex"],
            DeclaredCapabilities = ["sensitive"],
            Cordoned = true,
            Healthy = false,
        };

        var member = SandboxPlacementMember.FromExecutorRegistration(registration);

        Assert.Equal("exec-7", member.MemberId);
        Assert.Equal(3, member.MaxConcurrentSandboxes);
        Assert.Equal(["open", "restricted"], member.NetworkProfiles);
        Assert.Equal(["codex"], member.Credentials);
        Assert.Equal(["sensitive"], member.Capabilities);
        Assert.True(member.Cordoned);
        Assert.False(member.Healthy);
    }

    [Fact]
    public void FromExecutorRegistration_NullCapacityMeansUncapped_ZeroMeansNeverSelected()
    {
        var uncapped = SandboxPlacementMember.FromExecutorRegistration(
            new ExecutorRegistration { HostId = "uncapped", MaxConcurrentSandboxes = null });
        var zero = SandboxPlacementMember.FromExecutorRegistration(
            new ExecutorRegistration { HostId = "zero", MaxConcurrentSandboxes = 0 });

        Assert.Null(uncapped.MaxConcurrentSandboxes);
        Assert.Equal(0, zero.MaxConcurrentSandboxes);

        Assert.True(ExecutorEligibility.IsEligibleForPlacement(uncapped, 10_000));
        Assert.False(ExecutorEligibility.IsEligibleForPlacement(zero, 0));
    }

    [Fact]
    public void WorkerRegistrationNullLists_ProjectToEmptyPreservingSemantics()
    {
        var row = new WorkerRegistration
        {
            WorkerId = "executor:exec-1",
            HostName = "exec-1",
            ProcessId = 123,
            StartedAt = DateTimeOffset.UtcNow,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            ExecutorHostId = "exec-1",
            MaxConcurrentSandboxes = null,
            ExecutorNetworkProfiles = null,
            ExecutorCredentials = null,
            ExecutorCapabilities = null,
            Healthy = true,
        };

        var member = SandboxPlacementMember.FromExecutorRegistration(new ExecutorRegistration
        {
            HostId = row.ExecutorHostId!,
            MaxConcurrentSandboxes = row.MaxConcurrentSandboxes,
            AllowedNetworkProfiles = row.ExecutorNetworkProfiles ?? [],
            DeclaredCredentials = row.ExecutorCredentials ?? [],
            DeclaredCapabilities = row.ExecutorCapabilities ?? [],
            Cordoned = row.Cordoned,
            Healthy = row.Healthy,
        });

        Assert.Empty(member.NetworkProfiles);
        Assert.Empty(member.Credentials);
        Assert.Empty(member.Capabilities);
        Assert.True(ExecutorEligibility.AcceptsNetworkProfile(member, "restricted"));
        Assert.False(ExecutorEligibility.HoldsCredential(member, "codex"));
        Assert.False(ExecutorEligibility.CoversRequiredCapabilities(member, ["sensitive"]));
    }

    [Fact]
    public void ZeroCapacityMember_ReportedAtCapacityAndNeverSelected()
    {
        var hosts = new SandboxPlacementMember[]
        {
            new() { MemberId = "exec-zero", MaxConcurrentSandboxes = 0 },
            new() { MemberId = "exec-ok", MaxConcurrentSandboxes = 4 },
        };

        var decision = ExecutorPlacement.Decide(hosts, NoRequirements());

        Assert.Equal("exec-ok", decision.SelectedHostId);
        Assert.False(decision.IsUnplaceable);
        Assert.Contains(
            decision.Candidates,
            c => c.HostId == "exec-zero" && c.Reason == "at-capacity(0/0)");
    }

    [Fact]
    public void ZeroCapacityAlone_IsTransientRatherThanUnplaceable()
    {
        var hosts = new SandboxPlacementMember[]
        {
            new() { MemberId = "exec-zero", MaxConcurrentSandboxes = 0 },
        };

        var decision = ExecutorPlacement.Decide(hosts, NoRequirements());

        Assert.Null(decision.SelectedHostId);
        Assert.False(decision.IsUnplaceable);
        Assert.Null(decision.UnmetCapability);
    }

    [Fact]
    public void UnmetCapability_IsUnplaceable_WhileHeldElsewhereStaysTransient()
    {
        var unplaceableHosts = new SandboxPlacementMember[]
        {
            new() { MemberId = "exec-1", Capabilities = ["general"] },
            new() { MemberId = "exec-2", Capabilities = ["general"] },
        };
        var requirements = NoRequirements() with
        {
            RequiredCapabilities = (IReadOnlyList<string>)["sensitive"],
        };

        var unplaceable = ExecutorPlacement.Decide(unplaceableHosts, requirements);

        Assert.Null(unplaceable.SelectedHostId);
        Assert.True(unplaceable.IsUnplaceable);
        Assert.Equal("sensitive", unplaceable.UnmetCapability);

        var transientHosts = new SandboxPlacementMember[]
        {
            new() { MemberId = "exec-1", Cordoned = true, Capabilities = ["sensitive"] },
        };
        var transient = ExecutorPlacement.Decide(transientHosts, requirements);

        Assert.Null(transient.SelectedHostId);
        Assert.False(transient.IsUnplaceable);
        Assert.Null(transient.UnmetCapability);
    }
}
