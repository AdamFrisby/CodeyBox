using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Placement-driven sandbox acquisition: requirements flow through the shared
/// assembler, eligibility comes from the placement decider, winners rank by
/// preference score, providers resolve from the registry, every decision is
/// logged with <c>Describe()</c>, and the two refusal kinds stay distinct
/// (permanent unplaceable vs. transient requeue).
/// </summary>
public sealed class SandboxPlacementAcquirerTests
{
    [Fact]
    public async Task Acquire_EmptyCatalog_DelegatesToFallbackUnchanged()
    {
        var fallback = new PlacementFakeSandboxProvider("fallback");
        var acquirer = new SandboxPlacementAcquirer(
            new SandboxClassesSnapshot([]),
            new PlacementFakeSandboxProviderRegistry([]),
            fallbackProvider: fallback);
        var spec = SandboxPlacementTestMembers.Spec();

        var sandbox = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, spec),
            CancellationToken.None);

        Assert.NotNull(sandbox);
        Assert.Same(spec, Assert.Single(fallback.Specs));
    }

    [Fact]
    public async Task Acquire_EmptyCatalogWithoutFallback_Throws()
    {
        var acquirer = new SandboxPlacementAcquirer(
            new SandboxClassesSnapshot([]),
            new PlacementFakeSandboxProviderRegistry([]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None));
    }

    [Fact]
    public async Task Acquire_SingleMember_CreatesOnRegistryProvider()
    {
        var provider = new PlacementFakeSandboxProvider("incus");
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("local", "incus")),
            new PlacementFakeSandboxProviderRegistry([provider]));
        var spec = SandboxPlacementTestMembers.Spec();

        await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, spec),
            CancellationToken.None);

        Assert.Same(spec, Assert.Single(provider.Specs));
    }

    [Fact]
    public async Task Acquire_CapabilityOnlyOneMemberDeclares_PlacedOnThatMember()
    {
        var plain = new PlacementFakeSandboxProvider("plain");
        var gpu = new PlacementFakeSandboxProvider("gpu");
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("plain", "plain"),
                SandboxPlacementTestMembers.Member("gpu", "gpu", capabilities: ["gpu-special"])),
            new PlacementFakeSandboxProviderRegistry([plain, gpu]));

        await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(
                WorkItemId.New(), "work", ["gpu-special"], null, null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None);

        Assert.Empty(plain.Specs);
        Assert.Equal(1, gpu.CreateCount);
    }

    [Fact]
    public async Task Acquire_CapabilityNeitherDeclares_RefusedAsUnplaceableNamingCapability()
    {
        var a = new PlacementFakeSandboxProvider("a");
        var b = new PlacementFakeSandboxProvider("b");
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("a", "a"),
                SandboxPlacementTestMembers.Member("b", "b", capabilities: ["other-cap"])),
            new PlacementFakeSandboxProviderRegistry([a, b]));

        var ex = await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(() => acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(
                WorkItemId.New(), "work", ["no-such-capability"], null, null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None));

        Assert.Equal("no-such-capability", ex.UnmetCapability);
        Assert.Contains("no-such-capability", ex.Message);
        Assert.NotNull(ex.Decision);
        Assert.True(ex.Decision.IsUnplaceable);
        Assert.Empty(a.Specs);
        Assert.Empty(b.Specs);
    }

    [Fact]
    public async Task Acquire_CredentialMismatch_DefersTransientlyWithPlacementBackoff()
    {
        var provider = new PlacementFakeSandboxProvider("a");
        var options = new ExecutorPhaseDispatchOptions { PlacementRecheckIn = TimeSpan.FromSeconds(42) };
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("a", "a", credentials: ["other-cred"])),
            new PlacementFakeSandboxProviderRegistry([provider]),
            optionsAccessor: () => options);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() => acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(
                WorkItemId.New(), "work", [], "claude", null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(42), ex.RecheckIn);
        Assert.Equal("no-eligible-host", ex.ErrorClass);
        Assert.Empty(provider.Specs);
    }

    [Fact]
    public async Task Acquire_MemberClaimsOperationItsProviderLacks_TreatedAsMissing()
    {
        var provider = new PlacementFakeSandboxProvider("a", declaredCapabilities: []);
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("a", "a", capabilities: [SandboxCapabilities.SuspendResume])),
            new PlacementFakeSandboxProviderRegistry([provider]));

        var ex = await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(() => acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(
                WorkItemId.New(), "work", [SandboxCapabilities.SuspendResume], null, null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None));

        Assert.Equal(SandboxCapabilities.SuspendResume, ex.UnmetCapability);
        Assert.Empty(provider.Specs);
    }

    [Fact]
    public async Task Acquire_ClearanceTagPassesThroughOnMemberDeclarationAlone()
    {
        var provider = new PlacementFakeSandboxProvider("a", declaredCapabilities: []);
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("a", "a", capabilities: ["team-gamma"])),
            new PlacementFakeSandboxProviderRegistry([provider]));

        await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(
                WorkItemId.New(), "work", ["team-gamma"], null, null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None);

        Assert.Equal(1, provider.CreateCount);
    }

    [Theory]
    [InlineData("low-first")]
    [InlineData("high-first")]
    public async Task Acquire_EligibleMembers_RankedByPreferenceScoreNotRegistrationOrder(string registrationOrder)
    {
        var low = new PlacementFakeSandboxProvider("low");
        var high = new PlacementFakeSandboxProvider("high");
        var members = registrationOrder == "low-first"
            ? new[]
            {
                SandboxPlacementTestMembers.Member("low", "low", preferenceScore: 10),
                SandboxPlacementTestMembers.Member("high", "high", preferenceScore: 100),
            }
            : new[]
            {
                SandboxPlacementTestMembers.Member("high", "high", preferenceScore: 100),
                SandboxPlacementTestMembers.Member("low", "low", preferenceScore: 10),
            };
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(members),
            new PlacementFakeSandboxProviderRegistry([low, high]));

        await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None);

        Assert.Empty(low.Specs);
        Assert.Equal(1, high.CreateCount);
    }

    [Fact]
    public async Task Acquire_LogsDecisionDescribeOutput()
    {
        var provider = new PlacementFakeSandboxProvider("a");
        var logger = new CapturingLogger();
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("a", "a")),
            new PlacementFakeSandboxProviderRegistry([provider]),
            log: logger);

        await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None);

        var logged = string.Join("\n", logger.Messages);
        Assert.Contains("candidates=[a=selected]", logged);
    }

    [Fact]
    public async Task Acquire_Refusal_LogsDecisionDescribeOutput()
    {
        var provider = new PlacementFakeSandboxProvider("a");
        var logger = new CapturingLogger();
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("a", "a")),
            new PlacementFakeSandboxProviderRegistry([provider]),
            log: logger);

        await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(() => acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(
                WorkItemId.New(), "work", ["missing-cap"], null, null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None));

        var logged = string.Join("\n", logger.Messages);
        Assert.Contains("unplaceable missing-capability=missing-cap", logged);
        Assert.Contains("candidates=[a=missing-capability:missing-cap]", logged);
    }

    [Fact]
    public async Task Acquire_UnknownProviderKind_FailsClosed()
    {
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("a", "nope-kind")),
            new PlacementFakeSandboxProviderRegistry([]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None));
    }

    private sealed class CapturingLogger : ILogger<SandboxPlacementAcquirer>
    {
        public readonly List<string> Messages = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullLogger<SandboxPlacementAcquirer>.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
        }
    }
}
