using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Plugin-contributed sandbox provider kinds: a plugin's <see cref="ISandboxProvider"/>
/// is constructible and selectable by placement under its normalised
/// <see cref="ISandboxProvider.Name"/>, shares one instance across members naming it
/// regardless of resolution order, is always classified
/// <see cref="EgressEnforcementLocation.NotEnforced"/> (a plugin can never declare
/// itself enforced), is refused wherever enforced egress is required, still passes
/// through the host platform-support and workload-trust gates, and never weakens the
/// unknown-kind refusal — which names plugin kinds alongside built-ins.
/// </summary>
public sealed class PluginSandboxProviderTests
{
    private const string PluginKind = "acme-vm";
    private const string PluginId = "acme.plugin";

    private static readonly HostPlatformSupport.HostOperatingSystem Linux = new(true, false, false);

    private sealed class PluginStubSandboxProvider : ISandboxProvider
    {
        public PluginStubSandboxProvider(
            string name,
            SandboxIsolationLevel isolation = SandboxIsolationLevel.None,
            IReadOnlyList<string>? declaredCapabilities = null)
        {
            Name = name;
            IsolationLevel = isolation;
            DeclaredCapabilities = declaredCapabilities ?? [];
        }

        public string Name { get; }
        public SandboxIsolationLevel IsolationLevel { get; }
        public IReadOnlyList<string> DeclaredCapabilities { get; }
        public int CreateCount { get; private set; }

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(spec);
            CreateCount++;
            return Task.FromResult<ISandbox>(new PlacementFakeSandbox(Name + "-sandbox"));
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ProductionHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "CodeyBox.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(Path.GetTempPath());
    }

    private static PluginSandboxProviderCatalog CatalogFor(ISandboxProvider provider, string pluginId = PluginId) =>
        new([(pluginId, provider)]);

    private static (SandboxProviderRegistry Registry, Dictionary<string, int> Builds) RegistryFor(PluginSandboxProviderCatalog catalog)
    {
        var builds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var registry = new SandboxProviderRegistry(
            kind =>
            {
                if (catalog.TryGetProvider(kind, out var pluginProvider))
                {
                    builds[kind] = builds.TryGetValue(kind, out var count) ? count + 1 : 1;
                    return pluginProvider;
                }
                throw new InvalidOperationException(
                    $"Unregistered sandbox provider kind '{kind}'. Registered providers: " +
                    $"{HostPlatformSupport.FormatKnownProviders(catalog.Kinds)}. " +
                    "The kind is NOT silently falling back to another provider.");
            },
            pluginKinds: catalog.Kinds);
        return (registry, builds);
    }

    [Fact]
    public void PluginKind_ResolvesSharedInstance_AndIsOrderIndependent()
    {
        var plugin = new PluginStubSandboxProvider(PluginKind);
        var catalog = CatalogFor(plugin);
        var (registry, builds) = RegistryFor(catalog);

        var first = registry.Resolve(SandboxPlacementTestMembers.Member("a", PluginKind));
        var second = registry.Resolve(SandboxPlacementTestMembers.Member("b", "ACME-VM "));

        Assert.Same(plugin, first);
        Assert.Same(first, second);
        Assert.Equal(PluginKind, Assert.Single(builds.Keys));
        Assert.Equal(1, builds[PluginKind]);

        // A second registry resolving in the opposite order shares the same instance.
        var (other, _) = RegistryFor(catalog);
        var bFirst = other.Resolve(SandboxPlacementTestMembers.Member("b", PluginKind));
        var aSecond = other.Resolve(SandboxPlacementTestMembers.Member("a", PluginKind));
        Assert.Same(plugin, bFirst);
        Assert.Same(bFirst, aSecond);
    }

