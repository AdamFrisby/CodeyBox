using System.Reflection;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox.Bubblewrap;
using CodeyBox.Sandbox.Incus;
using CodeyBox.Sandbox.Multipass;
using CodeyBox.Sandbox.MultipassRemote;
using CodeyBox.Sandbox.Process;
using CodeyBox.Sandbox.Sprites;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies sandbox provider capability declarations:
/// 1. Each shipped provider explicitly declares its real capabilities.
/// 2. Capability tags use the single source of truth in <see cref="SandboxCapabilities"/>.
/// 3. Providers without explicit declarations visibly default to empty.
/// 4. Placement and admission control wrappers preserve declared capabilities.
/// </summary>
public sealed class SandboxProviderCapabilitiesTests
{
    private static readonly Type[] ShippedProviderTypes =
    [
        typeof(MultipassSandboxProvider),
        typeof(IncusSandboxProvider),
        typeof(MultipassRemoteSandboxProvider),
        typeof(BubblewrapSandboxProvider),
        typeof(ProcessSandboxProvider),
        typeof(SpritesSandboxProvider),
    ];

    [Theory]
    [InlineData(typeof(MultipassSandboxProvider))]
    [InlineData(typeof(IncusSandboxProvider))]
    [InlineData(typeof(MultipassRemoteSandboxProvider))]
    [InlineData(typeof(BubblewrapSandboxProvider))]
    [InlineData(typeof(ProcessSandboxProvider))]
    [InlineData(typeof(SpritesSandboxProvider))]
    public void ShippedProvider_ExplicitlyDeclaresCapabilitiesProperty(Type providerType)
    {
        // Must be declared on the concrete class itself so an omitted declaration is immediately detectable
        // via reflection rather than silently falling back to the default interface implementation.
        var property = providerType.GetProperty(
            nameof(ISandboxProvider.DeclaredCapabilities),
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.NotNull(property);
        Assert.True(property.CanRead);
    }

    [Fact]
    public void DefaultInterfaceImplementation_ReturnsEmpty_AndUnoverriddenClassIsVisible()
    {
        var undeclaredProvider = new ProviderWithoutCapabilities();
        ISandboxProvider asInterface = undeclaredProvider;

        // Default interface method yields empty list without throwing or null
        Assert.NotNull(asInterface.DeclaredCapabilities);
        Assert.Empty(asInterface.DeclaredCapabilities);

        // Reflection confirms the concrete type did NOT declare it
        var property = typeof(ProviderWithoutCapabilities).GetProperty(
            nameof(ISandboxProvider.DeclaredCapabilities),
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.Null(property);
    }

    [Fact]
    public void MultipassSandboxProvider_DeclaresFullCapabilitySet()
    {
        var provider = new MultipassSandboxProvider(
            new MultipassSandboxOptions(),
            NullLogger<MultipassSandboxProvider>.Instance);

        Assert.Equal(
            [
                SandboxCapabilities.BaselineBake,
                SandboxCapabilities.CacheSeeding,
                SandboxCapabilities.DiskGuard,
                SandboxCapabilities.PortPublishing,
                SandboxCapabilities.SuspendResume,
                SandboxCapabilities.Teardown,
            ],
            provider.DeclaredCapabilities);
    }

    [Fact]
    public void IncusSandboxProvider_DeclaresSupportedCapabilities()
    {
        var provider = new IncusSandboxProvider(
            new IncusSandboxOptions(),
            NullLogger<IncusSandboxProvider>.Instance);

        Assert.Equal(
            [
                SandboxCapabilities.BaselineBake,
                SandboxCapabilities.CacheSeeding,
                SandboxCapabilities.DiskGuard,
                SandboxCapabilities.Teardown,
            ],
            provider.DeclaredCapabilities);

        // Incus does not support suspend/resume or port publishing
        Assert.DoesNotContain(SandboxCapabilities.SuspendResume, provider.DeclaredCapabilities);
        Assert.DoesNotContain(SandboxCapabilities.PortPublishing, provider.DeclaredCapabilities);
    }

    [Fact]
    public void MultipassRemoteSandboxProvider_DeclaresEmptyCapabilities()
    {
        var provider = new MultipassRemoteSandboxProvider(
            new MultipassRemoteSandboxOptions(),
            new MultipassRemoteSandboxProviderTests.FakeRemoteHostTransport(),
            NullLogger<MultipassRemoteSandboxProvider>.Instance);

        Assert.Empty(provider.DeclaredCapabilities);
    }

    [Fact]
    public void BubblewrapSandboxProvider_DeclaresEmptyCapabilities()
    {
        var provider = new BubblewrapSandboxProvider(
            new BubblewrapSandboxOptions(),
            NullLogger<BubblewrapSandboxProvider>.Instance);

        Assert.Empty(provider.DeclaredCapabilities);
    }

    [Fact]
    public void ProcessSandboxProvider_DeclaresEmptyCapabilities()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);

        Assert.Empty(provider.DeclaredCapabilities);
    }

