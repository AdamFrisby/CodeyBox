using CodeyBox.Build.GitHubActions;
using CodeyBox.Core.ExternalBuilds;
using CodeyBox.Orchestrator.ExternalBuilds;

namespace CodeyBox.Tests.ExternalBuilds;

/// <summary>
/// GitHub Actions execution/evidence adapter coverage through the real
/// framework (service, lifecycle, artifact guard, evidence gate) with the
/// deterministic fake transport standing in for HTTP. Every test asserts
/// values the production adapter/provider paths produced — no mock-call
/// assertions, no live provider access.
/// </summary>
public sealed class GitHubActionsAdapterTests
{
    private const string TargetName = "gha-target";
    private const string TargetId = "unity-android";
    private const string Owner = "acme";
    private const string Repository = "game";
    private const string Workflow = ".github/workflows/build.yml";
    private const string HeadSha = "abcdef0123456789abcdef0123456789abcdef01";
    private const string OtherSha = "1111111111111111111111111111111111111111";
    private const string SourceDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BaseDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string CandidateRef = "refs/heads/codeybox-candidates/build-1";

    private sealed class Harness
    {
        public required FakeGitHubActionsTransport Transport { get; init; }
        public required InMemoryGitHubActionsBindingStore Bindings { get; init; }
        public required GitHubActionsBuildProvider Provider { get; init; }
        public required InMemoryExternalBuildStore Store { get; init; }
        public required ExternalBuildService Service { get; init; }
        public required ControllableClock Clock { get; init; }
        public required GitHubActionsExternalBuildOptions AdapterOptions { get; init; }
        public required ExternalBuildOptions FrameworkOptions { get; init; }
    }

    private static Harness Build(
        Action<FakeGitHubActionsTransport>? tuneTransport = null,
        Action<GitHubActionsWorkflowApproval>? tuneApproval = null)
    {
        var transport = new FakeGitHubActionsTransport();
        tuneTransport?.Invoke(transport);
        var bindings = new InMemoryGitHubActionsBindingStore();
        var clock = new ControllableClock(DateTimeOffset.UtcNow);
        var adapterOptions = new GitHubActionsExternalBuildOptions { Enabled = true };
        var approval = new GitHubActionsWorkflowApproval
        {
            Owner = Owner,
            Repository = Repository,
            WorkflowPath = Workflow,
            Toolchain = "unity-6000.0",
            Platform = "android",
            Configuration = "release",
        };
        tuneApproval?.Invoke(approval);
        adapterOptions.ApprovedWorkflows[TargetId] = approval;
        var frameworkOptions = new ExternalBuildOptions
        {
            Enabled = true,
            AllowedCandidateRefPrefixes = ["refs/candidates/", "refs/heads/codeybox-candidates/"],
        };
        frameworkOptions.ApprovedTargets[TargetName] = new ExternalBuildTargetApproval
        {
            ProviderId = GitHubActionsExternalBuildOptions.ProviderId,
            TargetId = TargetId,
            Configuration = "release",
            AllowGitPublication = true,
            AllowSnapshotUpload = false,
        };
        var provider = new GitHubActionsBuildProvider(
            transport, bindings, () => adapterOptions, () => frameworkOptions, clock);
        var store = new InMemoryExternalBuildStore();
        var service = new ExternalBuildService(store, [provider], () => frameworkOptions, clock);
        return new Harness
        {
            Transport = transport,
            Bindings = bindings,
            Provider = provider,
            Store = store,
            Service = service,
            Clock = clock,
            AdapterOptions = adapterOptions,
            FrameworkOptions = frameworkOptions,
        };
    }

    private static ExternalBuildStartRequest Request(string targetName = TargetName, string? candidateRef = CandidateRef) =>
        new()
        {
            ProjectId = "proj-gha",
            WorkItemId = "wi-1",
            Phase = "implement",
            Iteration = 3,
            Attempt = 1,
            ApprovedTargetName = targetName,
            IdempotencyKey = "gha-" + Guid.NewGuid().ToString("N"),
            Source = new ExternalBuildSourceIdentity
            {
                SourceDigestSha256 = SourceDigest,
                BaseDigestSha256 = BaseDigest,
                SnapshotId = "snap-1",
                ByteSize = 128,
                FileCount = 2,
                CandidateRef = candidateRef,
            },
        };

