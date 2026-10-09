using CodeyBox.Core.ExternalBuilds;
using CodeyBox.Orchestrator.ExternalBuilds;

namespace CodeyBox.Tests.ExternalBuilds;

/// <summary>Evidence gate, artifact guard, budgets, cancel races, callbacks.</summary>
public sealed class ExternalBuildEvidenceAndBudgetTests
{
    private static ExternalBuildRecord BuildRecord(
        string sourceDigest, string baseDigest, string? runId = "fake-snapshot-run-1",
        ExternalBuildState state = ExternalBuildState.Succeeded) => new()
        {
            Id = "xb-test",
            ProjectId = "proj",
            WorkItemId = "w1",
            Phase = "work",
            Iteration = 1,
            Attempt = 1,
            State = state,
            Target = new ExternalBuildTargetKey
            {
                ProviderId = "fake-snapshot", TargetId = "fake-target", Configuration = "release",
            },
            Source = new ExternalBuildSourceIdentity
            {
                SourceDigestSha256 = sourceDigest, BaseDigestSha256 = baseDigest,
            },
            ConfigDigest = "cfg",
            ProviderRunId = runId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static ExternalBuildEvidence Evidence(string source, string run, bool auth = true) => new()
    {
        Compile = ExternalBuildDimensionOutcome.Passed,
        Tests = ExternalBuildDimensionOutcome.Passed,
        Package = ExternalBuildDimensionOutcome.Passed,
        SourceDigestSha256 = source,
        ProviderRunId = run,
        WorkflowIdentity = "fake-workflow@pinned",
        ApprovedTargetName = "fake-target",
        Configuration = "release",
        ArtifactDigests = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["package.zip"] = new string('c', 64),
        },
        Authoritative = auth,
        CapturedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Gate_Passes_WithExactMergeTreeEvidence()
    {
        var source = new string('a', 64);
        var record = BuildRecord(source, new string('b', 64)) with { Evidence = Evidence(source, "fake-snapshot-run-1") };
        var gate = ExternalBuildEvidenceGate.Check(record, source, new string('b', 64));
        Assert.True(gate.Passed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Gate_Rejects_MalformedMissingStale(int variant)
    {
        var source = new string('a', 64);
        ExternalBuildEvidence? evidence = variant switch
        {
            0 => null,
            1 => Evidence(source, "fake-snapshot-run-1", auth: false),
            _ => Evidence(source, "fake-snapshot-run-1") with { Tests = ExternalBuildDimensionOutcome.NotRun },
        };
        var record = BuildRecord(source, new string('b', 64)) with { Evidence = evidence };
        Assert.False(ExternalBuildEvidenceGate.Check(record, source, new string('b', 64)).Passed);
    }

    [Fact]
    public void Gate_Rejects_CompilationMasqueradingAsTests()
    {
        var source = new string('a', 64);
        var evidence = Evidence(source, "fake-snapshot-run-1") with
        {
            Tests = ExternalBuildDimensionOutcome.Unknown,
        };
        var record = BuildRecord(source, new string('b', 64)) with { Evidence = evidence };
        Assert.False(ExternalBuildEvidenceGate.Check(record, source, new string('b', 64)).Passed);
    }

    [Fact]
    public void Gate_Rejects_WrongMergeTree_WrongBase_WrongRun()
    {
        var source = new string('a', 64);
        var record = BuildRecord(source, new string('b', 64)) with { Evidence = Evidence(source, "fake-snapshot-run-1") };
        Assert.False(ExternalBuildEvidenceGate.Check(record, new string('d', 64), new string('b', 64)).Passed);
        Assert.False(ExternalBuildEvidenceGate.Check(record, source, new string('e', 64)).Passed);
        var otherRun = record with { ProviderRunId = "other-run" };
        Assert.False(ExternalBuildEvidenceGate.Check(otherRun, source, new string('b', 64)).Passed);
    }

    [Fact]
    public void Gate_Blocked_WhileEvidenceOutstanding()
    {
        var running = BuildRecord(new string('a', 64), new string('b', 64), state: ExternalBuildState.Running);
        Assert.False(ExternalBuildEvidenceGate.Check(running, new string('a', 64), new string('b', 64)).Passed);
        Assert.True(ExternalBuildParkCoordinator.IsEvidenceOutstanding(running));
    }

    [Fact]
    public void Exploratory_Reused_OnlyOnExactMatch()
    {
        var source = new string('a', 64);
        var build = BuildRecord(source, new string('b', 64));
        Assert.True(ExternalBuildEvidenceEvaluator.IsExploratoryReusable(Evidence(source, "r"), build));
        Assert.False(ExternalBuildEvidenceEvaluator.IsExploratoryReusable(
            Evidence(new string('f', 64), "r"), build));
        Assert.False(ExternalBuildEvidenceEvaluator.IsExploratoryReusable(
            Evidence(source, "r", auth: false), build));
    }

    [Fact]
    public void ArtifactGuard_RejectsTraversalSymlinkDigestAndSize()
    {
        var opts = new ExternalBuildOptions();
        var traversal = new ExternalBuildArtifactRef("../evil.sh", 10, new string('a', 64), "text/plain");
        Assert.NotNull(ExternalBuildArtifactGuard.ValidateRef(traversal, opts));
        var big = new ExternalBuildArtifactRef("ok.zip", opts.MaxArtifactBytes + 1, new string('a', 64), "application/zip");
        Assert.NotNull(ExternalBuildArtifactGuard.ValidateRef(big, opts));
        var entries = new List<(string, long, long, bool)> { ("link", 10, 10, true) };
        Assert.NotNull(ExternalBuildArtifactGuard.ValidateArchiveEntries(entries, opts));
        var escape = new List<(string, long, long, bool)> { ("../../x", 10, 10, false) };
        Assert.NotNull(ExternalBuildArtifactGuard.ValidateArchiveEntries(escape, opts));
        var bomb = new List<(string, long, long, bool)> { ("f", 10, 10 * 100 + 1, false) };
        Assert.NotNull(ExternalBuildArtifactGuard.ValidateArchiveEntries(bomb, opts));
        Assert.NotNull(ExternalBuildArtifactGuard.ValidateArtifactUrl("http://insecure/x", ["h"]));
        Assert.NotNull(ExternalBuildArtifactGuard.ValidateArtifactUrl("https://evil.example/x", ["good.example"]));
        Assert.Null(ExternalBuildArtifactGuard.ValidateArtifactUrl("https://good.example/x", ["good.example"]));
        var content = new byte[] { 1, 2, 3 };
        var payload = new ExternalBuildArtifactPayload("ok", content, "application/octet-stream", new string('0', 64));
        Assert.NotNull(ExternalBuildArtifactGuard.ValidatePayload(payload, opts));
    }

    [Fact]
    public async Task Budgets_Enforced_AndCleanup_RetainsActive()
    {
        var provider = new FakeSnapshotBuildProvider();
        var (service, store, clock, _) = ExternalBuildTestKit.BuildService(
            provider, o => { o.MaxQueuedPerProject = 1; o.RetentionDays = 30; });
        await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        await Assert.ThrowsAsync<ExternalBuildBudgetExceededException>(() =>
            service.StartAsync(ExternalBuildTestKit.Request(attempt: "w2"), new ExternalBuildSubmitInput()));
        clock.Advance(TimeSpan.FromDays(60));
        await service.CleanupAsync();
        Assert.Equal(1, await store.CountActiveAsync("proj"));
    }

    [Fact]
    public async Task Cancel_Races_ResolveTruthfully()
    {
        var provider = new FakeSnapshotBuildProvider { PollsToTerminal = 1 };
        var (service, _, _, _) = ExternalBuildTestKit.BuildService(provider);
        var record = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        var terminal = await service.ReconcileAsync(record.Id);
        Assert.Equal(ExternalBuildState.Succeeded, terminal.State);
        var cancelAfter = await service.CancelAsync(record.Id, ExternalBuildTerminalCause.UserCancelled);
        Assert.Equal(ExternalBuildState.Succeeded, cancelAfter.State);

        var record2 = await service.StartAsync(
            ExternalBuildTestKit.Request(attempt: "w2"), new ExternalBuildSubmitInput());
        var userCancel = await service.CancelAsync(record2.Id, ExternalBuildTerminalCause.UserCancelled);
        Assert.Equal(ExternalBuildState.Cancelled, userCancel.State);
        Assert.True(provider.WasCancelled(userCancel.ProviderRunId!));
    }

    [Fact]
    public async Task Callback_Validated_ThenReconciled()
    {
        var provider = new FakeSnapshotBuildProvider { PollsToTerminal = 1 };
        var (service, _, clock, _) = ExternalBuildTestKit.BuildService(provider);
        var record = await service.StartAsync(ExternalBuildTestKit.Request(), new ExternalBuildSubmitInput());
        var callback = new ExternalBuildCallback(
            "fake-snapshot", record.ProviderRunId!, record.Id,
            ExternalBuildExecutionPhase.Succeeded, clock.GetUtcNow(), "sig");
        var error = await service.HandleCallbackAsync(callback, _ => true);
        Assert.Null(error);
        var stale = callback with { SentAt = clock.GetUtcNow().AddHours(-5) };
        Assert.NotNull(await service.HandleCallbackAsync(stale, _ => true));
        Assert.NotNull(await service.HandleCallbackAsync(callback, _ => false));
        var unknown = callback with { BuildId = "xb-missing" };
        Assert.NotNull(await service.HandleCallbackAsync(unknown, _ => true));
    }

    [Fact]
    public void OptionsValidation_RejectsBadTargets()
    {
        var opts = ExternalBuildTestKit.Options();
        Assert.Null(ExternalBuildOptionsValidation.Validate(opts));
        opts.ApprovedTargets["bad"] = new ExternalBuildTargetApproval();
        Assert.NotNull(ExternalBuildOptionsValidation.Validate(opts));
    }
}