    [Fact]
    public void SpritesSandboxProvider_DeclaresEmptyCapabilities()
    {
        var provider = new SpritesSandboxProvider(
            () => new SpritesSandboxOptions(),
            NullLogger<SpritesSandboxProvider>.Instance);

        Assert.Empty(provider.DeclaredCapabilities);
    }

    [Fact]
    public void CapabilityConstants_AreUnique_AndMatchExpectedValues()
    {
        Assert.Equal("baseline-bake", SandboxCapabilities.BaselineBake);
        Assert.Equal("cache-seeding", SandboxCapabilities.CacheSeeding);
        Assert.Equal("disk-guard", SandboxCapabilities.DiskGuard);
        Assert.Equal("port-publishing", SandboxCapabilities.PortPublishing);
        Assert.Equal("suspend-resume", SandboxCapabilities.SuspendResume);
        Assert.Equal("teardown", SandboxCapabilities.Teardown);

        Assert.Equal(6, SandboxCapabilities.All.Count);
        Assert.Equal(SandboxCapabilities.All.Count, SandboxCapabilities.All.Distinct(StringComparer.Ordinal).Count());

        // Verify alias classes match
        Assert.Equal(SandboxCapabilities.BaselineBake, SandboxCapabilityTags.BaselineBake);
        Assert.Equal(SandboxCapabilities.CacheSeeding, SandboxCapabilityTags.CacheSeeding);
        Assert.Equal(SandboxCapabilities.DiskGuard, SandboxCapabilityTags.DiskGuard);
        Assert.Equal(SandboxCapabilities.PortPublishing, SandboxCapabilityTags.PortPublishing);
        Assert.Equal(SandboxCapabilities.SuspendResume, SandboxCapabilityTags.SuspendResume);
        Assert.Equal(SandboxCapabilities.Teardown, SandboxCapabilityTags.Teardown);

        Assert.Equal(SandboxCapabilities.BaselineBake, WellKnownSandboxCapabilities.BaselineBake);
        Assert.Equal(SandboxCapabilities.CacheSeeding, WellKnownSandboxCapabilities.CacheSeeding);
        Assert.Equal(SandboxCapabilities.DiskGuard, WellKnownSandboxCapabilities.DiskGuard);
        Assert.Equal(SandboxCapabilities.PortPublishing, WellKnownSandboxCapabilities.PortPublishing);
        Assert.Equal(SandboxCapabilities.SuspendResume, WellKnownSandboxCapabilities.SuspendResume);
        Assert.Equal(SandboxCapabilities.Teardown, WellKnownSandboxCapabilities.Teardown);
    }

