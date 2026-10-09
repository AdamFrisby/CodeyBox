using CodeyBox.Core.ExternalBuilds;
using CodeyBox.Orchestrator.ExternalBuilds;

namespace CodeyBox.Tests.ExternalBuilds;

/// <summary>Sandbox tools: scoped capabilities, ownership rechecks,
/// expiry/revocation, approved-target enforcement, bounded diagnostics.</summary>
public sealed class ExternalBuildToolsTests
{
    private static (ExternalBuildSandboxTools Tools, ExternalBuildCapability Cap, FakeSnapshotBuildProvider Provider)
        Build(ControllableClock? clock = null)
    {
        clock ??= new ControllableClock(DateTimeOffset.UtcNow);
        var opts = ExternalBuildTestKit.Options();
        var store = new InMemoryExternalBuildStore();
        var provider = new FakeSnapshotBuildProvider();
        var service = new ExternalBuildService(store, [provider], () => opts, clock);
        var tools = new ExternalBuildSandboxTools(service, store, () => opts, clock, [provider]);
        var cap = tools.IssueCapability("proj", "w1", "work", 1, 1);
        return (tools, cap, provider);
    }

    [Fact]
    public async Task Start_Status_Result_Cancel_FlowThroughRealService()
    {
        var (tools, cap, _) = Build();
        var started = await tools.StartAsync(cap.Handle, "fake-target", ExternalBuildTestKit.Source(), "k1");
        Assert.Equal(ExternalBuildState.Queued, started.State);

        var status = await tools.StatusAsync(cap.Handle, started.Id);
        Assert.Equal(started.Id, status.Id);

        var cancelled = await tools.CancelAsync(cap.Handle, started.Id);
        Assert.Equal(ExternalBuildState.Cancelled, cancelled.State);
        Assert.Equal(ExternalBuildTerminalCause.ProviderConfirmedCancellation, cancelled.TerminalCause);
    }

    [Fact]
    public async Task ForeignHandle_Rejected()
    {
        var (tools, cap, _) = Build();
        var started = await tools.StartAsync(cap.Handle, "fake-target", ExternalBuildTestKit.Source(), "k1");
        var other = tools.IssueCapability("proj", "other-work", "work", 1, 1);
        await Assert.ThrowsAsync<ExternalBuildOwnershipException>(() =>
            tools.StatusAsync(other.Handle, started.Id));
    }

    [Fact]
    public async Task StaleAttemptHandle_Rejected()
    {
        var (tools, cap, _) = Build();
        var started = await tools.StartAsync(cap.Handle, "fake-target", ExternalBuildTestKit.Source(), "k1");
        var stale = tools.IssueCapability("proj", "w1", "work", 1, 2);
        await Assert.ThrowsAsync<ExternalBuildOwnershipException>(() =>
            tools.StatusAsync(stale.Handle, started.Id));
    }

    [Fact]
    public async Task ExpiredCapability_Rejected()
    {
        var clock = new ControllableClock(DateTimeOffset.UtcNow);
        var (tools, cap, _) = Build(clock);
        var started = await tools.StartAsync(cap.Handle, "fake-target", ExternalBuildTestKit.Source(), "k1");
        clock.Advance(TimeSpan.FromHours(2));
        await Assert.ThrowsAsync<ExternalBuildCapabilityExpiredException>(() =>
            tools.StatusAsync(cap.Handle, started.Id));
    }

    [Fact]
    public async Task RevokedCapability_Rejected()
    {
        var (tools, cap, _) = Build();
        var started = await tools.StartAsync(cap.Handle, "fake-target", ExternalBuildTestKit.Source(), "k1");
        tools.Revoke(cap.Handle);
        await Assert.ThrowsAsync<ExternalBuildCapabilityExpiredException>(() =>
            tools.StatusAsync(cap.Handle, started.Id));
    }

    [Fact]
    public async Task ArbitraryEndpoint_Rejected()
    {
        var (tools, cap, _) = Build();
        await Assert.ThrowsAsync<ExternalBuildTargetNotApprovedException>(() =>
            tools.StartAsync(cap.Handle, "https://evil.example/run", ExternalBuildTestKit.Source(), "k1"));
    }