    private static ExternalBuildSubmitInput Input(string headSha = HeadSha, string? candidateRef = CandidateRef) =>
        new()
        {
            CandidateRef = candidateRef,
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GitHubActionsBuildProvider.HeadShaParameter] = headSha,
            },
        };

    private static async Task<ExternalBuildRecord> PollToTerminalAsync(Harness h, string buildId)
    {
        var record = await h.Store.GetAsync(buildId);
        Assert.NotNull(record);
        for (var i = 0; i < 8 && !ExternalBuildLifecycle.IsTerminal(record.State); i++)
            record = await h.Service.ReconcileAsync(buildId);
        return record;
    }

    [Fact]
    public void Adapter_IsDisabled_ByDefault()
    {
        var options = new GitHubActionsExternalBuildOptions();
        Assert.False(options.Enabled);
        Assert.Empty(options.ApprovedWorkflows);
        Assert.True(GitHubActionsExternalBuildOptions.IsValid(options));
    }

    [Fact]
    public async Task Submit_WhenDisabled_ThrowsNotEnabled()
    {
        var h = Build();
        h.AdapterOptions.Enabled = false;
        var record = new ExternalBuildRecord
        {
            Id = "xb-test",
            ProjectId = "p",
            WorkItemId = "w",
            Phase = "implement",
            Iteration = 1,
            Attempt = 1,
            State = ExternalBuildState.IntentRecorded,
            Target = new ExternalBuildTargetKey
            {
                ProviderId = GitHubActionsExternalBuildOptions.ProviderId,
                TargetId = TargetId,
                Configuration = "release",
            },
            Source = new ExternalBuildSourceIdentity
            {
                SourceDigestSha256 = SourceDigest,
                BaseDigestSha256 = BaseDigest,
                CandidateRef = CandidateRef,
            },
            ConfigDigest = "cfg",
            RequestId = "req-1",
        };
        await Assert.ThrowsAsync<ExternalBuildNotEnabledException>(
            () => h.Provider.SubmitAsync(record, Input(), CancellationToken.None));
    }

    [Fact]
    public async Task HappyPath_Succeeds_WithAuthoritativeEvidence()
    {
        var h = Build();
        var record = await h.Service.StartAsync(Request(), Input());
        Assert.Equal(ExternalBuildState.Queued, record.State);
        Assert.NotNull(record.ProviderRunId);

        record = await PollToTerminalAsync(h, record.Id);
        Assert.Equal(ExternalBuildState.Succeeded, record.State);
        Assert.NotNull(record.Evidence);
        Assert.Equal(ExternalBuildDimensionOutcome.Passed, record.Evidence.Compile);
        Assert.Equal(ExternalBuildDimensionOutcome.Passed, record.Evidence.Tests);
        Assert.Equal(ExternalBuildDimensionOutcome.Passed, record.Evidence.Package);
        Assert.True(record.Evidence.Authoritative);
        Assert.Equal(SourceDigest, record.Evidence.SourceDigestSha256);
        Assert.Equal(record.ProviderRunId, record.Evidence.ProviderRunId);
        Assert.Equal($"{Owner}/{Repository}/{Workflow}@unity-6000.0", record.Evidence.WorkflowIdentity);
        Assert.Equal(TargetId, record.Evidence.ApprovedTargetName);
        Assert.Equal("unity-6000.0", record.Evidence.Toolchain);
        Assert.Equal("android", record.Evidence.Platform);
        Assert.NotEmpty(record.Evidence.ArtifactDigests);

        var gate = ExternalBuildEvidenceGate.Check(record, SourceDigest, BaseDigest);
        Assert.True(gate.Passed, gate.Reason);

        // Duplicate polls replay the same terminal evidence without re-dispatch.
        var dispatches = h.Transport.DispatchCalls;
        record = await h.Service.ReconcileAsync(record.Id);
        Assert.Equal(ExternalBuildState.Succeeded, record.State);
        Assert.Equal(dispatches, h.Transport.DispatchCalls);
    }

    [Theory]
    [InlineData("unity-android", "unity-6000.0", "android", "release")]
    [InlineData("dotnet-linux", "dotnet-10.0", "linux-x64", "Release")]
    [InlineData("node-web", "node-22", "web", "production")]
    public async Task NeutralContract_HoldsAcrossToolchains(
        string targetId, string toolchain, string platform, string configuration)
    {
        var h = Build(tuneApproval: approval =>
        {
            approval.Toolchain = toolchain;
            approval.Platform = platform;
            approval.Configuration = configuration;
        });
        h.FrameworkOptions.ApprovedTargets["x-target"] = new ExternalBuildTargetApproval
        {
            ProviderId = GitHubActionsExternalBuildOptions.ProviderId,
            TargetId = targetId,
            Configuration = configuration,
            AllowGitPublication = true,
            AllowSnapshotUpload = false,
        };
        h.AdapterOptions.ApprovedWorkflows[targetId] = new GitHubActionsWorkflowApproval
        {
            Owner = Owner,
            Repository = Repository,
            WorkflowPath = Workflow,
            Toolchain = toolchain,
            Platform = platform,
            Configuration = configuration,
        };
        var request = Request("x-target");
        var record = await h.Service.StartAsync(request, Input());
        record = await PollToTerminalAsync(h, record.Id);
        Assert.Equal(ExternalBuildState.Succeeded, record.State);
        Assert.NotNull(record.Evidence);
        Assert.Equal(toolchain, record.Evidence.Toolchain);
        Assert.Equal(platform, record.Evidence.Platform);
        Assert.Equal(configuration, record.Evidence.Configuration);
    }

    [Fact]
    public async Task UncertainDispatch_ReconcilesWithoutDuplicate_ThenAdopts()
    {
        var h = Build(t => t.DispatchUncertain = true);
        var record = await h.Service.StartAsync(Request(), Input());
        Assert.Equal(ExternalBuildState.SubmitUncertain, record.State);
        Assert.Null(record.ProviderRunId);

        // The operator probes the exact correlated run out-of-band (this
        // durably binds run id to the request), then adopts that exact id
        // while the intent is still uncertain — before any service
        // reconcile could park it as blocked.
        var probe = await h.Provider.GetStatusAsync(
            "request:" + record.RequestId, CancellationToken.None);
        Assert.Equal(ExternalBuildExecutionPhase.Queued, probe.Phase);
        var runId = h.Transport.GetRunId(Owner, Repository, Workflow, record.RequestId);
        Assert.NotNull(runId);
        var adopted = await h.Service.AdoptAsync(record.Id, runId);
        Assert.Equal(runId, adopted.ProviderRunId);

        // Restart with a fresh service over the same durable store/bindings:
        // polling resumes through the new instance, never redispatches.
        var service2 = new ExternalBuildService(
            h.Store, [h.Provider], () => h.FrameworkOptions, h.Clock);
        var terminal = await h.Store.GetAsync(record.Id);
        Assert.NotNull(terminal);
        for (var i = 0; i < 8 && !ExternalBuildLifecycle.IsTerminal(terminal.State); i++)
            terminal = await service2.ReconcileAsync(record.Id);
        Assert.Equal(ExternalBuildState.Succeeded, terminal.State);
        Assert.Equal(1, h.Transport.DispatchCalls);
    }

    [Fact]
    public async Task CheckoutMismatch_FailsClosed()
    {
        var h = Build();
        var record = await h.Service.StartAsync(Request(), Input());
        h.Transport.SetHeadSha(record.RequestId, OtherSha);
        record = await PollToTerminalAsync(h, record.Id);
        Assert.Equal(ExternalBuildState.Failed, record.State);
        Assert.Null(record.Evidence);
        Assert.Contains("checkout mismatch", record.FailureDetail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkflowSubstitution_IsRejected()
    {
        var h = Build();
        var record = await h.Service.StartAsync(Request(), Input());
        h.Transport.SpoofRun(record.RequestId, workflowPath: ".github/workflows/other.yml");
        record = await PollToTerminalAsync(h, record.Id);
        Assert.Equal(ExternalBuildState.Failed, record.State);
        Assert.Contains("substitution rejected", record.FailureDetail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForkSubstitution_IsRejected()
    {
        var h = Build();
        var record = await h.Service.StartAsync(Request(), Input());
        h.Transport.SpoofRun(record.RequestId, fork: true);
        record = await PollToTerminalAsync(h, record.Id);
        Assert.Equal(ExternalBuildState.Failed, record.State);
        Assert.Contains("substitution rejected", record.FailureDetail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rerun_EvaluatesLatestAttempt_NotCachedTerminal()
    {
        var h = Build();
        var record = await h.Service.StartAsync(Request(), Input());
        record = await PollToTerminalAsync(h, record.Id);
        Assert.Equal(ExternalBuildState.Succeeded, record.State);

        h.Transport.Rerun(record.RequestId);
        h.Transport.Complete(record.RequestId, GitHubActionsConclusions.Failure);
        var status = await h.Provider.GetStatusAsync(
            record.ProviderRunId!, CancellationToken.None);
        Assert.Equal(ExternalBuildExecutionPhase.Failed, status.Phase);
        Assert.Contains("not authoritative success", status.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("timed_out")]
    [InlineData("action_required")]
    [InlineData("stale")]
    [InlineData("skipped")]
    [InlineData("neutral")]
    public async Task NonSuccessConclusions_NeverCountAsPass(string conclusion)
    {
        var h = Build();
        var record = await h.Service.StartAsync(Request(), Input());
        h.Transport.ConclusionOverrides[record.RequestId] = conclusion;
        record = await PollToTerminalAsync(h, record.Id);
        Assert.Equal(ExternalBuildState.Failed, record.State);
        Assert.Null(record.Evidence);
        Assert.Contains("not authoritative success", record.FailureDetail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObsoleteAttempt_DoesNotReplayAsCurrent()
    {
        var h = Build();
        var record = await h.Service.StartAsync(Request(), Input());
        // Terminal at attempt 1, then a rerun starts attempt 2.
        record = await PollToTerminalAsync(h, record.Id);
        Assert.Equal(ExternalBuildState.Succeeded, record.State);
        h.Transport.Rerun(record.RequestId);
        var running = await h.Provider.GetStatusAsync(
            record.ProviderRunId!, CancellationToken.None);
        Assert.True(
            running.Phase is ExternalBuildExecutionPhase.Running or ExternalBuildExecutionPhase.Queued,
            "rerun must resume polling, not replay the old terminal");
        // A stale duplicate reporting attempt 1 must not resolve attempt 2.
        h.Transport.Complete(record.RequestId, GitHubActionsConclusions.Success);
        h.Transport.SetAttempt(record.RequestId, 1);
        var stale = await h.Provider.GetStatusAsync(
            record.ProviderRunId!, CancellationToken.None);
        Assert.True(
            stale.Phase is ExternalBuildExecutionPhase.Running or ExternalBuildExecutionPhase.Queued,
            "obsolete attempt must wait for latest, not replay");
        Assert.Contains("obsolete", stale.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingTestReport_FailsClosed()
    {
        var h = Build(t => t.ReportFactory = _ => null);
        var record = await h.Service.StartAsync(Request(), Input());
        record = await PollToTerminalAsync(h, record.Id);
        Assert.Equal(ExternalBuildState.Failed, record.State);
        Assert.Null(record.Evidence);
        Assert.Contains("does not imply tests ran", record.FailureDetail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailingTestReport_FailsClosed()
    {
        var h = Build(t => t.ReportFactory = _ => new GitHubActionsTestReport(39, 2, 0, "fake-harness"));
        var record = await h.Service.StartAsync(Request(), Input());
        record = await PollToTerminalAsync(h, record.Id);
        Assert.Equal(ExternalBuildState.Failed, record.State);
        Assert.Contains("tests failed", record.FailureDetail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptReportArchive_FailsClosed()
    {
        var h = Build(t => t.CorruptReportArchive = true);
        var record = await h.Service.StartAsync(Request(), Input());
        record = await PollToTerminalAsync(h, record.Id);
        Assert.Equal(ExternalBuildState.Failed, record.State);
        Assert.Contains("rejected", record.FailureDetail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizeReport_FailsClosed()
    {
        var h = Build(t => t.OversizeReport = true);
        var record = await h.Service.StartAsync(Request(), Input());
        record = await PollToTerminalAsync(h, record.Id);
        Assert.Equal(ExternalBuildState.Failed, record.State);
        Assert.Contains("cap", record.FailureDetail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredReportArtifact_FailsClosed()
    {
        var h = Build(t => t.ReportArtifactExpired = true);
        var record = await h.Service.StartAsync(Request(), Input());
        record = await PollToTerminalAsync(h, record.Id);
        Assert.Equal(ExternalBuildState.Failed, record.State);
        Assert.Contains("expired", record.FailureDetail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportDownloadAuthFailure_IsTyped()
    {
        var h = Build(t =>
        {
            t.PollsToCompleted = 0;
            t.ReportDownloadAuthFailure = true;
        });
        var record = await h.Service.StartAsync(Request(), Input());
        await Assert.ThrowsAsync<GitHubActionsAuthException>(
            () => h.Provider.GetStatusAsync(record.ProviderRunId!, CancellationToken.None));
    }

    [Fact]
    public async Task SkippedCompileJob_FailsClosed()
    {
        var h = Build(t => t.JobsFactory = _ =>
        [
            new GitHubActionsJob(1, "build", "completed", "skipped"),
            new GitHubActionsJob(2, "test", "completed", "success"),
        ]);
        var record = await h.Service.StartAsync(Request(), Input());
        record = await PollToTerminalAsync(h, record.Id);
        Assert.Equal(ExternalBuildState.Failed, record.State);
        Assert.Contains("skipped", record.FailureDetail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessWithoutRequiredReport_NeverImpliesTestsPassed()
    {
        var h = Build(t =>
        {
            t.ReportFactory = _ => null;
            t.JobsFactory = _ =>
            [
                new GitHubActionsJob(1, "build", "completed", "success"),
            ];
        });
        var record = await h.Service.StartAsync(Request(), Input());
        record = await PollToTerminalAsync(h, record.Id);
        Assert.Equal(ExternalBuildState.Failed, record.State);
    }

    [Fact]
    public async Task RateLimitedPoll_IsTypedWithRetryAfter()
    {
        var h = Build(t =>
        {
            t.FailNextPollRateLimited = true;
            t.RateLimitedRetryAfter = TimeSpan.FromSeconds(45);
        });
        var record = await h.Service.StartAsync(Request(), Input());
        var ex = await Assert.ThrowsAsync<ExternalBuildRateLimitedException>(
            () => h.Provider.GetStatusAsync(record.ProviderRunId!, CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(45), ex.RetryAfter);
    }

    [Fact]
    public async Task RateLimitedDispatch_RetriesWithoutDuplicate()
    {
        var h = Build(t => t.FailNextDispatchRateLimited = true);
        await Assert.ThrowsAsync<ExternalBuildRateLimitedException>(
            () => h.Service.StartAsync(Request(), Input()));
        // The throttled attempt left no run behind: a fresh idempotency key
        // dispatches exactly once more and runs to terminal.
        var record = await h.Service.StartAsync(Request(), Input());
        Assert.Equal(ExternalBuildState.Queued, record.State);
        Assert.Equal(2, h.Transport.DispatchCalls);
        record = await PollToTerminalAsync(h, record.Id);
        Assert.Equal(ExternalBuildState.Succeeded, record.State);
    }

    [Fact]
    public async Task CancelMidFlight_ConfirmsCancellation()
    {
        var h = Build();
        var record = await h.Service.StartAsync(Request(), Input());
        record = await h.Service.CancelAsync(record.Id, ExternalBuildTerminalCause.UserCancelled);
        Assert.Equal(ExternalBuildState.Cancelled, record.State);
        Assert.Equal(ExternalBuildTerminalCause.ProviderConfirmedCancellation, record.TerminalCause);
    }

    [Fact]
    public async Task LateCompletion_WinsOverCancel()
    {
        var h = Build();
        var record = await h.Service.StartAsync(Request(), Input());
        record = await PollToTerminalAsync(h, record.Id);
        Assert.Equal(ExternalBuildState.Succeeded, record.State);
        var cancel = await h.Provider.CancelAsync(
            record.ProviderRunId!, CancellationToken.None);
        Assert.False(cancel.Confirmed);
        Assert.Contains("already completed", cancel.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallbackClaim_IsReconciledAgainstAuthoritativeState()
    {
        var h = Build();
        var record = await h.Service.StartAsync(Request(), Input());
        // A duplicate/late success callback for a queued build is a wakeup:
        // the service reconciles and does not invent evidence.
        var callbackError = await h.Service.HandleCallbackAsync(
            new ExternalBuildCallback(
                GitHubActionsExternalBuildOptions.ProviderId, record.ProviderRunId!,
                record.Id, ExternalBuildExecutionPhase.Succeeded,
                h.Clock.GetUtcNow(), Signature: null),
            _ => true,
            CancellationToken.None);
        Assert.Null(callbackError);
        var after = await h.Store.GetAsync(record.Id);
        Assert.NotNull(after);
        if (!ExternalBuildLifecycle.IsTerminal(after.State))
        {
            var terminal = await PollToTerminalAsync(h, record.Id);
            Assert.Equal(ExternalBuildState.Succeeded, terminal.State);
        }
    }

    [Fact]
    public async Task ParkedOwner_ResumesAndDeliversExactlyOnce()
    {
        var h = Build();
        var record = await h.Service.StartAsync(Request(), Input());
        var history = new InMemoryExternalBuildHistory();
        var coordinator = new ExternalBuildParkCoordinator(
            h.Service, h.Store, history, () => h.FrameworkOptions, h.Clock);
        var parked = await coordinator.MaybeParkAsync(
            record.Id, TimeSpan.FromMinutes(11), coldCache: true);
        Assert.NotNull(parked);

        var terminal = await PollToTerminalAsync(h, record.Id);
        Assert.Equal(ExternalBuildState.Succeeded, terminal.State);
        var resumes = new List<string>();
        var delivered = await coordinator.DeliverCompletionsAsync(
            (rec, checkpoint, ct) =>
            {
                resumes.Add(rec.Id + "@" + rec.Attempt);
                return Task.CompletedTask;
            });
        Assert.Contains(terminal.Id, delivered);
        Assert.Single(resumes);
        // Redelivery is suppressed: durable completion wins.
        var redelivered = await coordinator.DeliverCompletionsAsync(
            (rec, checkpoint, ct) => Task.CompletedTask);
        Assert.Empty(redelivered);
    }

    [Fact]
    public async Task ArtifactsFlow_ThroughFrameworkGuards()
    {
        var h = Build();
        var record = await h.Service.StartAsync(Request(), Input());
        record = await PollToTerminalAsync(h, record.Id);
        Assert.Equal(ExternalBuildState.Succeeded, record.State);

        var refs = await h.Provider.ListArtifactsAsync(
            record.ProviderRunId!, CancellationToken.None);
        Assert.Contains(refs, r => r.Name == FakeGitHubActionsTransport.PackageArtifactName);
        var package = refs.First(r => r.Name == FakeGitHubActionsTransport.PackageArtifactName);
        Assert.Equal(64, package.ContentDigestSha256.Length);

        var payload = await h.Provider.ReadArtifactAsync(
            record.ProviderRunId!, package.Name, CancellationToken.None);
        Assert.Equal(package.ContentDigestSha256, payload.ContentDigestSha256);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Provider.ReadArtifactAsync(
                record.ProviderRunId!, "nope.zip", CancellationToken.None));
    }

    [Fact]
    public void OptionsValidation_RejectsBadApprovals()
    {
        Assert.False(GitHubActionsExternalBuildOptions.IsValid(null));
        Assert.False(GitHubActionsExternalBuildOptions.IsValid(new GitHubActionsExternalBuildOptions
        {
            Enabled = true,
            ApiBaseUrl = "http://insecure.example",
        }));
        Assert.False(GitHubActionsExternalBuildOptions.IsValid(new GitHubActionsExternalBuildOptions
        {
            Enabled = true,
            ApprovedWorkflows =
            {
                ["x"] = new GitHubActionsWorkflowApproval
                {
                    Owner = "",
                    Repository = Repository,
                    WorkflowPath = Workflow,
                    Toolchain = "t",
                    Platform = "p",
                },
            },
        }));
        Assert.False(GitHubActionsExternalBuildOptions.IsValid(new GitHubActionsExternalBuildOptions
        {
            Enabled = true,
            ApprovedWorkflows =
            {
                ["x"] = new GitHubActionsWorkflowApproval
                {
                    Owner = Owner,
                    Repository = Repository,
                    WorkflowPath = ".github/workflows/evil.sh",
                    Toolchain = "t",
                    Platform = "p",
                },
            },
        }));
        Assert.False(GitHubActionsWorkflowApproval.IsValid(new GitHubActionsWorkflowApproval
        {
            Owner = Owner,
            Repository = Repository,
            WorkflowPath = Workflow,
            Toolchain = "t",
            Platform = "p",
            CompileJobNames = [],
        }));
    }

    [Fact]
    public void RefPolicy_ScopesPublicationAndCleanup()
    {
        const string prefix = "refs/heads/codeybox-candidates/";
        Assert.Null(GitHubActionsCandidateRefPolicy.ValidateForDispatch(CandidateRef, prefix));
        Assert.NotNull(GitHubActionsCandidateRefPolicy.ValidateForDispatch("refs/heads/main", prefix));
        Assert.NotNull(GitHubActionsCandidateRefPolicy.ValidateForDispatch("refs/candidates/x", prefix));
        Assert.NotNull(GitHubActionsCandidateRefPolicy.ValidateForDispatch(null, prefix));
        Assert.Equal("codeybox-candidates/build-1", GitHubActionsCandidateRefPolicy.DeriveDispatchRef(CandidateRef));

        var now = DateTimeOffset.UtcNow;
        Assert.True(GitHubActionsCandidateRefPolicy.CanDelete(
            CandidateRef, prefix, CandidateRef, HeadSha, HeadSha,
            now.AddDays(-2), now, TimeSpan.FromDays(1), buildTerminal: false));
        Assert.False(GitHubActionsCandidateRefPolicy.CanDelete(
            CandidateRef, prefix, CandidateRef, HeadSha, HeadSha,
            now, now, TimeSpan.FromDays(1), buildTerminal: false));
        Assert.True(GitHubActionsCandidateRefPolicy.CanDelete(
            CandidateRef, prefix, CandidateRef, HeadSha, HeadSha,
            now, now, TimeSpan.FromDays(1), buildTerminal: true));
        Assert.False(GitHubActionsCandidateRefPolicy.CanDelete(
            "refs/heads/main", prefix, "refs/heads/main", HeadSha, HeadSha,
            now.AddDays(-2), now, TimeSpan.Zero, buildTerminal: true));
        Assert.False(GitHubActionsCandidateRefPolicy.CanDelete(
            CandidateRef, prefix, CandidateRef, HeadSha, OtherSha,
            now.AddDays(-2), now, TimeSpan.Zero, buildTerminal: true));
    }

    [Fact]
    public void Mapper_ParsesAndRejectsReports()
    {
        var good = GitHubActionsEvidenceMapper.ParseReport(
            System.Text.Encoding.UTF8.GetBytes(
                """{"version":1,"framework":"dotnet-test","passed":10,"failed":0,"skipped":1}"""),
            1024);
        Assert.Equal(10, good.Passed);
        Assert.Throws<GitHubActionsEvidenceUnavailableException>(
            () => GitHubActionsEvidenceMapper.ParseReport([], 1024));
        Assert.Throws<GitHubActionsEvidenceUnavailableException>(
            () => GitHubActionsEvidenceMapper.ParseReport(
                System.Text.Encoding.UTF8.GetBytes("not json"), 1024));
        Assert.Throws<GitHubActionsEvidenceUnavailableException>(
            () => GitHubActionsEvidenceMapper.ParseReport(
                System.Text.Encoding.UTF8.GetBytes(
                    """{"version":1,"framework":"x","passed":-1,"failed":0,"skipped":0}"""),
                1024));
        Assert.Throws<GitHubActionsEvidenceUnavailableException>(
            () => GitHubActionsEvidenceMapper.ParseReport(new byte[2048], 1024));
    }
}