    [Fact]
    public void PluginKind_KnownKinds_UnionBuiltInsAndPlugin()
    {
        var catalog = CatalogFor(new PluginStubSandboxProvider(PluginKind));
        var (registry, _) = RegistryFor(catalog);

        Assert.Contains("incus", registry.KnownKinds, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(PluginKind, registry.KnownKinds, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("process", registry.KnownKinds, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PluginKind_PlacesUnprofiledWork_OnItsProvider()
    {
        var plugin = new PluginStubSandboxProvider(PluginKind);
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("plugin-member", PluginKind)),
            new PlacementFakeSandboxProviderRegistry([plugin]));

        var sandbox = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None);

        var placed = sandbox;
        Assert.Equal("acme-vm-sandbox", placed.Id);
        Assert.Equal(1, plugin.CreateCount);
        Assert.NotNull(SandboxCapability.Find<PlacementFakeSandbox>(placed));
        await sandbox.DisposeAsync();
    }

    [Fact]
    public void PluginKind_ClassifiedNotEnforced_RegardlessOfPluginClaims()
    {
        // Even a plugin advertising dedicated-kernel isolation and capabilities
        // cannot reach an enforced egress classification: the host switch is the
        // only authority and it names exactly the reviewed in-tree kinds.
        var hardened = new PluginStubSandboxProvider(
            PluginKind,
            SandboxIsolationLevel.DedicatedKernel,
            ["baseline-bake", "suspend-resume", "teardown", "disk-guard"]);
        var catalog = CatalogFor(hardened);
        Assert.True(catalog.IsPluginKind(PluginKind));

        Assert.Equal(EgressEnforcementLocation.NotEnforced, HostPlatformSupport.GetEgressEnforcement(PluginKind));
        Assert.False(HostPlatformSupport.ClaimsNetworkIsolation(PluginKind, Linux));
        Assert.False(SandboxEgressPolicy.IsEnforced(PluginKind));
        Assert.Contains("NOT enforced", SandboxEgressPolicy.DescribeEgressEnforcement(PluginKind), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PluginKind_ProfiledWork_RefusedNamingKind()
    {
        var plugin = new PluginStubSandboxProvider(PluginKind);
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("plugin-member", PluginKind)),
            new PlacementFakeSandboxProviderRegistry([plugin]));

        var ex = await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(() =>
            acquirer.AcquireAsync(
                new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, "llm", SandboxPlacementTestMembers.Spec()),
                CancellationToken.None));

        Assert.Equal("enforced-egress", ex.UnmetCapability);
        Assert.Contains(PluginKind, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("llm", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PluginKind_ProfiledWork_PrefersEnforcedMember()
    {
        var plugin = new PluginStubSandboxProvider(PluginKind);
        var enforced = new PlacementFakeSandboxProvider("incus");
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("plugin-member", PluginKind, preferenceScore: 100),
                SandboxPlacementTestMembers.Member("enforced-member", "incus", preferenceScore: 10)),
            new PlacementFakeSandboxProviderRegistry([plugin, enforced]));

        await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, "llm", SandboxPlacementTestMembers.Spec()),
            CancellationToken.None);