    [Fact]
    public async Task Diagnostics_BoundedAndRedacted()
    {
        var clock = new ControllableClock(DateTimeOffset.UtcNow);
        var opts = ExternalBuildTestKit.Options();
        opts.MaxDiagnosticsChars = 32;
        var store = new InMemoryExternalBuildStore();
        var provider = new FakeSnapshotBuildProvider
        {
            EvidenceFactory = _ => new ExternalBuildEvidence
            {
                Compile = ExternalBuildDimensionOutcome.Passed,
                Tests = ExternalBuildDimensionOutcome.Passed,
                Package = ExternalBuildDimensionOutcome.Passed,
                SourceDigestSha256 = new string('a', 64),
                ProviderRunId = "run-1",
                WorkflowIdentity = "bearer SECRET-should-be-redacted-0123456789\n" + new string('w', 200),
                ApprovedTargetName = "fake-target",
                Toolchain = "fake-toolchain-1",
                Platform = "fake-platform-1",
                Configuration = "release",
                Authoritative = true,
                CapturedAt = DateTimeOffset.UtcNow,
            },
            PollsToTerminal = 1,
        };
        var service = new ExternalBuildService(store, [provider], () => opts, clock);
        var tools = new ExternalBuildSandboxTools(service, store, () => opts, clock, [provider]);
        var cap = tools.IssueCapability("proj", "w1", "work", 1, 1);
        var started = await tools.StartAsync(cap.Handle, "fake-target", ExternalBuildTestKit.Source(), "k1");
        await tools.StatusAsync(cap.Handle, started.Id);
        var text = await tools.DiagnosticsAsync(cap.Handle, started.Id);
        Assert.True(text.Length <= 32 + "[...truncated]".Length);
        Assert.Contains("[redacted]", text);
        Assert.DoesNotContain("SECRET-should-be-redacted", text);
    }

    [Fact]
    public async Task ArtifactListRead_EnforcesDigest()
    {
        var (tools, cap, provider) = Build();
        var started = await tools.StartAsync(cap.Handle, "fake-target", ExternalBuildTestKit.Source(), "k1");
        var refs = await tools.ListArtifactsAsync(cap.Handle, started.Id, provider);
        Assert.Single(refs);
        var payload = await tools.ReadArtifactAsync(cap.Handle, started.Id, "package.zip", provider);
        Assert.Equal(payload.ContentDigestSha256, ExternalBuildProvenance.DigestBytes(payload.Content));
    }

    [Fact]
    public async Task RunnerTransport_McpBaseline_AndCliFallback()
    {
        var clock = new ControllableClock(DateTimeOffset.UtcNow);
        var opts = ExternalBuildTestKit.Options();
        var store = new InMemoryExternalBuildStore();
        var provider = new FakeSnapshotBuildProvider { PollsToTerminal = 100 };
        var service = new ExternalBuildService(store, [provider], () => opts, clock);
        var tools = new ExternalBuildSandboxTools(service, store, () => opts, clock);
        var cap = tools.IssueCapability("proj", "w1", "work", 1, 1);
        var transport = new ExternalBuildRunnerTransport(tools);
        var started = await tools.StartAsync(cap.Handle, "fake-target", ExternalBuildTestKit.Source(), "k1");

        var mcp = await transport.DispatchStatusAsync(
            new ExternalBuildRunnerTransport.RunnerCapabilities(true, false, false, true),
            cap.Handle, started.Id);
        Assert.Equal(RunnerTransportKind.McpTools, mcp.Kind);
        Assert.Null(mcp.TaskHandle);

        var tasks = await transport.DispatchStatusAsync(
            new ExternalBuildRunnerTransport.RunnerCapabilities(true, true, false, true),
            cap.Handle, started.Id);
        Assert.Equal(RunnerTransportKind.McpTasksCurrent, tasks.Kind);
        Assert.NotNull(tasks.TaskHandle);

        var legacy = await transport.DispatchStatusAsync(
            new ExternalBuildRunnerTransport.RunnerCapabilities(true, false, true, true),
            cap.Handle, started.Id);
        Assert.Equal(RunnerTransportKind.McpTasksLegacy, legacy.Kind);

        var cli = await transport.DispatchStatusAsync(
            new ExternalBuildRunnerTransport.RunnerCapabilities(false, false, false, true),
            cap.Handle, started.Id);
        Assert.Equal(RunnerTransportKind.CliBridge, cli.Kind);
        Assert.Contains(started.Id, cli.ResultSummary);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.DispatchStatusAsync(
                new ExternalBuildRunnerTransport.RunnerCapabilities(false, false, false, false),
                cap.Handle, started.Id));
    }
}
