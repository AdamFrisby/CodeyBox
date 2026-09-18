using CodeyBox.Api;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// The member-keyed registry builds each provider kind once, shares the
/// instance across members naming it, and resolves independently of
/// registration order — the seam that lets core code stay provider-agnostic.
/// </summary>
public sealed class SandboxProviderRegistryTests
{
    [Fact]
    public void Resolve_SameKindAcrossMembers_ReturnsSharedInstanceBuiltOnce()
    {
        var builds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var registry = new SandboxProviderRegistry(kind =>
        {
            builds[kind] = builds.TryGetValue(kind, out var count) ? count + 1 : 1;
            return new PlacementFakeSandboxProvider(kind);
        });

        var first = registry.Resolve(Member("a", "incus"));
        var second = registry.Resolve(Member("b", "incus"));

        Assert.Same(first, second);
        Assert.Equal(1, builds["incus"]);
    }

    [Fact]
    public void Resolve_DifferentKinds_ReturnDistinctInstances()
    {
        var registry = new SandboxProviderRegistry(kind => new PlacementFakeSandboxProvider(kind));

        var incus = registry.Resolve(Member("a", "incus"));
        var multipass = registry.Resolve(Member("b", "multipass"));

        Assert.NotSame(incus, multipass);
        Assert.Equal("incus", incus.Name);
        Assert.Equal("multipass", multipass.Name);
    }

    [Fact]
    public void Resolve_OrderIndependent_ReturnsSameInstances()
    {
        var registry = new SandboxProviderRegistry(kind => new PlacementFakeSandboxProvider(kind));

        var aFirst = registry.Resolve(Member("a", "multipass"));
        var bFirst = registry.Resolve(Member("b", "incus"));
        var bSecond = registry.Resolve(Member("b", "incus"));
        var aSecond = registry.Resolve(Member("a", "multipass"));

        Assert.Same(aFirst, aSecond);
        Assert.Same(bFirst, bSecond);
    }

    [Fact]
    public void Resolve_NormalizesKindCase()
    {
        var builds = 0;
        var registry = new SandboxProviderRegistry(kind =>
        {
            builds++;
            return new PlacementFakeSandboxProvider(kind);
        });

        var lower = registry.Resolve(Member("a", "incus"));
        var upper = registry.Resolve(Member("b", "INCUS"));

        Assert.Same(lower, upper);
        Assert.Equal(1, builds);
    }

    [Fact]
    public void Resolve_NullMember_Throws()
    {
        var registry = new SandboxProviderRegistry(kind => new PlacementFakeSandboxProvider(kind));
        Assert.Throws<ArgumentNullException>(() => registry.Resolve(null!));
    }

    [Fact]
    public void Resolve_BlankKind_FailsClosed()
    {
        var registry = new SandboxProviderRegistry(kind => new PlacementFakeSandboxProvider(kind));
        var ex = Assert.Throws<InvalidOperationException>(() => registry.Resolve(Member("a", "  ")));
        Assert.Contains("a", ex.Message);
    }

    [Fact]
    public void Resolve_UnknownKind_PropagatesFactoryFailure()
    {
        var registry = new SandboxProviderRegistry(kind =>
            throw new InvalidOperationException($"Unregistered sandbox provider kind '{kind}'."));
        var ex = Assert.Throws<InvalidOperationException>(() => registry.Resolve(Member("a", "nope")));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void ListRegistered_EnumeratesKindsSortedRegardlessOfBuildOrder()
    {
        var registry = new SandboxProviderRegistry(kind => new PlacementFakeSandboxProvider(kind));
        registry.EnsureKind("multipass");
        registry.EnsureKind("incus");
        registry.EnsureKind("bubblewrap");

        var listed = registry.ListRegistered();

        Assert.Equal(["bubblewrap", "incus", "multipass"], listed.Select(static r => r.Kind).ToList());
        Assert.All(listed, static r => Assert.NotNull(r.Provider));
    }

    private static SandboxMember Member(string memberId, string providerKind) =>
        SandboxPlacementTestMembers.Member(memberId, providerKind);

    [Fact]
    public void SyncKindCapacities_DerivesKindGateAsMemberSum()
    {
        var registry = new SandboxProviderRegistry(kind =>
            CodeyBox.Orchestrator.SandboxAdmissionControlledProvider.Wrap(
                new PlacementFakeSandboxProvider(kind),
                2,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<
                    CodeyBox.Orchestrator.SandboxAdmissionControlledProvider>.Instance));
        registry.EnsureKind("r");
        var wrapper = Assert.IsAssignableFrom<
            CodeyBox.Orchestrator.SandboxAdmissionControlledProvider>(registry.EnsureKind("r"));
        Assert.Equal(2, wrapper.MaxConcurrentSandboxes);

        registry.SyncKindCapacities(SandboxPlacementTestMembers.Snapshot(
            SandboxPlacementTestMembers.Member("a", "r", capacity: 3),
            SandboxPlacementTestMembers.Member("b", "r", capacity: 5)).Current);

        Assert.Equal(8, wrapper.MaxConcurrentSandboxes);
    }

    [Fact]
    public void SyncKindCapacities_SumsPerKindAndLeavesUnnamedKindsAlone()
    {
        var registry = new SandboxProviderRegistry(kind =>
            CodeyBox.Orchestrator.SandboxAdmissionControlledProvider.Wrap(
                new PlacementFakeSandboxProvider(kind),
                2,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<
                    CodeyBox.Orchestrator.SandboxAdmissionControlledProvider>.Instance));
        var r = Assert.IsAssignableFrom<
            CodeyBox.Orchestrator.SandboxAdmissionControlledProvider>(registry.EnsureKind("r"));
        var other = Assert.IsAssignableFrom<
            CodeyBox.Orchestrator.SandboxAdmissionControlledProvider>(registry.EnsureKind("other"));

        registry.SyncKindCapacities(SandboxPlacementTestMembers.Snapshot(
            SandboxPlacementTestMembers.Member("a", "r", capacity: 3),
            SandboxPlacementTestMembers.Member("b", "R", capacity: 5)).Current);

        Assert.Equal(8, r.MaxConcurrentSandboxes);
        Assert.Equal(2, other.MaxConcurrentSandboxes);
    }

    [Fact]
    public void SyncKindCapacities_UnchangedTargetsAreSilentNoOps()
    {
        var logger = new CapturingLogger<CodeyBox.Orchestrator.SandboxAdmissionControlledProvider>();
        var registry = new SandboxProviderRegistry(
            kind => CodeyBox.Orchestrator.SandboxAdmissionControlledProvider.Wrap(
                new PlacementFakeSandboxProvider(kind), 8, logger),
            log: Microsoft.Extensions.Logging.Abstractions.NullLogger<SandboxProviderRegistry>.Instance);
        registry.EnsureKind("r");

        var catalog = SandboxPlacementTestMembers.Snapshot(
            SandboxPlacementTestMembers.Member("a", "r", capacity: 8)).Current;
        registry.SyncKindCapacities(catalog);
        registry.SyncKindCapacities(catalog);

        Assert.Empty(logger.Entries);
    }
}
