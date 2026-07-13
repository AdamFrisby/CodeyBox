using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox.Incus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class ReloadableSandboxProviderTests
{
    [Fact]
    public async Task ActivatedProviders_SnapshotsRetainedIdsWithoutReadingCollectionCount()
    {
        var alpha = new SuspendingTestProvider("alpha", "alpha-");
        var beta = new TestProvider("beta", "beta-");
        var retained = new DeceptiveProviderIdCollection(
            ["beta"],
            static () => throw new InvalidOperationException("Count must not be read"));
        var router = new ReloadableSandboxProvider(
            static () => "alpha",
            () => retained,
            [Register(alpha), Register(beta)],
            NullLogger<ReloadableSandboxProvider>.Instance);

        await router.ListAllManagedAsync(CancellationToken.None);

        Assert.Equal(1, retained.EnumerationCount);
        Assert.Equal(1, alpha.ManagedListCalls);
        Assert.Equal(1, beta.ManagedListCalls);
    }

    [Fact]
    public async Task ActivatedProviders_RejectsRetainedEnumerationBeyondBoundWhenCountUnderreports()
    {
        var providers = Enumerable
            .Range(0, ReloadableSandboxProvider.MaximumRetainedInventoryProviders + 1)
            .Select(index => new TestProvider($"provider-{index}", $"provider-{index}-"))
            .ToArray();
        var retained = new DeceptiveProviderIdCollection(
            providers.Select(static provider => provider.Name).ToArray(),
            static () => 0);
        var router = new ReloadableSandboxProvider(
            () => providers[0].Name,
            () => retained,
            providers.Select(Register).ToArray(),
            NullLogger<ReloadableSandboxProvider>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.ListAllManagedAsync(CancellationToken.None));

        Assert.Contains("safety bound", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, retained.EnumerationCount);
        Assert.All(providers, static provider => Assert.Equal(0, provider.ManagedListCalls));
    }

    [Fact]
    public async Task ActivatedProviders_RejectsDuplicateRetainedIdsBeforeProviderInventory()
    {
        var alpha = new SuspendingTestProvider("alpha", "alpha-");
        var beta = new TestProvider("beta", "beta-");
        var router = new ReloadableSandboxProvider(
            static () => "alpha",
            static () => [" BETA ", "beta"],
            [Register(alpha), Register(beta)],
            NullLogger<ReloadableSandboxProvider>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.ListAllManagedAsync(CancellationToken.None));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, alpha.ManagedListCalls);
        Assert.Equal(0, beta.ManagedListCalls);
    }

    [Fact]
    public void NormalizeConfiguredProviderId_RejectsHugeSelectorBeforeNormalization()
    {
        var configured = new string(' ', 1_000_000);

        var exception = Assert.Throws<InvalidOperationException>(
            () => ReloadableSandboxProvider.NormalizeConfiguredProviderId(configured));

        Assert.Contains("safety bound", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivatedProviders_RejectsHugeRetainedIdBeforeProviderInventory()
    {
        var alpha = new SuspendingTestProvider("alpha", "alpha-");
        var beta = new TestProvider("beta", "beta-");
        var retained = new DeceptiveProviderIdCollection(
            [new string(' ', 1_000_000)],
            static () => 1);
        var router = new ReloadableSandboxProvider(
            static () => "alpha",
            () => retained,
            [Register(alpha), Register(beta)],
            NullLogger<ReloadableSandboxProvider>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.ListAllManagedAsync(CancellationToken.None));

        Assert.Equal(0, alpha.ManagedListCalls);
        Assert.Equal(0, beta.ManagedListCalls);
    }

    [Fact]
    public async Task Constructor_AndUnclassifiedBaselineDoNotConstructDormantProviders()
    {
        var selected = new TestProvider("beta", "beta-");
        var router = new ReloadableSandboxProvider(
            selectedProviderId: () => "beta",
            retainedInventoryProviderIds: static () => [],
            registrations:
            [
                new ReloadableSandboxProvider.ProviderRegistration(
                    "alpha",
                    () => throw new InvalidOperationException("dormant provider was constructed"),
                    static _ => false),
                Register(selected),
            ],
            NullLogger<ReloadableSandboxProvider>.Instance);

        Assert.Equal("beta", router.Name);
        Assert.Same(selected, router.GetProvider("beta"));
        await using var created = await router.CreateAsync(CreateSpec("unclassified-ref"));
        Assert.Equal(1, selected.CreateCalls);
    }

    [Fact]
    public async Task InvalidDormantProviderConfigDoesNotAffectSelectedProviderCreate()
    {
        var selected = new TestProvider("multipass", "cb-baseline-");
        var options = new CodeyBoxOptions
        {
            SandboxProvider = "multipass",
            StateDatabasePath = "\0invalid-unrelated-path",
            Incus = new IncusSandboxConfig
            {
                BaselineNamePrefix = "unsafe/path",
                StagingDirectory = "\0invalid-staging-path",
            },
        };
        var router = new ReloadableSandboxProvider(
            selectedProviderId: () => "multipass",
            retainedInventoryProviderIds: static () => [],
            registrations:
            [
                Register(selected),
                new ReloadableSandboxProvider.ProviderRegistration(
                    "incus",
                    () => throw new InvalidOperationException("dormant provider was constructed"),
                    baselineRef => IncusSandboxProvider.IsOwnedBaselineRef(
                        options.Incus.BaselineNamePrefix,
                        baselineRef)),
            ],
            NullLogger<ReloadableSandboxProvider>.Instance);

        await using var created = await router.CreateAsync(CreateSpec("unclassified-ref"));

        Assert.Equal(1, selected.CreateCalls);
    }

    [Fact]
    public async Task CreateAsync_UsesLiveSelectionAndExistingHandleRetainsItsOwner()
    {
        var context = CreateDefaultRouter("alpha");

        var first = await context.Router.CreateAsync(CreateSpec());
        context.Monitor.Set(Options("beta"));
        var second = await context.Router.CreateAsync(CreateSpec());

        Assert.Equal(1, context.Alpha.CreateCalls);
        Assert.Equal(1, context.Beta.CreateCalls);
        Assert.Equal("beta", context.Router.Name);
        Assert.Same(context.Alpha, Assert.IsType<TestSandbox>(first).Owner);
        Assert.Same(context.Beta, Assert.IsType<TestSandbox>(second).Owner);

        await first.DisposeAsync();
        Assert.True(Assert.IsType<TestSandbox>(first).Disposed);
        Assert.False(Assert.IsType<TestSandbox>(second).Disposed);
    }

    [Fact]
    public async Task CreateAsync_RecoveryLeaseRoutesToExactProviderAfterLiveCutover()
    {
        var context = CreateDefaultRouter("alpha");
        context.Monitor.Set(Options("beta"));
        var lease = new SandboxRecoveryLease("alpha", "alpha-retained", "private-token");
        var recoverySpec = CreateSpec("alpha-original-baseline") with
        {
            RecoveryLease = lease,
        };

        var recovered = await context.Router.CreateAsync(recoverySpec);

        Assert.Same(context.Alpha, Assert.IsType<TestSandbox>(recovered).Owner);
        Assert.Equal(1, context.Alpha.CreateCalls);
        Assert.Equal(0, context.Beta.CreateCalls);
        Assert.Same(lease, Assert.Single(context.Alpha.CreatedSpecs).RecoveryLease);
        Assert.Equal("alpha-original-baseline", context.Alpha.CreatedSpecs[0].BaselineImageRef);
    }

    [Fact]
    public async Task CreateAsync_RecoveryLeaseActivatesExplicitRetainedProviderAfterRestart()
    {
        var monitor = new MutableOptionsMonitor(Options("beta", "alpha"));
        var alpha = new SuspendingTestProvider("alpha", "alpha-");
        var beta = new TestProvider("beta", "beta-");
        var router = CreateRouter(monitor, alpha, beta);
        var lease = new SandboxRecoveryLease("alpha", "alpha-retained", "private-token");

        var recovered = await router.CreateAsync(CreateSpec() with { RecoveryLease = lease });

        Assert.Same(alpha, Assert.IsType<TestSandbox>(recovered).Owner);
        Assert.Equal(1, alpha.CreateCalls);
        Assert.Equal(0, beta.CreateCalls);
    }

    [Theory]
    [InlineData("beta", "neither selected nor retained")]
    [InlineData("BETA", "unknown lifecycle provider")]
    [InlineData("unknown", "unknown lifecycle provider")]
    public async Task CreateAsync_RecoveryLeaseFailsClosedForUnavailableProvider(
        string leaseProviderId,
        string expectedMessage)
    {
        var context = CreateDefaultRouter("alpha");
        var lease = new SandboxRecoveryLease(
            leaseProviderId,
            "retained-sandbox",
            "private-token");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Router.CreateAsync(CreateSpec() with { RecoveryLease = lease }));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, context.Alpha.CreateCalls);
        Assert.Equal(0, context.Beta.CreateCalls);
    }

    [Fact]
    public async Task HistoricalIncusPrefix_TranslatesQueuedPinAfterProviderCutover()
    {
        var incus = new TestProvider("incus", "unused-") { CurrentBaselineRef = "new-incus-current" };
        var multipass = new TestProvider("multipass", "cb-baseline-") { CurrentBaselineRef = "cb-baseline-current" };
        var router = new ReloadableSandboxProvider(
            static () => "multipass",
            static () => [],
            [
                new ReloadableSandboxProvider.ProviderRegistration(
                    "incus",
                    () => incus,
                    IncusSandboxProvider.IsRoutableBaselineRef),
                Register(multipass),
            ],
            NullLogger<ReloadableSandboxProvider>.Instance);

        await using var sandbox = await router.CreateAsync(
            CreateSpec("old-incus-profile-headless-0123456789ab"));

        var translated = Assert.Single(multipass.CreatedSpecs);
        Assert.Equal("cb-baseline-current", translated.BaselineImageRef);
        Assert.Equal(0, incus.CreateCalls);
    }

    [Fact]
    public void IncusRoutingClassifier_RecognizesHistoricalPrefixesWithoutCollidingWithMultipass()
    {
        Assert.True(IncusSandboxProvider.IsRoutableBaselineRef("old-incus-profile-headless-0123456789ab"));
        Assert.True(IncusSandboxProvider.IsRoutableBaselineRef("new-incus-profile-gui-0123456789ab"));
        Assert.False(IncusSandboxProvider.IsRoutableBaselineRef("cb-baseline-0123456789ab"));
        Assert.False(IncusSandboxProvider.IsRoutableBaselineRef("old-incus-profile-headless-0123456789AB"));
    }

    [Fact]
    public async Task CreateAsync_PropagatesSelectedProviderFailureWithoutFallback()
    {
        var context = CreateDefaultRouter("beta");
        var failure = new InvalidOperationException("selected provider failed");
        context.Beta.CreateFailure = failure;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Router.CreateAsync(CreateSpec()));

        Assert.Same(failure, actual);
        Assert.Equal(1, context.Beta.CreateCalls);
        Assert.Equal(0, context.Alpha.CreateCalls);
    }

    [Fact]
    public async Task CreateAsync_ScopesRetainedResourceFailureToSelectedProvider()
    {
        var context = CreateDefaultRouter("alpha");
        var providerFailure = RetainedCreateFailure("shared-sandbox");
        context.Alpha.CreateFailure = providerFailure;

        var scoped = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => context.Router.CreateAsync(CreateSpec()));

        Assert.Equal("shared-sandbox", scoped.RetainedSandboxName);
        Assert.Equal("alpha", scoped.RetainedSandboxLifecycleProviderId);
        Assert.Same(providerFailure, scoped.InnerException);
        Assert.Equal(1, context.Alpha.CreateCalls);
        Assert.Equal(0, context.Beta.CreateCalls);
    }

    [Fact]
    public async Task CreateAsync_RejectsAndDisposesSandboxWithMismatchedOwner()
    {
        var context = CreateDefaultRouter("alpha");
        var mismatched = new TestSandbox(context.Beta, "wrong-owner");
        context.Alpha.CreatedSandboxFactory = _ => mismatched;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Router.CreateAsync(CreateSpec()));

        Assert.True(mismatched.Disposed);
        Assert.Equal(1, context.Alpha.CreateCalls);
        Assert.Equal(0, context.Beta.CreateCalls);
    }

    [Fact]
    public void LiveSelection_RejectsUnknownProviderWithoutUsingAnExistingProvider()
    {
        var context = CreateDefaultRouter("alpha");
        context.Monitor.Set(Options("unknown"));

        var exception = Assert.Throws<InvalidOperationException>(() => _ = context.Router.Name);

        Assert.Contains("registered reloadable set", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, context.Beta.CreateCalls);
    }

    [Fact]
    public async Task CutoverConfig_RetainsNamedProviderInventoryWithoutActivatingOthers()
    {
        var monitor = new MutableOptionsMonitor(Options("beta", "alpha"));
        var alpha = new SuspendingTestProvider("alpha", "alpha-");
        var beta = new TestProvider("beta", "beta-");
        var gamma = new TestProvider("gamma", "gamma-");
        alpha.ManagedSandboxes.Add(Managed("alpha-retained"));
        beta.ManagedSandboxes.Add(Managed("beta-selected"));
        gamma.ManagedSandboxes.Add(Managed("gamma-dormant"));
        var router = CreateRouter(monitor, alpha, beta, gamma);

        var listed = await router.ListAllManagedAsync(CancellationToken.None);

        Assert.Equal(
            ["alpha-retained", "beta-selected"],
            listed.Select(static item => item.Name).OrderBy(static name => name, StringComparer.Ordinal));
        Assert.All(listed, static item => Assert.NotNull(item.LifecycleProviderId));
        Assert.Equal(1, alpha.ManagedListCalls);
        Assert.Equal(1, beta.ManagedListCalls);
        Assert.Equal(0, gamma.ManagedListCalls);
    }

    [Fact]
    public async Task ActivatedProviderRemainsInLifecycleInventoryAfterRetentionEntryIsRemoved()
    {
        var context = CreateDefaultRouter("beta", "alpha");
        context.Alpha.ManagedSandboxes.Add(Managed("alpha-retained"));
        context.Beta.ManagedSandboxes.Add(Managed("beta-selected"));
        _ = await context.Router.ListAllManagedAsync(CancellationToken.None);
        context.Monitor.Set(Options("beta"));

        var listed = await context.Router.ListAllManagedAsync(CancellationToken.None);

        Assert.Equal(
            ["alpha-retained", "beta-selected"],
            listed.Select(static item => item.Name).OrderBy(static name => name, StringComparer.Ordinal));
        Assert.Equal(2, context.Alpha.ManagedListCalls);
    }

    [Fact]
    public async Task ManagedLifecycle_RoutesDuplicateNamesOnlyThroughScopedSnapshots()
    {
        const string sharedName = "shared-sandbox";
        var context = CreateDefaultRouter("beta", "alpha");
        context.Alpha.ManagedSandboxes.Add(Managed(sharedName));
        context.Beta.ManagedSandboxes.Add(Managed(sharedName));

        var listed = await context.Router.ListAllManagedAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Router.DisposeLeakedAsync(sharedName, CancellationToken.None));

        await context.Router.DisposeLeakedAsync(
            Assert.Single(listed, static item => item.LifecycleProviderId == "alpha"),
            CancellationToken.None);
        await context.Router.DisposeLeakedAsync(
            Assert.Single(listed, static item => item.LifecycleProviderId == "beta"),
            CancellationToken.None);

        Assert.Equal([sharedName], context.Alpha.DisposedSandboxNames);
        Assert.Equal([sharedName], context.Beta.DisposedSandboxNames);
    }

    [Fact]
    public async Task ProductionLifecycleComposition_PreservesNestedReloadableProviderScope()
    {
        const string sharedName = "nested-shared";
        var context = CreateDefaultRouter("beta", "alpha");
        context.Alpha.ManagedSandboxes.Add(Managed(sharedName) with { DiskBytes = 1 });
        context.Beta.ManagedSandboxes.Add(Managed(sharedName) with { DiskBytes = 2 });
        var admitted = SandboxAdmissionControlledProvider.Wrap(
            context.Router,
            maxConcurrentSandboxes: 2,
            NullLogger.Instance);
        var lifecycle = new CompositeManagedSandboxProvider([admitted]);

        var listed = await lifecycle.ListAllManagedAsync(CancellationToken.None);
        var alphaSnapshot = Assert.Single(listed, static item => item.DiskBytes == 1);
        var betaSnapshot = Assert.Single(listed, static item => item.DiskBytes == 2);

        Assert.NotEqual(alphaSnapshot.LifecycleProviderId, betaSnapshot.LifecycleProviderId);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifecycle.DisposeLeakedAsync(sharedName, CancellationToken.None));
        await lifecycle.DisposeLeakedAsync(alphaSnapshot, CancellationToken.None);
        await lifecycle.DisposeLeakedAsync(betaSnapshot, CancellationToken.None);

        Assert.Equal([sharedName], context.Alpha.DisposedSandboxNames);
        Assert.Equal([sharedName], context.Beta.DisposedSandboxNames);
    }

    [Fact]
    public async Task Admission_RetainsSameNamedDeferredResourcesPerLifecycleProvider()
    {
        const string sharedName = "deferred-shared";
        var context = CreateDefaultRouter("alpha", "beta");
        context.Alpha.CreateFailure = RetainedCreateFailure(sharedName);
        context.Beta.CreateFailure = RetainedCreateFailure(sharedName);
        var admitted = SandboxAdmissionControlledProvider.Wrap(
            context.Router,
            maxConcurrentSandboxes: 2,
            NullLogger.Instance);
        var admission = Assert.IsAssignableFrom<ISandboxAdmissionSnapshot>(admitted);

        await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() => admitted.CreateAsync(CreateSpec()));
        context.Monitor.Set(Options("beta", "alpha"));
        await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() => admitted.CreateAsync(CreateSpec()));

        Assert.Equal(2, admission.CurrentAdmittedSandboxes);
        context.Alpha.ManagedSandboxes.Add(Managed(sharedName));
        context.Beta.ManagedSandboxes.Add(Managed(sharedName));
        var listed = await admitted.ListAllManagedAsync(CancellationToken.None);
        Assert.Equal(2, listed.Count);
        Assert.Equal(2, admission.CurrentAdmittedSandboxes);

        await admitted.DisposeLeakedAsync(
            Assert.Single(listed, static item => item.LifecycleProviderId == "alpha"),
            CancellationToken.None);
        Assert.Equal(1, admission.CurrentAdmittedSandboxes);
        await admitted.DisposeLeakedAsync(
            Assert.Single(listed, static item => item.LifecycleProviderId == "beta"),
            CancellationToken.None);
        Assert.Equal(0, admission.CurrentAdmittedSandboxes);
    }

    [Fact]
    public async Task Admission_UsesDecoratedLiveSandboxOwnerWhenReconcilingDuplicateNames()
    {
        const string sharedName = "alpha-1";
        var context = CreateDefaultRouter("alpha", "beta");
        context.Alpha.ManagedSandboxes.Add(Managed(sharedName));
        context.Beta.ManagedSandboxes.Add(Managed(sharedName));
        var admitted = SandboxAdmissionControlledProvider.Wrap(
            context.Router,
            maxConcurrentSandboxes: 1,
            NullLogger.Instance);
        var admission = Assert.IsAssignableFrom<ISandboxAdmissionSnapshot>(admitted);

        var sandbox = await admitted.CreateAsync(CreateSpec());
        await sandbox.DisposeAsync();

        Assert.Equal(1, admission.CurrentAdmittedSandboxes);
        var listed = await admitted.ListAllManagedAsync(CancellationToken.None);
        await admitted.DisposeLeakedAsync(
            Assert.Single(listed, static item => item.LifecycleProviderId == "alpha"),
            CancellationToken.None);
        Assert.Equal(0, admission.CurrentAdmittedSandboxes);
        Assert.Equal([sharedName], context.Alpha.DisposedSandboxNames);
        Assert.Empty(context.Beta.DisposedSandboxNames);
    }

    [Fact]
    public async Task Admission_ReconcilesSameNamedDeferredBaselinesPerLifecycleProvider()
    {
        const string sharedName = "shared-baseline";
        var context = CreateDefaultRouter("alpha", "beta");
        context.Alpha.EnsureBaselineFailure = RetainedBaselineFailure(sharedName);
        context.Beta.EnsureBaselineFailure = RetainedBaselineFailure(sharedName);
        var admitted = SandboxAdmissionControlledProvider.Wrap(
            context.Router,
            maxConcurrentSandboxes: 2,
            NullLogger.Instance);
        var admission = Assert.IsAssignableFrom<ISandboxAdmissionSnapshot>(admitted);
        var provisioner = Assert.IsAssignableFrom<IBaselineImageProvisioner>(admitted);
        var resolver = Assert.IsAssignableFrom<IBaselineImageResolver>(admitted);

        await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provisioner.EnsureBaselineImageAsync(
                "default", SandboxProfileFlavor.Headless, pinnedBaselineRef: null, CancellationToken.None));
        context.Monitor.Set(Options("beta", "alpha"));
        await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provisioner.EnsureBaselineImageAsync(
                "default", SandboxProfileFlavor.Headless, pinnedBaselineRef: null, CancellationToken.None));

        Assert.Equal(2, admission.CurrentAdmittedSandboxes);
        context.Alpha.Baselines.Add(new BaselineImageInfo(sharedName, null, null));
        context.Beta.Baselines.Add(new BaselineImageInfo(sharedName, null, null));
        Assert.Equal(2, (await resolver.ListBaselineImagesAsync(CancellationToken.None)).Count);
        Assert.Equal(2, admission.CurrentAdmittedSandboxes);

        context.Alpha.Baselines.Clear();
        Assert.Single(await resolver.ListBaselineImagesAsync(CancellationToken.None));
        Assert.Equal(1, admission.CurrentAdmittedSandboxes);
        context.Beta.Baselines.Clear();
        Assert.Empty(await resolver.ListBaselineImagesAsync(CancellationToken.None));
        Assert.Equal(0, admission.CurrentAdmittedSandboxes);
    }

    [Fact]
    public async Task ManagedLifecycle_ActivatedProviderFailureRefusesPartialInventory()
    {
        var logger = new CapturingLogger<ReloadableSandboxProvider>();
        var context = CreateDefaultRouter("beta", logger: logger);
        context.Beta.ManagedSandboxes.Add(Managed("beta-live"));
        context.Alpha.ManagedListFailure = new InvalidOperationException("alpha unavailable");

        var standalone = await context.Router.ListAllManagedAsync(CancellationToken.None);
        Assert.Equal("beta-live", Assert.Single(standalone).Name);

        context.Monitor.Set(Options("beta", "alpha"));
        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => context.Router.ListAllManagedAsync(CancellationToken.None));

        Assert.Contains("partial lifecycle inventory", exception.Message, StringComparison.Ordinal);
        var warning = Assert.Single(logger.Entries, static entry => entry.Level == LogLevel.Warning);
        Assert.Equal("alpha", warning.Properties["ProviderId"]);
        Assert.Equal(nameof(InvalidOperationException), warning.Properties["FailureType"]);
        Assert.Null(warning.Exception);
    }

    [Fact]
    public async Task ScopedResume_RejectsNonSuspendingOwnerWithoutCallingAnotherProvider()
    {
        var context = CreateDefaultRouter("alpha");
        context.Beta.ManagedSandboxes.Add(Managed("beta-preserved"));
        var snapshot = Managed("beta-preserved") with { LifecycleProviderId = "beta" };

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => context.Router.ResumeSandboxAsync(snapshot, CancellationToken.None));

        Assert.Contains("owning provider", exception.Message, StringComparison.Ordinal);
        Assert.Empty(context.Alpha.ResumedSandboxNames);
        Assert.Equal(0, context.Alpha.ManagedListCalls);
        Assert.Equal(1, context.Beta.ManagedListCalls);
    }

    [Fact]
    public async Task ScopedResume_RoutesExactlyToSuspendingOwnerRegardlessOfLiveSelection()
    {
        var context = CreateDefaultRouter("beta");
        context.Alpha.ManagedSandboxes.Add(Managed("alpha-preserved"));
        var snapshot = Managed("alpha-preserved") with { LifecycleProviderId = "alpha" };

        await context.Router.ResumeSandboxAsync(snapshot, CancellationToken.None);

        Assert.Equal(["alpha-preserved"], context.Alpha.ResumedSandboxNames);
        Assert.Equal(1, context.Alpha.ManagedListCalls);
        Assert.Equal(0, context.Beta.ManagedListCalls);
    }

    [Fact]
    public async Task AgentSessionReference_PreservesOpaqueOwnerAndResumesThroughScopedRouter()
    {
        var context = CreateDefaultRouter("beta");
        context.Alpha.ManagedSandboxes.Add(Managed("alpha-session"));
        ISandbox sandbox = new TestSandboxDecorator(
            new TestSandbox(context.Alpha, "alpha-session"),
            outerId: "decorator-local-id");
        var reference = AgentSessionSandboxRouting.CreateReference(sandbox);

        await AgentSessionSandboxRouting.ResumeAsync(
            context.Router,
            reference,
            CancellationToken.None);

        Assert.Equal("alpha-session", reference.Id);
        Assert.Equal("alpha", reference.Provider);
        Assert.Equal(["alpha-session"], context.Alpha.ResumedSandboxNames);
        Assert.Equal(0, context.Beta.ManagedListCalls);
    }

    [Fact]
    public async Task AgentSessionRouting_AddsConfiguredLegacyScopeWithoutOverwritingPersistedOwner()
    {
        var context = CreateDefaultRouter("beta");
        context.Alpha.ManagedSandboxes.Add(Managed("alpha-legacy"));
        var legacy = AgentSessionSandboxRouting.AddProviderScopeIfMissing(
            new AgentSessionSandboxRef("alpha-legacy"),
            legacyProviderId: "alpha");
        var alreadyScoped = AgentSessionSandboxRouting.AddProviderScopeIfMissing(
            new AgentSessionSandboxRef("beta-session", Provider: "beta"),
            legacyProviderId: "alpha");

        await AgentSessionSandboxRouting.ResumeAsync(
            context.Router,
            legacy,
            CancellationToken.None);

        Assert.Equal("alpha", legacy.Provider);
        Assert.Equal("beta", alreadyScoped.Provider);
        Assert.Equal(["alpha-legacy"], context.Alpha.ResumedSandboxNames);
    }

    [Fact]
    public async Task AgentSessionRouting_PlainProviderRejectsForeignScopedResume()
    {
        var context = CreateDefaultRouter("alpha");
        var reference = new AgentSessionSandboxRef("alpha-session", Provider: "foreign");

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            AgentSessionSandboxRouting.ResumeAsync(
                context.Alpha,
                reference,
                CancellationToken.None));

        Assert.Empty(context.Alpha.ResumedSandboxNames);
    }

    [Fact]
    public async Task OptionalCapabilities_AggregateActivatedProvidersAndFollowLiveMetricSelection()
    {
        var context = CreateDefaultRouter("beta", "alpha");
        context.Alpha.AddActive(WorkItemId.New(), "alpha-active");
        context.Beta.AddActive(WorkItemId.New(), "beta-active");
        context.Alpha.DiskSamples.Add(new DiskGuardSample("/alpha", 10, 20));
        context.Beta.DiskSamples.Add(new DiskGuardSample("/beta", 30, 20));
        context.Alpha.CapturesMetrics = false;
        context.Beta.CapturesMetrics = true;

        _ = await context.Router.ListAllManagedAsync(CancellationToken.None);

        Assert.Equal(2, context.Router.SnapshotActiveSandboxes().Count);
        Assert.Equal(2, context.Router.SnapshotActiveSandboxProgress().Count);
        Assert.Equal(
            ["/alpha", "/beta"],
            context.Router.SampleDiskGuardState().Select(static sample => sample.Path));
        Assert.True(context.Router.CapturesResourceMetrics);

        context.Monitor.Set(Options("alpha"));
        Assert.False(context.Router.CapturesResourceMetrics);
    }

    [Fact]
    public async Task ForeignBaselinePin_WithOneOfThreeOwnersTranslatesToSelectedProvider()
    {
        var monitor = new MutableOptionsMonitor(Options("gamma"));
        var alpha = new SuspendingTestProvider("alpha", "alpha-");
        var beta = new TestProvider("beta", "beta-");
        var gamma = new TestProvider("gamma", "gamma-") { CurrentBaselineRef = "gamma-current" };
        var router = CreateRouter(monitor, alpha, beta, gamma);

        await router.CreateAsync(CreateSpec("alpha-persisted"));
        await router.CreateAsync(CreateSpec("unclassified-ref"));

        Assert.Equal("gamma-current", gamma.CreatedSpecs[0].BaselineImageRef);
        Assert.Equal("unclassified-ref", gamma.CreatedSpecs[1].BaselineImageRef);
        Assert.Equal(0, alpha.CreateCalls);
        Assert.Equal(0, beta.CreateCalls);
    }

    [Fact]
    public async Task ForeignBaselinePin_RejectsAmbiguousNamespaceAcrossNProviders()
    {
        var monitor = new MutableOptionsMonitor(Options("gamma"));
        var alpha = new SuspendingTestProvider("alpha", "shared-");
        var beta = new TestProvider("beta", "shared-");
        var gamma = new TestProvider("gamma", "gamma-") { CurrentBaselineRef = "gamma-current" };
        var router = CreateRouter(monitor, alpha, beta, gamma);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.CreateAsync(CreateSpec("shared-persisted")));

        Assert.Contains("Multiple reloadable providers", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, gamma.CreateCalls);
    }

    [Fact]
    public async Task BaselineLifecycle_UsesRetainedInventoryAndRoutesUniqueOwner()
    {
        var context = CreateDefaultRouter("beta", "alpha");
        context.Alpha.Baselines.Add(new BaselineImageInfo("alpha-old", null, null));
        context.Beta.Baselines.Add(new BaselineImageInfo("beta-current", null, null));

        var listed = await context.Router.ListBaselineImagesAsync(CancellationToken.None);
        await context.Router.DisposeBaselineImageAsync("alpha-old", CancellationToken.None);

        Assert.Equal(
            ["alpha-old", "beta-current"],
            listed.Select(static item => item.Name).OrderBy(static name => name, StringComparer.Ordinal));
        Assert.Equal(
            ["alpha", "beta"],
            listed.Select(static item => item.LifecycleProviderId).OrderBy(static id => id, StringComparer.Ordinal));
        Assert.Equal(["alpha-old"], context.Alpha.DisposedBaselineNames);
        Assert.Empty(context.Beta.DisposedBaselineNames);
    }

    [Fact]
    public async Task BaselineLifecycle_AmbiguousNameAndPartialInventoryFailClosed()
    {
        const string sharedBaseline = "shared-baseline";
        var context = CreateDefaultRouter("beta", "alpha");
        context.Alpha.Baselines.Add(new BaselineImageInfo(sharedBaseline, null, null));
        context.Beta.Baselines.Add(new BaselineImageInfo(sharedBaseline, null, null));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Router.DisposeBaselineImageAsync(sharedBaseline, CancellationToken.None));
        Assert.Empty(context.Alpha.DisposedBaselineNames);
        Assert.Empty(context.Beta.DisposedBaselineNames);

        context.Alpha.BaselineListFailure = new InvalidOperationException("alpha baseline list unavailable");
        var partial = await Assert.ThrowsAsync<AggregateException>(
            () => context.Router.ListBaselineImagesAsync(CancellationToken.None));
        Assert.Contains("partial baseline inventory", partial.Message, StringComparison.Ordinal);
    }

    private static SandboxSpec CreateSpec(string? baselineRef = null) => new()
    {
        ImageReference = "image",
        BaselineImageRef = baselineRef,
        Network = new SandboxNetworkPolicy { ProfileName = "work" },
    };

    private static ManagedSandboxInfo Managed(string name) =>
        new(name, CreatedAt: null, DiskBytes: null, IsTrackedActive: false);

    private static SandboxProvisioningDeferredException RetainedCreateFailure(string name) =>
        new(
            provider: "test-provider",
            operation: "create-cleanup",
            errorClass: "delete-unconfirmed",
            detail: "test retained resource",
            recheckIn: TimeSpan.FromSeconds(1),
            retainedSandboxName: name);

    private static SandboxProvisioningDeferredException RetainedBaselineFailure(string name) =>
        new(
            provider: "test-provider",
            operation: "baseline-bake-cleanup",
            errorClass: "delete-unconfirmed",
            detail: "test retained baseline",
            recheckIn: TimeSpan.FromSeconds(1),
            retainedSandboxName: name);

    private static CodeyBoxOptions Options(string selected, params string[] retainedProviderIds) => new()
    {
        SandboxProvider = selected,
        SandboxProviderCutover = new SandboxProviderCutoverConfig
        {
            RetainedInventoryProviders = [.. retainedProviderIds],
        },
    };

    private static RouterContext CreateDefaultRouter(
        string selected,
        string? retainedProvider = null,
        ILogger<ReloadableSandboxProvider>? logger = null)
    {
        var monitor = new MutableOptionsMonitor(Options(
            selected,
            retainedProvider is null ? [] : [retainedProvider]));
        var alpha = new SuspendingTestProvider("alpha", "alpha-")
        {
            CurrentBaselineRef = "alpha-current",
        };
        var beta = new TestProvider("beta", "beta-")
        {
            CurrentBaselineRef = "beta-current",
        };
        var router = CreateRouter(monitor, alpha, beta, logger);
        return new RouterContext(router, monitor, alpha, beta);
    }

    private static ReloadableSandboxProvider CreateRouter(
        MutableOptionsMonitor monitor,
        TestProvider first,
        TestProvider second,
        ILogger<ReloadableSandboxProvider>? logger = null) =>
        CreateRouter(monitor, [first, second], logger);

    private static ReloadableSandboxProvider CreateRouter(
        MutableOptionsMonitor monitor,
        TestProvider first,
        TestProvider second,
        TestProvider third) =>
        CreateRouter(monitor, [first, second, third], logger: null);

    private static ReloadableSandboxProvider CreateRouter(
        MutableOptionsMonitor monitor,
        IReadOnlyList<TestProvider> providers,
        ILogger<ReloadableSandboxProvider>? logger) =>
        new(
            () => monitor.CurrentValue.SandboxProvider ?? string.Empty,
            () => monitor.CurrentValue.SandboxProviderCutover?.RetainedInventoryProviders?.ToArray() ?? [],
            providers.Select(Register).ToArray(),
            logger ?? NullLogger<ReloadableSandboxProvider>.Instance);

    private static ReloadableSandboxProvider.ProviderRegistration Register(TestProvider provider) =>
        new(provider.Name, () => provider, provider.OwnsBaselineRef);

    private sealed record RouterContext(
        ReloadableSandboxProvider Router,
        MutableOptionsMonitor Monitor,
        SuspendingTestProvider Alpha,
        TestProvider Beta);

    private sealed class MutableOptionsMonitor(CodeyBoxOptions initial)
    {
        private CodeyBoxOptions _value = initial;

        internal CodeyBoxOptions CurrentValue => Volatile.Read(ref _value);

        internal void Set(CodeyBoxOptions value)
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _value, value);
        }
    }

    private sealed class DeceptiveProviderIdCollection(
        IReadOnlyList<string> values,
        Func<int> readCount) : IReadOnlyCollection<string>
    {
        private int _enumerationCount;

        public int Count => readCount();
        public int EnumerationCount => Volatile.Read(ref _enumerationCount);

        public IEnumerator<string> GetEnumerator()
        {
            Interlocked.Increment(ref _enumerationCount);
            return values.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private class TestProvider(string name, string baselinePrefix) :
        ISandboxProvider,
        IActiveSandboxProvider,
        IActiveSandboxProgressProvider,
        IDiskGuardedSandboxProvider,
        IBaselineImageResolver,
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
        public Exception? EnsureBaselineFailure { get; set; }
        public bool CapturesMetrics { get; set; }
        public bool CapturesResourceMetrics => CapturesMetrics;
        public Func<string, ISandbox>? CreatedSandboxFactory { get; set; }
        public string? CurrentBaselineRef { get; set; }
        public List<SandboxSpec> CreatedSpecs { get; } = [];
        public List<ManagedSandboxInfo> ManagedSandboxes { get; } = [];
        public List<string> DisposedSandboxNames { get; } = [];
        public List<DiskGuardSample> DiskSamples { get; } = [];
        public List<BaselineImageInfo> Baselines { get; } = [];
        public List<string> DisposedBaselineNames { get; } = [];

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            CreateCalls++;
            CreatedSpecs.Add(spec);
            var sandboxId = $"{Name}-{CreateCalls}";
            return CreateFailure is null
                ? Task.FromResult(CreatedSandboxFactory?.Invoke(sandboxId)
                    ?? new TestSandbox(this, sandboxId))
                : Task.FromException<ISandbox>(CreateFailure);
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
            EnsureBaselineFailure is null
                ? Task.FromResult(CurrentBaselineRef)
                : Task.FromException<string?>(EnsureBaselineFailure);
    }

    private sealed class SuspendingTestProvider(string name, string baselinePrefix) :
        TestProvider(name, baselinePrefix),
        ISuspendingSandboxProvider
    {
        internal List<string> ResumedSandboxNames { get; } = [];

        public Task ResumeSandboxAsync(string sandboxName, CancellationToken ct)
        {
            ResumedSandboxNames.Add(sandboxName);
            return Task.CompletedTask;
        }
    }

    private sealed class TestSandbox(TestProvider owner, string id) :
        IShutdownTeardownSandbox,
        IProviderOwnedSandbox
    {
        internal TestProvider Owner { get; } = owner;
        public string Id { get; } = id;
        public string ProviderId => Owner.Name;
        internal bool Disposed { get; private set; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestSandboxDecorator(ISandbox inner, string? outerId = null) : ISandboxDecorator
    {
        public ISandbox InnerSandbox { get; } = inner;
        public string Id { get; } = outerId ?? inner.Id;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            InnerSandbox.ExecAsync(exec, ct);

        public ValueTask DisposeAsync() => InnerSandbox.DisposeAsync();
    }
}
