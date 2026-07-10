using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

public sealed class HotSwappableSandboxProviderTests
{
    [Theory]
    [InlineData("incus")]
    [InlineData("multipass")]
    public void StandaloneSelection_DoesNotConstructDormantBackend(string selected)
    {
        var monitor = new MutableOptionsMonitor(new CodeyBoxOptions { SandboxProvider = selected });
        var selectedProvider = new TestProvider(selected, selected + "-");
        Func<ISandboxProvider> multipassFactory = selected == "multipass"
            ? () => selectedProvider
            : () => throw new InvalidOperationException("dormant Multipass factory was invoked");
        Func<ISandboxProvider> incusFactory = selected == "incus"
            ? () => selectedProvider
            : () => throw new InvalidOperationException("dormant Incus factory was invoked");
        var provider = new HotSwappableSandboxProvider(
            monitor,
            multipassFactory,
            incusFactory,
            NullLogger<HotSwappableSandboxProvider>.Instance);

        Assert.Equal(selected, provider.Name);
        Assert.Same(selectedProvider, selected == "incus" ? provider.IncusProvider : provider.MultipassProvider);
    }

    [Fact]
    public void SessionReference_UnwrapsDecoratorAndPersistsCreatingProvider()
    {
        var owner = new TestProvider("incus", "incus-");
        ISandbox sandbox = new TestSandboxDecorator(new TestSandbox(owner, "incus-session"));

        var reference = AgentSessionSandboxRouting.CreateReference(sandbox);

        Assert.Equal("incus-session", reference.Id);
        Assert.Equal(HotSwappableSandboxProvider.IncusProviderId, reference.Provider);
    }

    [Fact]
    public async Task SessionResume_IncusReferenceNeverReachesMultipass()
    {
        var multipass = new TestProvider("multipass", "mp-");
        var reference = new AgentSessionSandboxRef(
            "incus-session",
            HotSwappableSandboxProvider.IncusProviderId);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => AgentSessionSandboxRouting.ResumeWithMultipassAsync(
                multipass,
                reference,
                CancellationToken.None));

        Assert.Contains("Incus", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(multipass.ResumedSandboxNames);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("multipass")]
    public async Task SessionResume_MultipassAndLegacyReferencesStillResume(string? providerId)
    {
        var multipass = new TestProvider("multipass", "mp-");

        await AgentSessionSandboxRouting.ResumeWithMultipassAsync(
            multipass,
            new AgentSessionSandboxRef("multipass-session", providerId),
            CancellationToken.None);

        Assert.Equal(["multipass-session"], multipass.ResumedSandboxNames);
    }

    [Fact]
    public async Task ProviderResume_IncusIdNeverReachesMultipass()
    {
        var (provider, monitor, multipass, incus) = CreateProvider("incus");
        incus.ManagedSandboxes.Add(new ManagedSandboxInfo("incus-preserved", null, null, false));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ResumeSandboxAsync("incus-preserved", CancellationToken.None));

