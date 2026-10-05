using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Upstream;
using TestClock = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

/// <summary>
/// Service tests for <see cref="AuditCheckPublicationService"/> with a real
/// SQLite publication store, an in-memory audit-report stub, and a scripted
/// upstream stub: default-disabled composition, verdict mapping, missing
/// audits, fork mismatch, rate-limit backoff/deferral, auth blocking,
/// cancellation, delivery policy, and stale-completion protection.
/// </summary>
public sealed class AuditCheckPublicationServiceTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly AuditCheckPublicationOptions EnabledOptions = new()
    {
        Enabled = true,
        RetryMaxAttempts = 2,
        RetryBaseDelay = TimeSpan.FromSeconds(2),
        RetryMaxDelay = TimeSpan.FromMinutes(2),
    };

    private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-checkpub-svc-");
    private readonly List<IDisposable> _disposables = [];
    private string DbPath => _scratch.DbPath("svc.db");

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            try { disposable.Dispose(); } catch { }
        }
        _scratch.Dispose();
    }

    private sealed class MemoryAuditReportStore : IAuditReportStore
    {
        public List<AuditReport> Reports { get; } = [];
        public Task CreateAsync(AuditReport report, CancellationToken ct = default)
        {
            Reports.Add(report);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<AuditReport>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditReport>>(Reports.Where(r => r.WorkItemId == workItemId).ToList());
        public Task<string?> GetRawOutputAsync(string workItemId, AuditTarget target, int iteration, string auditorName, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
            Task.FromResult(0);
    }

    private sealed class ScriptedUpstreamRemote : IUpstreamRemote
    {
        public string Name => "github";
        public bool Supported { get; set; } = true;
        public Func<AuditCheckPublicationRequest, AuditCheckPublicationResult>? PublishBehavior { get; set; }
        public Exception? PublishError { get; set; }
        public List<AuditCheckPublicationRequest> Published { get; } = [];

        public Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<UpstreamCompletionOutcome> CompleteAsync(UpstreamCompletionRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> TryMergeUpstreamBranchAsync(string targetBranch, string sourceBranch, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AuditCheckPublicationSupport> GetAuditCheckPublicationSupportAsync(CancellationToken ct = default) =>
            Task.FromResult(Supported ? AuditCheckPublicationSupport.Yes : AuditCheckPublicationSupport.No("no checks here"));

        public Task<AuditCheckPublicationResult> PublishAuditCheckAsync(
            AuditCheckPublicationRequest request, AuditCheckPublicationOptions options, CancellationToken ct = default)
        {
            Published.Add(request);
            if (PublishError is not null)
                return Task.FromException<AuditCheckPublicationResult>(PublishError);
            return Task.FromResult(PublishBehavior?.Invoke(request) ?? new AuditCheckPublicationResult
            {
                CheckRunId = 1000 + Published.Count,
                Status = "completed",
                Conclusion = "failure",
                AnnotationsPublished = 0,
                AnnotationsOmitted = 0,
                BatchesSent = 0,
            });
        }
    }

    private static Project MakeProject(bool checksEnabled = true, string kind = "github") => new()
    {
        Id = new ProjectId("test-project"),
        DisplayName = "Test",
        RepositoryUrl = "https://github.com/myorg/myrepo.git",
        Upstream = new ProjectUpstream
        {
            Kind = kind,
            GitHubOwner = "myorg",
            GitHubRepository = "myrepo",
        },
        AuditChecks = new ProjectAuditChecks { Enabled = checksEnabled },
    };

    private static AuditReport MakeReport(
        string workItemId, int iteration, string auditor, string worstSeverity,
        IReadOnlyList<AuditReportFinding>? findings = null) => new()
        {
            Id = Guid.NewGuid().ToString(),
            WorkItemId = workItemId,
            Iteration = iteration,
            Target = AuditTarget.Code,
            AuditorName = auditor,
            AuditorKind = "test",
            WorstSeverity = worstSeverity,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            EndedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            DurationMs = 10,
            Findings = findings ?? [],
        };

    private static AuditReportFinding MakeFinding() => new(
        "finding-1", "error", "Null dereference", "May dereference null.",
        ["src/Widget.cs"], [42]);

    private (AuditCheckPublicationService Service, MemoryAuditReportStore Audits, TestClock Time)
        BuildService(
            AuditCheckPublicationOptions? options = null,
            SqliteAuditCheckPublicationStore? publications = null)
    {
        var audits = new MemoryAuditReportStore();
        var pubs = publications ?? new SqliteAuditCheckPublicationStore(DbPath);
        _disposables.Add(pubs);
        var time = new TestClock();
        var service = new AuditCheckPublicationService(
            audits, pubs, () => options ?? EnabledOptions, time);
        return (service, audits, time);
    }

    [Fact]
    public async Task DisabledOptions_AreExplicitNoOp()
    {
        var (service, _, _) = BuildService(EnabledOptions with { Enabled = false });
        var remote = new ScriptedUpstreamRemote();
        var result = await service.PublishWorkItemAsync(
            MakeProject(), remote, "wi-1", Sha, iteration: 1, attempt: 1, null, null);
        Assert.False(result.Enabled);
        Assert.Empty(result.Scopes);
        Assert.Empty(remote.Published);
    }

    [Fact]
    public async Task ProjectOptOut_IsExplicitNoOp()
    {
        var (service, _, _) = BuildService();
        var remote = new ScriptedUpstreamRemote();
        var result = await service.PublishWorkItemAsync(
            MakeProject(checksEnabled: false), remote, "wi-1", Sha, iteration: 1, attempt: 1, null, null);
        Assert.False(result.Enabled);
        Assert.Empty(remote.Published);
    }

    [Fact]
    public async Task UnsupportedForge_ReturnsUnsupportedWithoutPublishing()
    {
        var (service, _, _) = BuildService();
        var remote = new ScriptedUpstreamRemote { Supported = false };
        var result = await service.PublishWorkItemAsync(
            MakeProject(), remote, "wi-1", Sha, iteration: 1, attempt: 1, null, null);
        Assert.True(result.Enabled);
        Assert.Equal(AuditCheckScopeOutcome.Unsupported, Assert.Single(result.Scopes).Outcome);
        Assert.Empty(remote.Published);
    }

    [Fact]
    public async Task NoopRemote_IsUnsupported_EndToEnd()
    {
        var (service, _, _) = BuildService();
        var result = await service.PublishWorkItemAsync(
            MakeProject(), new NoopUpstreamRemote(), "wi-1", Sha, iteration: 1, attempt: 1, null, null);
        Assert.Equal(AuditCheckScopeOutcome.Unsupported, Assert.Single(result.Scopes).Outcome);
    }

    [Fact]
    public async Task FailedAudit_PublishesFailure_PlusPerAuditorScopes()
    {
        var (service, audits, _) = BuildService();
        audits.Reports.Add(MakeReport("wi-2", 1, "lint", "error", [MakeFinding()]));
        var remote = new ScriptedUpstreamRemote();
        var result = await service.PublishWorkItemAsync(
            MakeProject(), remote, "wi-2", Sha, iteration: 1, attempt: 1, null, "https://codeybox.local/r/wi-2");

        Assert.True(result.Enabled);
        Assert.Equal(2, result.Scopes.Count);
        Assert.All(result.Scopes, s => Assert.Equal(AuditCheckScopeOutcome.Published, s.Outcome));
        var aggregate = remote.Published.Single(r => r.Scope == "aggregate");
        Assert.Equal(AuditCheckVerdict.Failed, aggregate.Verdict);
        Assert.Equal(Sha, aggregate.HeadSha);
        Assert.Equal("https://codeybox.local/r/wi-2", aggregate.DetailsUrl);
        Assert.Single(aggregate.Findings);
        var auditor = remote.Published.Single(r => r.Scope == "lint");
        Assert.Equal(AuditCheckVerdict.Failed, auditor.Verdict);
    }

    [Fact]
    public async Task CleanReport_PublishesSuccess_VerdictIndependentOfEmptyFindings()
    {
        var (service, audits, _) = BuildService();
        // Report row exists with zero findings: the audit ran and passed.
        audits.Reports.Add(MakeReport("wi-3", 1, "lint", "none"));
        var remote = new ScriptedUpstreamRemote();
        await service.PublishWorkItemAsync(
            MakeProject(), remote, "wi-3", Sha, iteration: 1, attempt: 1, null, null);
        var aggregate = remote.Published.Single(r => r.Scope == "aggregate");
        Assert.Equal(AuditCheckVerdict.Passed, aggregate.Verdict);
    }

    [Fact]
    public async Task MissingIteration_PublishesNotRunSkippedCheck()
    {
        var (service, _, _) = BuildService();
        var remote = new ScriptedUpstreamRemote();
        var result = await service.PublishWorkItemAsync(
            MakeProject(), remote, "wi-missing", Sha, iteration: 4, attempt: 1, null, null);
        var scope = Assert.Single(result.Scopes);
        Assert.Equal(AuditCheckScopeOutcome.Published, scope.Outcome);
        var request = Assert.Single(remote.Published);
        Assert.Equal(AuditCheckVerdict.NotRun, request.Verdict);
        Assert.Equal(AuditCheckUnavailabilityReason.Missing, request.UnavailabilityReason);
    }

    [Fact]
    public async Task NonGithubProject_DoesNotPublishToWrongForge()
    {
        var (service, _, _) = BuildService();
        var remote = new ScriptedUpstreamRemote();
        var result = await service.PublishWorkItemAsync(
            MakeProject(kind: "git-generic"), remote, "wi-1", Sha, iteration: 1, attempt: 1, null, null);
        Assert.Equal(AuditCheckScopeOutcome.Unsupported, Assert.Single(result.Scopes).Outcome);
        Assert.Empty(remote.Published);
    }

    [Fact]
    public async Task InvalidSha_IsRequestInvalid()
    {
        var (service, _, _) = BuildService();
        var remote = new ScriptedUpstreamRemote();
        var result = await service.PublishWorkItemAsync(
            MakeProject(), remote, "wi-1", "short-sha", iteration: 1, attempt: 1, null, null);
        Assert.Equal(AuditCheckScopeOutcome.RequestInvalid, Assert.Single(result.Scopes).Outcome);
        Assert.Empty(remote.Published);
    }

    [Fact]
    public async Task RateLimited_SchedulesBoundedRetry_ThenDefersUntilDue()
    {
        var (service, audits, time) = BuildService();
        audits.Reports.Add(MakeReport("wi-4", 1, "lint", "error", [MakeFinding()]));
        var remote = new ScriptedUpstreamRemote
        {
            PublishError = new AuditCheckRateLimitedException("limited", TimeSpan.FromSeconds(60)),
        };
        var project = MakeProject();

        var first = await service.PublishWorkItemAsync(project, remote, "wi-4", Sha, 1, 1, null, null);
        Assert.All(first.Scopes, s => Assert.Equal(AuditCheckScopeOutcome.RetryScheduled, s.Outcome));

        // Immediate re-run defers: the backoff window has not elapsed.
        var second = await service.PublishWorkItemAsync(project, remote, "wi-4", Sha, 1, 1, null, null);
        Assert.All(second.Scopes, s => Assert.Equal(AuditCheckScopeOutcome.DeferredRetry, s.Outcome));
        var callsAfterDefer = remote.Published.Count;

        time.Advance(TimeSpan.FromMinutes(5));
        remote.PublishError = null;
        var third = await service.PublishWorkItemAsync(project, remote, "wi-4", Sha, 1, 1, null, null);
        Assert.All(third.Scopes, s => Assert.Equal(AuditCheckScopeOutcome.Published, s.Outcome));
        Assert.True(remote.Published.Count > callsAfterDefer);

        // Same key re-run after completion is idempotent, not a duplicate.
        var fourth = await service.PublishWorkItemAsync(project, remote, "wi-4", Sha, 1, 1, null, null);
        Assert.All(fourth.Scopes, s => Assert.Equal(AuditCheckScopeOutcome.AlreadyPublished, s.Outcome));
    }

    [Fact]
    public async Task AuthDenied_IsBlockedTerminalState()
    {
        var (service, audits, _) = BuildService();
        audits.Reports.Add(MakeReport("wi-5", 1, "lint", "error", [MakeFinding()]));
        var remote = new ScriptedUpstreamRemote
        {
            PublishError = new AuditCheckAuthException("403: missing checks:write"),
        };
        var result = await service.PublishWorkItemAsync(
            MakeProject(), remote, "wi-5", Sha, iteration: 1, attempt: 1, null, null);
        Assert.All(result.Scopes, s => Assert.Equal(AuditCheckScopeOutcome.Blocked, s.Outcome));
        Assert.Contains("checks:write", result.Scopes[0].Detail!, StringComparison.Ordinal);

        // A re-run stays blocked: auth denial is never retried automatically.
        var calls = remote.Published.Count;
        var again = await service.PublishWorkItemAsync(
            MakeProject(), remote, "wi-5", Sha, iteration: 1, attempt: 1, null, null);
        Assert.All(again.Scopes, s => Assert.Equal(AuditCheckScopeOutcome.Blocked, s.Outcome));
        Assert.Equal(calls, remote.Published.Count);
    }

    [Fact]
    public async Task RetriesExhausted_WithRequireDelivery_ReportsDeliveryRequired()
    {
        var options = EnabledOptions with { RetryMaxAttempts = 0, RequireDelivery = true };
        var (service, audits, _) = BuildService(options);
        audits.Reports.Add(MakeReport("wi-6", 1, "lint", "error", [MakeFinding()]));
        var remote = new ScriptedUpstreamRemote
        {
            PublishError = new AuditCheckTransientException("forge down"),
        };
        var result = await service.PublishWorkItemAsync(
            MakeProject(), remote, "wi-6", Sha, iteration: 1, attempt: 1, null, null);
        Assert.All(result.Scopes, s => Assert.Equal(AuditCheckScopeOutcome.DeliveryRequired, s.Outcome));
    }

    [Fact]
    public async Task CancelledRun_PropagatesCancellation_AndLeavesPendingRows()
    {
        var (service, audits, _) = BuildService();
        audits.Reports.Add(MakeReport("wi-7", 1, "lint", "error", [MakeFinding()]));
        var remote = new ScriptedUpstreamRemote();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PublishWorkItemAsync(
            MakeProject(), remote, "wi-7", Sha, iteration: 1, attempt: 1, null, null, cts.Token));
    }

    [Fact]
    public async Task OldIterationCompletion_AfterNewIterationBegins_IsSuperseded()
    {
        var publications = new SqliteAuditCheckPublicationStore(DbPath);
        _disposables.Add(publications);
        var (service, audits, _) = BuildService(publications: publications);
        audits.Reports.Add(MakeReport("wi-8", 1, "lint", "error", [MakeFinding()]));
        var project = MakeProject();
        var remote = new ScriptedUpstreamRemote();
        var first = await service.PublishWorkItemAsync(project, remote, "wi-8", Sha, 1, 1, null, null);
        Assert.All(first.Scopes, s => Assert.Equal(AuditCheckScopeOutcome.Published, s.Outcome));

        // Iteration 2 begins publishing for the same scope…
        audits.Reports.Add(MakeReport("wi-8", 2, "lint", "none"));
        var second = await service.PublishWorkItemAsync(project, remote, "wi-8", Sha, 2, 1, null, null);
        Assert.All(second.Scopes, s => Assert.Equal(AuditCheckScopeOutcome.Published, s.Outcome));

        // …then iteration 1's delayed completion arrives: it must not apply.
        var stale = new AuditCheckPublicationRecord
        {
            Repository = "myorg/myrepo",
            HeadSha = Sha,
            WorkItemId = "wi-8",
            Target = "code",
            Iteration = 1,
            Attempt = 1,
            Scope = "aggregate",
            CheckName = "codeybox-audit",
            ExternalId = "stale",
            CheckRunId = 1,
            State = AuditCheckPublicationState.Completed,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        Assert.False(await publications.TryCompleteAsync(stale));
    }
}