        Assert.Equal(1, enforced.CreateCount);
    }

    [Fact]
    public async Task EnforcedKind_ProfiledWork_PlacesNormally()
    {
        var enforced = new PlacementFakeSandboxProvider("incus");
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("enforced-member", "incus")),
            new PlacementFakeSandboxProviderRegistry([enforced]));

        // A profiled acquisition on an enforced provider must not raise the
        // egress refusal: it places on the member's provider.
        var sandbox = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, "llm", SandboxPlacementTestMembers.Spec()),
            CancellationToken.None);

        Assert.NotNull(sandbox);
        Assert.Equal(1, enforced.CreateCount);
        await sandbox.DisposeAsync();
    }

    [Fact]
    public void PlatformGate_RunsForPluginKinds_AndIsHostOwned()
    {
        var pluginKinds = new HashSet<string>([PluginKind], StringComparer.OrdinalIgnoreCase);

        // With host registration the gate passes on every OS (there is no
        // OS-gated host enforcement to check for a NotEnforced backend).
        Assert.True(HostPlatformSupport.IsProviderSupportedOnHost(PluginKind, Linux, pluginKinds));
        Assert.True(HostPlatformSupport.IsProviderSupportedOnHost(
            PluginKind, new HostPlatformSupport.HostOperatingSystem(false, false, true), pluginKinds));
        Assert.Equal(string.Empty, HostPlatformSupport.GetUnsupportedReason("ACME-VM ", Linux, pluginKinds));

        // Without host registration the same kind is unsupported: the plugin
        // cannot assert its own support.
        Assert.False(HostPlatformSupport.IsProviderSupportedOnHost(PluginKind, Linux));
        Assert.False(HostPlatformSupport.IsProviderSupportedOnHost(PluginKind, Linux, pluginKinds: null));
    }

    [Fact]
    public void WorkloadTrustGate_RunsForPluginProviders()
    {
        var plugin = new PluginStubSandboxProvider(PluginKind, SandboxIsolationLevel.None);
        var options = new CodeyBoxOptions { WorkloadTrust = "Untrusted" };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var ex = Assert.Throws<RequiredConfigurationException>(() =>
            SandboxProviderSelection.ValidateWorkloadTrust(plugin, options, configuration, new ProductionHostEnvironment()));

        Assert.Contains(PluginKind, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkloadTrustGate_PluginDedicatedKernel_PassesTrust_ButEgressStaysNotEnforced()
    {
        // Capability declaration (isolation level) is the plugin's to make and flows
        // through the unchanged trust gate; egress classification is the host's and
        // does not follow it.
        var plugin = new PluginStubSandboxProvider(PluginKind, SandboxIsolationLevel.DedicatedKernel);
        var options = new CodeyBoxOptions { WorkloadTrust = "Untrusted" };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var exception = Record.Exception(() =>
            SandboxProviderSelection.ValidateWorkloadTrust(plugin, options, configuration, new ProductionHostEnvironment()));
        Assert.Null(exception);
        Assert.Equal(EgressEnforcementLocation.NotEnforced, HostPlatformSupport.GetEgressEnforcement(PluginKind));
    }

    [Fact]
    public void UnknownKind_Refusal_ListsBuiltInsAndPluginKinds()
    {
        var catalog = CatalogFor(new PluginStubSandboxProvider(PluginKind));
        var registered = catalog.AllKnownKinds;
        var classes = new List<SandboxClassOptions>
        {
            new()
            {
                Id = "default",
                DisplayName = "Default",
                Members =
                {
                    new SandboxMemberOptions
                    {
                        MemberId = "nope-member",
                        ProviderKind = "nope",
                        Capacity = 4,
                        PreferenceScore = 100,
                    },
                },
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SandboxClassesConfigBuilder.Build(classes, 1, registered, NullLogger.Instance));

        Assert.Contains("nope", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NOT silently falling back", ex.Message, StringComparison.Ordinal);
        Assert.Contains("incus", ex.Message, StringComparison.Ordinal);
        Assert.Contains(PluginKind, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownKind_RegistryMessage_ListsBuiltInsAndPluginKinds()
    {
        var listed = HostPlatformSupport.FormatKnownProviders(
            CatalogFor(new PluginStubSandboxProvider(PluginKind)).Kinds);
        Assert.Contains("incus", listed, StringComparison.Ordinal);
        Assert.Contains(PluginKind, listed, StringComparison.Ordinal);
        var builtIns = HostPlatformSupport.FormatKnownProviders(pluginKinds: null);
        Assert.DoesNotContain(PluginKind, builtIns, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalog_AcceptsPluginKind_InMemberValidation()
    {
        var catalog = CatalogFor(new PluginStubSandboxProvider(PluginKind));
        var classes = new List<SandboxClassOptions>
        {
            new()
            {
                Id = "default",
                DisplayName = "Default",
                Members =
                {
                    new SandboxMemberOptions
                    {
                        MemberId = "plugin-member",
                        ProviderKind = "ACME-VM",
                        Capacity = 4,
                        PreferenceScore = 100,
                    },
                },
            },
        };

        var built = SandboxClassesConfigBuilder.Build(
            classes, 1, catalog.AllKnownKinds, NullLogger.Instance);

        Assert.Equal(PluginKind, Assert.Single(Assert.Single(built).Members).ProviderKind);
    }

    [Fact]
    public void Catalog_RejectsBlankName_BuiltinCollision_AndDuplicates()
    {
        var blank = Assert.Throws<InvalidOperationException>(() =>
            CatalogFor(new PluginStubSandboxProvider("  ")));
        Assert.Contains(PluginId, blank.Message, StringComparison.Ordinal);

        var collision = Assert.Throws<InvalidOperationException>(() =>
            CatalogFor(new PluginStubSandboxProvider("Incus"), "acme.shadow"));
        Assert.Contains("acme.shadow", collision.Message, StringComparison.Ordinal);
        Assert.Contains("built-in", collision.Message, StringComparison.OrdinalIgnoreCase);

        var first = new PluginStubSandboxProvider(PluginKind);
        var second = new PluginStubSandboxProvider("ACME-VM ");
        var duplicate = Assert.Throws<InvalidOperationException>(() =>
            new PluginSandboxProviderCatalog([("acme.one", first), ("acme.two", second)]));
        Assert.Contains("acme.one", duplicate.Message, StringComparison.Ordinal);
        Assert.Contains("acme.two", duplicate.Message, StringComparison.Ordinal);

        var reversed = Assert.Throws<InvalidOperationException>(() =>
            new PluginSandboxProviderCatalog([("acme.two", second), ("acme.one", first)]));
        Assert.Contains("acme.one", reversed.Message, StringComparison.Ordinal);
        Assert.Contains("acme.two", reversed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_NormalizesKinds_OrderIndependently()
    {
        var first = CatalogFor(new PluginStubSandboxProvider(" Acme-VM "));
        Assert.Equal([PluginKind], first.Kinds.OrderBy(static s => s, StringComparer.Ordinal).ToList());
        Assert.True(first.TryGetProvider("ACME-VM", out var resolved));
        Assert.Equal(PluginKind, resolved.Name.Trim().ToLowerInvariant());
        Assert.Equal("(built-in)", first.PluginIdFor("incus"));
        Assert.Equal(PluginId, first.PluginIdFor("ACME-VM"));
    }
}