        Assert.Empty(multipass.ResumedSandboxNames);
        Assert.Empty(incus.ResumedSandboxNames);
        Assert.Equal(0, multipass.ManagedListCalls);
        Assert.Equal(1, incus.ManagedListCalls);
    }

    [Fact]
    public async Task CreateAsync_UsesLiveSelection_AndExistingHandleKeepsOriginalOwner()
    {
        var (provider, monitor, multipass, incus) = CreateProvider("multipass");

        var first = await provider.CreateAsync(CreateSpec());
        monitor.Set(new CodeyBoxOptions { SandboxProvider = "incus" });
        var second = await provider.CreateAsync(CreateSpec());

        Assert.Equal(1, multipass.CreateCalls);
        Assert.Equal(1, incus.CreateCalls);
        Assert.Equal("incus", provider.Name);

        await first.DisposeAsync();
        Assert.True(Assert.IsType<TestSandbox>(first).Disposed);
        Assert.False(Assert.IsType<TestSandbox>(second).Disposed);
        Assert.Same(multipass, Assert.IsType<TestSandbox>(first).Owner);
    }

    [Fact]
    public async Task CreateAsync_DoesNotFallbackWhenSelectedProviderFails()
    {
        var (provider, monitor, multipass, incus) = CreateProvider("incus");
        var failure = new InvalidOperationException("selected incus failed");
        incus.CreateFailure = failure;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CreateAsync(CreateSpec()));

        Assert.Same(failure, actual);
        Assert.Equal(1, incus.CreateCalls);
        Assert.Equal(0, multipass.CreateCalls);
    }

    [Fact]
    public async Task ManagedLifecycle_ListsBothAndRoutesProviderScopedSnapshots()
    {
        var (provider, monitor, multipass, incus) = CreateProvider("incus");
        const string sharedName = "codeybox-shared";
        multipass.ManagedSandboxes.Add(new ManagedSandboxInfo(sharedName, null, null, false));
        incus.ManagedSandboxes.Add(new ManagedSandboxInfo(sharedName, null, null, false));
        monitor.Set(new CodeyBoxOptions { SandboxProvider = "multipass" });
        _ = provider.Name;
        monitor.Set(new CodeyBoxOptions { SandboxProvider = "incus" });

        var listed = await provider.ListAllManagedAsync(CancellationToken.None);

        Assert.Collection(
            listed.OrderBy(static item => item.LifecycleProviderId, StringComparer.Ordinal),
            item => Assert.Equal(HotSwappableSandboxProvider.IncusProviderId, item.LifecycleProviderId),
            item => Assert.Equal(HotSwappableSandboxProvider.MultipassProviderId, item.LifecycleProviderId));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.DisposeLeakedAsync(sharedName, CancellationToken.None));

        await provider.DisposeLeakedAsync(
            Assert.Single(listed, static item => item.LifecycleProviderId == HotSwappableSandboxProvider.MultipassProviderId),
            CancellationToken.None);
        await provider.DisposeLeakedAsync(
            Assert.Single(listed, static item => item.LifecycleProviderId == HotSwappableSandboxProvider.IncusProviderId),
            CancellationToken.None);

        Assert.Equal([sharedName], multipass.DisposedSandboxNames);
        Assert.Equal([sharedName], incus.DisposedSandboxNames);
    }

    [Fact]
    public async Task ProductionLifecycleComposition_PreservesNestedCutoverOwnerForDuplicateNames()
    {
        var (cutover, monitor, multipass, incus) = CreateProvider("incus");
        const string sharedName = "codeybox-production-shared";
        multipass.ManagedSandboxes.Add(new ManagedSandboxInfo(
            sharedName,
            CreatedAt: null,
            DiskBytes: 1,
            IsTrackedActive: false));
        incus.ManagedSandboxes.Add(new ManagedSandboxInfo(
            sharedName,
            CreatedAt: null,
            DiskBytes: 2,
            IsTrackedActive: false));
        monitor.Set(new CodeyBoxOptions { SandboxProvider = "multipass" });
        _ = cutover.Name;
        monitor.Set(new CodeyBoxOptions { SandboxProvider = "incus" });
        var admitted = SandboxAdmissionControlledProvider.Wrap(
            cutover,
            maxConcurrentSandboxes: 2,
            NullLogger.Instance);
        var lifecycle = new CompositeManagedSandboxProvider([admitted]);

        var listed = await lifecycle.ListAllManagedAsync(CancellationToken.None);
        var multipassSnapshot = Assert.Single(listed, static item => item.DiskBytes == 1);
        var incusSnapshot = Assert.Single(listed, static item => item.DiskBytes == 2);

        Assert.NotNull(multipassSnapshot.LifecycleProviderId);
        Assert.NotNull(incusSnapshot.LifecycleProviderId);
        Assert.NotEqual(multipassSnapshot.LifecycleProviderId, incusSnapshot.LifecycleProviderId);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifecycle.DisposeLeakedAsync(sharedName, CancellationToken.None));

        await lifecycle.DisposeLeakedAsync(multipassSnapshot, CancellationToken.None);
        Assert.Equal([sharedName], multipass.DisposedSandboxNames);
        Assert.Empty(incus.DisposedSandboxNames);

        await lifecycle.DisposeLeakedAsync(incusSnapshot, CancellationToken.None);
        Assert.Equal([sharedName], incus.DisposedSandboxNames);
    }

    [Fact]
    public async Task ManagedLifecycle_DormantProviderIsIndependent_ButActivatedCutoverFailsClosed()
    {
        var logger = new CapturingLogger<HotSwappableSandboxProvider>();
        var (provider, monitor, multipass, incus) = CreateProvider("incus", logger);
        multipass.ManagedListFailure = new InvalidOperationException("multipass list unavailable");
        incus.ManagedSandboxes.Add(new ManagedSandboxInfo("incus-live", null, null, false));

        var standalone = await provider.ListAllManagedAsync(CancellationToken.None);
        Assert.Equal("incus-live", Assert.Single(standalone).Name);
        Assert.Empty(logger.Entries);

        monitor.Set(new CodeyBoxOptions { SandboxProvider = "multipass" });
        _ = provider.Name;
        monitor.Set(new CodeyBoxOptions { SandboxProvider = "incus" });
        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => provider.ListAllManagedAsync(CancellationToken.None));

        Assert.Contains("partial lifecycle inventory", exception.Message, StringComparison.Ordinal);
        var warning = Assert.Single(logger.Entries, static entry => entry.Level == LogLevel.Warning);
        Assert.Equal(HotSwappableSandboxProvider.MultipassProviderId, warning.Properties["ProviderId"]);
        Assert.Equal(nameof(InvalidOperationException), warning.Properties["FailureType"]);
        Assert.Null(warning.Exception);
    }

    [Fact]
    public async Task CrossRestartCutoverFlag_ActivatesMultipassInventoryFromIncusStartup()
    {
        var (provider, _, multipass, incus) = CreateProvider(
            "incus",
            includeMultipassCutoverInventory: true);
        multipass.ManagedSandboxes.Add(new ManagedSandboxInfo("mp-preserved", null, null, false));
        incus.ManagedSandboxes.Add(new ManagedSandboxInfo("incus-live", null, null, false));

        var listed = await provider.ListAllManagedAsync(CancellationToken.None);

        Assert.Equal(
            ["incus-live", "mp-preserved"],
            listed.Select(static sandbox => sandbox.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task OptionalCapabilities_AggregateBoth_AndSuspendLifecycleAlwaysRoutesToMultipass()
    {
        var (provider, monitor, multipass, incus) = CreateProvider("incus");
        var multipassWork = WorkItemId.New();
        var incusWork = WorkItemId.New();
        multipass.AddActive(multipassWork, "mp-active");
        incus.AddActive(incusWork, "incus-active");
        multipass.DiskSamples.Add(new DiskGuardSample("/multipass", 10, 20));
        multipass.ManagedSandboxes.Add(new ManagedSandboxInfo("multipass-preserved", null, null, false));
        incus.DiskSamples.Add(new DiskGuardSample("/incus", 30, 20));
        multipass.CapturesMetrics = false;
        incus.CapturesMetrics = true;
        monitor.Set(new CodeyBoxOptions { SandboxProvider = "multipass" });
        _ = provider.Name;
        monitor.Set(new CodeyBoxOptions { SandboxProvider = "incus" });

        Assert.Equal(2, provider.SnapshotActiveSandboxes().Count);
        Assert.Equal(2, provider.SnapshotActiveSandboxProgress().Count);
        Assert.Equal(["/multipass", "/incus"], provider.SampleDiskGuardState().Select(static sample => sample.Path));
        Assert.True(provider.CapturesResourceMetrics);

        await provider.ResumeSandboxAsync("multipass-preserved", CancellationToken.None);
        Assert.Equal(["multipass-preserved"], multipass.ResumedSandboxNames);
        Assert.Empty(incus.ResumedSandboxNames);

        monitor.Set(new CodeyBoxOptions { SandboxProvider = "multipass" });
        Assert.False(provider.CapturesResourceMetrics);
    }

    [Fact]
    public async Task ForeignBaselinePin_IsTranslatedOnlyWhenOtherProviderOwnsItsNamespace()
    {
        var (provider, _, _, incus) = CreateProvider("incus");
        incus.CurrentBaselineRef = "incus-current";

        await provider.CreateAsync(CreateSpec("mp-persisted"));
        Assert.Equal("incus-current", Assert.Single(incus.CreatedSpecs).BaselineImageRef);

        incus.CreatedSpecs.Clear();
        await provider.CreateAsync(CreateSpec("unclassified-ref"));
        Assert.Equal("unclassified-ref", Assert.Single(incus.CreatedSpecs).BaselineImageRef);
    }

    [Fact]
    public async Task BaselineLifecycle_ListsBothAndRoutesUniqueNamesWithoutUsingSelector()
    {
        var (provider, monitor, multipass, incus) = CreateProvider("multipass");
        multipass.Baselines.Add(new BaselineImageInfo("mp-old", null, null));
        incus.Baselines.Add(new BaselineImageInfo("incus-old", null, null));
        monitor.Set(new CodeyBoxOptions { SandboxProvider = "incus" });
        _ = provider.Name;
        monitor.Set(new CodeyBoxOptions { SandboxProvider = "multipass" });

        var listed = await provider.ListBaselineImagesAsync(CancellationToken.None);
        Assert.Equal(["incus-old", "mp-old"], listed.Select(static item => item.Name).Order());

        monitor.Set(new CodeyBoxOptions { SandboxProvider = "incus" });
        await provider.DisposeBaselineImageAsync("mp-old", CancellationToken.None);

        Assert.Equal(["mp-old"], multipass.DisposedBaselineNames);
        Assert.Empty(incus.DisposedBaselineNames);
    }

    [Fact]
    public async Task BaselineLifecycle_DormantProviderIsIndependent_ButActivatedCutoverFailsClosed()
    {
        var logger = new CapturingLogger<HotSwappableSandboxProvider>();
        var (provider, monitor, multipass, incus) = CreateProvider("multipass", logger);
        incus.BaselineListFailure = new InvalidOperationException("incus baseline list unavailable");
        multipass.Baselines.Add(new BaselineImageInfo("mp-current", null, null));

        var standalone = await provider.ListBaselineImagesAsync(CancellationToken.None);
        Assert.Equal("mp-current", Assert.Single(standalone).Name);
        Assert.Empty(logger.Entries);

        monitor.Set(new CodeyBoxOptions { SandboxProvider = "incus" });
        _ = provider.Name;
        monitor.Set(new CodeyBoxOptions { SandboxProvider = "multipass" });
        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => provider.ListBaselineImagesAsync(CancellationToken.None));

        Assert.Contains("partial baseline inventory", exception.Message, StringComparison.Ordinal);
        var warning = Assert.Single(logger.Entries, static entry => entry.Level == LogLevel.Warning);
        Assert.Equal(HotSwappableSandboxProvider.IncusProviderId, warning.Properties["ProviderId"]);
        Assert.Equal(nameof(InvalidOperationException), warning.Properties["FailureType"]);
        Assert.Null(warning.Exception);
    }

    [Theory]
    [InlineData("multipass", "incus")]
    [InlineData("incus", "multipass")]
    [InlineData(" MULTIPASS ", " Incus ")]
    public void ImmutableValidator_AllowsOnlyMultipassIncusCutover(string startupKind, string candidateKind)
    {
        var startup = new CodeyBoxOptions { SandboxProvider = startupKind };
        var validator = new ImmutableCodeyBoxOptionsValidator(startup);

        var result = validator.Validate(null, new CodeyBoxOptions { SandboxProvider = candidateKind });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("process", "incus")]
    [InlineData("incus", "process")]
    [InlineData("multipass", "bubblewrap")]
    public void ImmutableValidator_RejectsProviderChangesOutsideCutover(string startupKind, string candidateKind)
    {
        var startup = new CodeyBoxOptions { SandboxProvider = startupKind };
        var validator = new ImmutableCodeyBoxOptionsValidator(startup);

        var result = validator.Validate(null, new CodeyBoxOptions { SandboxProvider = candidateKind });

        Assert.True(result.Failed);
        Assert.Contains("SandboxProvider", result.FailureMessage);
    }

    [Fact]
    public void ImmutableValidator_GuardsIncusProjectAndEffectiveStagingIdentity()
    {
        var startup = new CodeyBoxOptions
        {
            SandboxProvider = "incus",
            StateDatabasePath = "/var/lib/codeybox/state.db",
            Incus = new IncusSandboxConfig
            {
                ProjectName = "codeybox",
                StagingDirectory = null,
            },
        };
        var validator = new ImmutableCodeyBoxOptionsValidator(startup);
        var candidate = WithIncus(
            startup,
            projectName: "codeybox-other",
            stagingDirectory: "/var/lib/codeybox/other-staging");

        var result = validator.Validate(null, candidate);

        Assert.True(result.Failed);
        Assert.Contains("Incus:ProjectName", result.FailureMessage);
        Assert.Contains("Incus:StagingDirectory", result.FailureMessage);
    }

    [Fact]
    public void ImmutableValidator_TreatsEquivalentEffectiveIncusStagingPathAsUnchanged()
    {
        var startup = new CodeyBoxOptions
        {
            SandboxProvider = "multipass",
            StateDatabasePath = "/var/lib/codeybox/state.db",
        };
        var validator = new ImmutableCodeyBoxOptionsValidator(startup);
        var candidate = WithIncus(
            startup,
            projectName: startup.Incus.ProjectName,
            stagingDirectory: "/var/lib/codeybox/incus-staging/.");

        var result = validator.Validate(null, candidate);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ImmutableValidator_DoesNotGuardDormantIncusIdentityForOtherProviders()
    {
        var startup = new CodeyBoxOptions { SandboxProvider = "process" };
        var validator = new ImmutableCodeyBoxOptionsValidator(startup);
        var candidate = WithIncus(
            startup,
            projectName: "unused-project",
            stagingDirectory: "/tmp/unused-incus-staging");

        var result = validator.Validate(null, candidate);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void OptionsMonitor_AcceptsMultipassIncusSelectorReload()
    {
        var values = new Dictionary<string, string?>
        {
            ["CodeyBox:SandboxProvider"] = "multipass",
            ["CodeyBox:StateDatabasePath"] = "/var/lib/codeybox/state.db",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var startup = configuration.GetSection("CodeyBox").Get<CodeyBoxOptions>()
            ?? throw new InvalidOperationException("Test configuration did not bind.");
        var services = new ServiceCollection();
        services.Configure<CodeyBoxOptions>(configuration.GetSection("CodeyBox"));
        services.AddSingleton<IOptionsMonitorCache<CodeyBoxOptions>>(
            new RetainingOptionsMonitorCache<CodeyBoxOptions>(startup));
        services.AddSingleton<IValidateOptions<CodeyBoxOptions>>(
            new ImmutableCodeyBoxOptionsValidator(startup));
        using var serviceProvider = services.BuildServiceProvider();
        var monitor = serviceProvider.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>();

        configuration["CodeyBox:SandboxProvider"] = "incus";
        ((IConfigurationRoot)configuration).Reload();

        Assert.Equal("incus", monitor.CurrentValue.SandboxProvider);
    }

    private static SandboxSpec CreateSpec(string? baselineRef = null) => new()
    {
        ImageReference = "image",
        BaselineImageRef = baselineRef,
        Network = new SandboxNetworkPolicy { ProfileName = "work" },
    };

    private static CodeyBoxOptions WithIncus(
        CodeyBoxOptions source,
        string projectName,
        string? stagingDirectory) => new()
        {
            SandboxProvider = source.SandboxProvider,
            StateDatabasePath = source.StateDatabasePath,
            GitRootDirectory = source.GitRootDirectory,
            SharedUpstreamMirrorDirectory = source.SharedUpstreamMirrorDirectory,
            EnableSharedUpstreamMirror = source.EnableSharedUpstreamMirror,
            Incus = new IncusSandboxConfig
            {
                ProjectName = projectName,
                StagingDirectory = stagingDirectory,
            },
        };

    private static (HotSwappableSandboxProvider Provider, MutableOptionsMonitor Monitor, TestProvider Multipass, TestProvider Incus)
        CreateProvider(
            string selected,
            ILogger<HotSwappableSandboxProvider>? logger = null,
            bool includeMultipassCutoverInventory = false)
    {
        var monitor = new MutableOptionsMonitor(new CodeyBoxOptions
        {
            SandboxProvider = selected,
            Incus = new IncusSandboxConfig
            {
                IncludeMultipassCutoverInventory = includeMultipassCutoverInventory,
            },
        });
        var multipass = new TestProvider("multipass", "mp-") { CurrentBaselineRef = "mp-current" };
        var incus = new TestProvider("incus", "incus-") { CurrentBaselineRef = "incus-current" };
        var provider = new HotSwappableSandboxProvider(
            monitor,
            multipass,
            incus,
            logger ?? NullLogger<HotSwappableSandboxProvider>.Instance);
        return (provider, monitor, multipass, incus);
    }

    private sealed class MutableOptionsMonitor(CodeyBoxOptions initial) : IOptionsMonitor<CodeyBoxOptions>
    {
        private CodeyBoxOptions _value = initial;

        public CodeyBoxOptions CurrentValue => _value;
        public CodeyBoxOptions Get(string? name) => _value;
        public IDisposable OnChange(Action<CodeyBoxOptions, string?> listener) => NoopDisposable.Instance;
        public void Set(CodeyBoxOptions value) => _value = value;

        private sealed class NoopDisposable : IDisposable
        {
            public static NoopDisposable Instance { get; } = new();
            public void Dispose() { }
        }
    }

    private sealed class TestProvider(string name, string baselinePrefix) :
        ISandboxProvider,
        IActiveSandboxProvider,
        IActiveSandboxProgressProvider,
        IDiskGuardedSandboxProvider,
        ISuspendingSandboxProvider,
        IBaselineImageResolver,
        IBaselineRefNamespace,
        IBaselineImageProvisioner,
        IResourceMetricsCapturingProvider
    {
        private readonly List<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> _active = [];

        public string Name { get; } = name;
        public int CreateCalls { get; private set; }
        public int ManagedListCalls { get; private set; }
        public Exception? CreateFailure { get; set; }
        public Exception? ManagedListFailure { get; set; }
        public Exception? BaselineListFailure { get; set; }
        public bool CapturesMetrics { get; set; }
        public bool CapturesResourceMetrics => CapturesMetrics;
        public string? CurrentBaselineRef { get; set; }
        public List<SandboxSpec> CreatedSpecs { get; } = [];
        public List<ManagedSandboxInfo> ManagedSandboxes { get; } = [];
        public List<string> DisposedSandboxNames { get; } = [];
        public List<string> ResumedSandboxNames { get; } = [];
        public List<DiskGuardSample> DiskSamples { get; } = [];
        public List<BaselineImageInfo> Baselines { get; } = [];
        public List<string> DisposedBaselineNames { get; } = [];

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            CreateCalls++;
            CreatedSpecs.Add(spec);
            if (CreateFailure is not null)
                return Task.FromException<ISandbox>(CreateFailure);
            return Task.FromResult<ISandbox>(new TestSandbox(this, $"{Name}-{CreateCalls}"));
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
        {
            ManagedListCalls++;
            return ManagedListFailure is null
                ? Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>(ManagedSandboxes.ToArray())
                : Task.FromException<IReadOnlyList<ManagedSandboxInfo>>(ManagedListFailure);
        }

        public Task DisposeLeakedAsync(string sandboxName, CancellationToken ct)
        {
            DisposedSandboxNames.Add(sandboxName);
            return Task.CompletedTask;
        }

        public void AddActive(WorkItemId workItemId, string sandboxId) =>
            _active.Add((workItemId, new TestSandbox(this, sandboxId)));

        public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() =>
            _active.ToArray();

        public IReadOnlyList<ActiveSandboxProgress> SnapshotActiveSandboxProgress() =>
            _active.Select(static item => new ActiveSandboxProgress(item.WorkItemId, item.Sandbox.Id)).ToArray();

        public IReadOnlyList<DiskGuardSample> SampleDiskGuardState() => DiskSamples.ToArray();

        public Task ResumeSandboxAsync(string sandboxName, CancellationToken ct)
        {
            ResumedSandboxNames.Add(sandboxName);
            return Task.CompletedTask;
        }

        public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor) =>
            CurrentBaselineRef;

        public bool OwnsBaselineRef(string baselineRef) =>
            baselineRef.StartsWith(baselinePrefix, StringComparison.Ordinal);

        public Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct) =>
            BaselineListFailure is null
                ? Task.FromResult<IReadOnlyList<BaselineImageInfo>>(Baselines.ToArray())
                : Task.FromException<IReadOnlyList<BaselineImageInfo>>(BaselineListFailure);

        public Task DisposeBaselineImageAsync(string baselineName, CancellationToken ct)
        {
            DisposedBaselineNames.Add(baselineName);
            return Task.CompletedTask;
        }

        public Task<string?> EnsureBaselineImageAsync(
            string profileName,
            SandboxProfileFlavor flavor,
            string? pinnedBaselineRef,
            CancellationToken ct) =>
            Task.FromResult(CurrentBaselineRef);
    }

    private sealed class TestSandbox(TestProvider owner, string id) : IShutdownTeardownSandbox, IProviderOwnedSandbox
    {
        public TestProvider Owner { get; } = owner;
        public string Id { get; } = id;
        public string ProviderId => Owner.Name;
        public bool Disposed { get; private set; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestSandboxDecorator(ISandbox inner) : ISandboxDecorator
    {
        public ISandbox InnerSandbox { get; } = inner;
        public string Id => InnerSandbox.Id;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            InnerSandbox.ExecAsync(exec, ct);

        public ValueTask DisposeAsync() => InnerSandbox.DisposeAsync();
    }
}