    [Fact]
    public void ShippedProviders_AllDeclaredCapabilities_AreFromSandboxCapabilitiesAll()
    {
        var knownSet = new HashSet<string>(SandboxCapabilities.All, StringComparer.Ordinal);

        foreach (var type in ShippedProviderTypes)
        {
            var instance = CreateProviderInstance(type);
            foreach (var cap in instance.DeclaredCapabilities)
            {
                Assert.True(
                    knownSet.Contains(cap),
                    $"Provider {type.Name} declared capability '{cap}' which is not in SandboxCapabilities.All");
            }
        }
    }

    [Fact]
    public void ExecutorEligibility_MatchesProviderCapabilities()
    {
        var multipassCaps = new MultipassSandboxProvider(
            new MultipassSandboxOptions(),
            NullLogger<MultipassSandboxProvider>.Instance).DeclaredCapabilities;

        var incusCaps = new IncusSandboxProvider(
            new IncusSandboxOptions(),
            NullLogger<IncusSandboxProvider>.Instance).DeclaredCapabilities;

        var multipassMember = new SandboxPlacementMember
        {
            MemberId = "host-multipass",
            Capabilities = multipassCaps,
        };

        var incusMember = new SandboxPlacementMember
        {
            MemberId = "host-incus",
            Capabilities = incusCaps,
        };

        // Multipass satisfies suspend-resume and port-publishing
        Assert.True(ExecutorEligibility.CoversRequiredCapabilities(
            multipassMember,
            [SandboxCapabilities.SuspendResume, SandboxCapabilities.PortPublishing]));

        // Incus fails when suspend-resume or port-publishing is required
        Assert.False(ExecutorEligibility.CoversRequiredCapabilities(
            incusMember,
            [SandboxCapabilities.SuspendResume]));

        Assert.False(ExecutorEligibility.CoversRequiredCapabilities(
            incusMember,
            [SandboxCapabilities.PortPublishing]));

        // Both satisfy baseline-bake and disk-guard
        Assert.True(ExecutorEligibility.CoversRequiredCapabilities(
            multipassMember,
            [SandboxCapabilities.BaselineBake, SandboxCapabilities.DiskGuard]));

        Assert.True(ExecutorEligibility.CoversRequiredCapabilities(
            incusMember,
            [SandboxCapabilities.BaselineBake, SandboxCapabilities.DiskGuard]));

        // Matching is case-insensitive per ExecutorEligibility
        Assert.True(ExecutorEligibility.CoversRequiredCapabilities(
            multipassMember,
            ["BASELINE-BAKE", "PORT-PUBLISHING"]));
    }

    [Fact]
    public void FormatMatrix_FormatsExpectedOutput()
    {
        var multipass = new StubProvider("multipass", [SandboxCapabilities.BaselineBake, SandboxCapabilities.DiskGuard]);
        var bubblewrap = new StubProvider("bubblewrap", []);

        var matrix = SandboxCapabilities.FormatMatrix([multipass, bubblewrap]);

        Assert.Equal("multipass: [baseline-bake, disk-guard]; bubblewrap: [none]", matrix);
    }

    [Fact]
    public void FormatMatrix_EmptyList_ReturnsEmptyString()
    {
        var matrix = SandboxCapabilities.FormatMatrix([]);
        Assert.Equal(string.Empty, matrix);
    }

    [Fact]
    public void FormatMatrix_NullThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => SandboxCapabilities.FormatMatrix(null!));
    }

    [Fact]
    public void SandboxAdmissionControlledProvider_ForwardsDeclaredCapabilities()
    {
        var inner = new StubProvider("stub", [SandboxCapabilities.BaselineBake, SandboxCapabilities.Teardown]);
        var wrapped = SandboxAdmissionControlledProvider.Wrap(
            inner,
            maxConcurrentSandboxes: 5,
            NullLogger<SandboxAdmissionControlledProvider>.Instance);

        Assert.Equal([SandboxCapabilities.BaselineBake, SandboxCapabilities.Teardown], wrapped.DeclaredCapabilities);
    }

    [Fact]
    public void ReloadableSandboxProvider_ForwardsSelectedProviderCapabilities()
    {
        var providerA = new StubReloadableProvider("provider-a", [SandboxCapabilities.SuspendResume]);
        var providerB = new StubReloadableProvider("provider-b", [SandboxCapabilities.BaselineBake, SandboxCapabilities.CacheSeeding]);

        var current = "provider-a";
        var reloadable = new ReloadableSandboxProvider(
            () => current,
            () => [],
            [
                new ReloadableSandboxProvider.ProviderRegistration(
                    "provider-a",
                    () => providerA,
                    _ => false),
                new ReloadableSandboxProvider.ProviderRegistration(
                    "provider-b",
                    () => providerB,
                    _ => false),
            ],
            NullLogger<ReloadableSandboxProvider>.Instance);

        Assert.Equal([SandboxCapabilities.SuspendResume], reloadable.DeclaredCapabilities);

        current = "provider-b";
        Assert.Equal([SandboxCapabilities.BaselineBake, SandboxCapabilities.CacheSeeding], reloadable.DeclaredCapabilities);

        // RegisteredProviders exposes all registered providers
        Assert.Equal(2, reloadable.RegisteredProviders.Count);
        Assert.Contains(reloadable.RegisteredProviders, p => p.Name == "provider-a");
        Assert.Contains(reloadable.RegisteredProviders, p => p.Name == "provider-b");
    }

    private static ISandboxProvider CreateProviderInstance(Type type)
    {
        if (type == typeof(MultipassSandboxProvider))
            return new MultipassSandboxProvider(new MultipassSandboxOptions(), NullLogger<MultipassSandboxProvider>.Instance);
        if (type == typeof(IncusSandboxProvider))
            return new IncusSandboxProvider(new IncusSandboxOptions(), NullLogger<IncusSandboxProvider>.Instance);
        if (type == typeof(MultipassRemoteSandboxProvider))
            return new MultipassRemoteSandboxProvider(new MultipassRemoteSandboxOptions(), new MultipassRemoteSandboxProviderTests.FakeRemoteHostTransport(), NullLogger<MultipassRemoteSandboxProvider>.Instance);
        if (type == typeof(BubblewrapSandboxProvider))
            return new BubblewrapSandboxProvider(new BubblewrapSandboxOptions(), NullLogger<BubblewrapSandboxProvider>.Instance);
        if (type == typeof(ProcessSandboxProvider))
            return new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        if (type == typeof(SpritesSandboxProvider))
            return new SpritesSandboxProvider(() => new SpritesSandboxOptions(), NullLogger<SpritesSandboxProvider>.Instance);

        throw new ArgumentException($"Unknown provider type {type.Name}");
    }

    private sealed class ProviderWithoutCapabilities : ISandboxProvider
    {
        public string Name => "undeclared";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubProvider(string name, IReadOnlyList<string> capabilities) : ISandboxProvider
    {
        public string Name => name;
        public IReadOnlyList<string> DeclaredCapabilities => capabilities;
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubReloadableProvider(string name, IReadOnlyList<string> capabilities)
        : ISandboxProvider,
          IActiveSandboxProvider,
          IActiveSandboxProgressProvider,
          IDiskGuardedSandboxProvider,
          IBaselineImageResolver,
          IBaselineImageProvisioner,
          IResourceMetricsCapturingProvider
    {
        public string Name => name;
        public IReadOnlyList<string> DeclaredCapabilities => capabilities;
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct = default) =>
            Task.CompletedTask;

        public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() => [];
        public IReadOnlyList<ActiveSandboxProgress> SnapshotActiveSandboxProgress() => [];
        public IReadOnlyList<DiskGuardSample> SampleDiskGuardState() => [];

        public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor) => null;
        public Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BaselineImageInfo>>([]);
        public Task DisposeBaselineImageAsync(string baselineName, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> EnsureBaselineImageAsync(string profileName, SandboxProfileFlavor flavor, string? pinnedBaselineRef, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public bool CapturesResourceMetrics => false;
    }
}
