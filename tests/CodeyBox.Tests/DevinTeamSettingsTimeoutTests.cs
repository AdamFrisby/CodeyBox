using CodeyBox.Agents.Devin;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Acceptance coverage for sig-devin-team-settings-timeout: the Devin ACP
/// startup transport failure (<c>fatal</c> <c>session/new</c> team-settings
/// fetch timeout, code -32603) must park for bounded transient retry on the
/// existing policy, while auth/config/permission shapes, other stages, bare
/// codes/timeouts, prose, and malformed/oversized inputs keep their existing
/// behavior. Wiring tests run the real persistent store + retry scheduler
/// with controlled clock seams — never a mocked final outcome.
/// </summary>
public sealed class DevinTeamSettingsTimeoutTests : IDisposable
{
    internal const string ExactMessage =
        "Failed to load team settings: Failed to fetch team settings: fetch timed out after 10000ms";

    private static readonly ProjectId TestProjectId = new("devin-team-settings-timeout");
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-devin-team-settings-").FullName;
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    private static string FatalEnvelope(
        string stage = "session/new",
        string eventName = "fatal",
        string codeJson = "-32603",
        string? message = ExactMessage)
    {
        var messageJson = message is null ? "null" : $"\"{message.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
        return $"{{\"type\":\"devin.acp\",\"event\":\"{eventName}\",\"stage\":\"{stage}\",\"code\":{codeJson},\"message\":{messageJson}}}";
    }

    private static string StdoutWith(params string[] lines) => string.Join("\n", lines);

    // Positive: exact envelope through the real diagnostic + classification path.

    [Fact]
    public void ExactEnvelope_OutcomeIsFatalSessionNewWithCode()
    {
        var stdout = StdoutWith(
            "{\"type\":\"devin.acp\",\"event\":\"session_started\",\"sessionId\":\"s-1\"}",
            FatalEnvelope());

        var outcome = DevinAcpOutcome.Extract(stdout);

        Assert.Equal(DevinAcpOutcome.TerminalEvent.Fatal, outcome.Event);
        Assert.NotNull(outcome.Diagnostic);
        Assert.Contains("session/new", outcome.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("-32603", outcome.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactEnvelope_ClassifiesInfraTransport()
    {
        var stdout = StdoutWith(FatalEnvelope());

        var detection = new DevinQuotaFailureDetector().DetectProviderTransient(null, stdout, null);

        Assert.NotNull(detection);
        Assert.Equal(ProviderTransientKind.InfraTransport, detection!.Kind);
        Assert.Equal("devin-team-settings-timeout", detection.MatchedSignature);
    }

    [Fact]
    public void ExactEnvelope_CompositeDispatch_ClassifiesInfraTransport()
    {
        var stdout = StdoutWith(FatalEnvelope());
        var classifier = new CompositeQuotaFailureClassifier([new DevinQuotaFailureDetector()]);

        var detection = classifier.DetectProviderTransient(AgentKind.Devin, null, stdout, null);

        Assert.NotNull(detection);
        Assert.Equal(ProviderTransientKind.InfraTransport, detection!.Kind);
        Assert.Equal("devin-team-settings-timeout", detection.MatchedSignature);
    }

    [Fact]
    public void StringCodeVariant_ClassifiesInfraTransport()
    {
        var stdout = StdoutWith(FatalEnvelope(codeJson: "\"-32603\""));

        var detection = new DevinQuotaFailureDetector().DetectProviderTransient(null, stdout, null);

        Assert.NotNull(detection);
        Assert.Equal(ProviderTransientKind.InfraTransport, detection!.Kind);
    }

    [Fact]
    public void NestedEnvelopeMidLine_ClassifiesInfraTransport()
    {
        var stdout = StdoutWith(
            "shim log prefix " + FatalEnvelope() + " trailing noise");

        var detection = new DevinQuotaFailureDetector().DetectProviderTransient(null, stdout, null);

        Assert.NotNull(detection);
        Assert.Equal(ProviderTransientKind.InfraTransport, detection!.Kind);
    }

    [Fact]
    public void EscapedEnvelope_ClassifiesInfraTransport()
    {
        var escaped = FatalEnvelope().Replace("\"", "\\\"", StringComparison.Ordinal);
        var stdout = StdoutWith("{\"log\":\"" + escaped + "\"}");

        var detection = new DevinQuotaFailureDetector().DetectProviderTransient(null, stdout, null);

        Assert.NotNull(detection);
        Assert.Equal(ProviderTransientKind.InfraTransport, detection!.Kind);
    }

    [Fact]
    public void LiftedDiagnosticShape_ClassifiesInfraTransport()
    {
        var summary = $"devin acp fatal during session/new: {ExactMessage} (code -32603)";

        var detection = new DevinQuotaFailureDetector().DetectProviderTransient(null, null, summary);

        Assert.NotNull(detection);
        Assert.Equal(ProviderTransientKind.InfraTransport, detection!.Kind);
    }

    [Fact]
    public void ParkedError_UsesTransientKindWithoutRawSecrets()
    {
        var detection = new DevinQuotaFailureDetector().DetectProviderTransient(
            null, StdoutWith(FatalEnvelope()), null);
        Assert.NotNull(detection);

        var parkedError = ProviderTransientRetryPolicy.BuildParkedError(detection!);

        Assert.Contains("infra-transport", parkedError);
        Assert.Contains("devin-team-settings-timeout", parkedError);
        Assert.DoesNotContain("Failed to fetch team settings", parkedError);
        Assert.Equal("transient", ProviderTransientRetryPolicy.ParkedFailureKind);
    }

    // Negatives: code alone, plain timeout, other stages/events, prose.

    [Fact]
    public void SameCodeDifferentMessage_StaysTerminal()
    {
        var stdout = StdoutWith(FatalEnvelope(message: "session/new response did not carry a sessionId"));

        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient(null, stdout, null));
    }

    [Fact]
    public void SameCodeAtPromptStage_StaysTerminal()
    {
        var stdout = StdoutWith(FatalEnvelope(stage: "prompt", message: ExactMessage));

        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient(null, stdout, null));
    }

    [Fact]
    public void SameCodeAtInitializeStage_StaysTerminal()
    {
        var stdout = StdoutWith(FatalEnvelope(stage: "initialize", message: ExactMessage));

        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient(null, stdout, null));
    }

    [Fact]
    public void SameCodeAtSessionPromptStage_StaysTerminal()
    {
        var stdout = StdoutWith(FatalEnvelope(stage: "session/prompt", message: ExactMessage));

        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient(null, stdout, null));
    }

    [Fact]
    public void TurnErrorWithSameMessage_StaysTerminal()
    {
        var stdout = StdoutWith(FatalEnvelope(eventName: "turn_error", stage: "prompt"));

        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient(null, stdout, null));
    }

    [Fact]
    public void BareCodeAlone_StaysTerminal()
    {
        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient(null, "code -32603", null));
        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient("error -32603", null, null));
    }

    [Fact]
    public void PlainTimeoutWithoutEnvelope_StaysTerminal()
    {
        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient(
            null, "fetch timed out after 10000ms", null));
        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient(
            "Failed to fetch team settings: fetch timed out after 10000ms", null, null));
    }

    [Fact]
    public void RepositoryProseWithExactWords_StaysTerminal()
    {
        const string prose = "The retry helper wraps fetch() so a fetch timed out after 10000ms " +
            "while loading team settings surfaces as a typed error; see Failed to load team settings handling.";
        var stdout = StdoutWith(
            "{\"type\":\"devin.acp\",\"event\":\"session_update\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"text\":" +
            $"\"{prose}\"}}}}");

        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient(null, stdout, null));
    }

    [Fact]
    public void MalformedJson_StaysTerminal()
    {
        var stdout = StdoutWith(
            "{\"type\":\"devin.acp\",\"event\":\"fatal\",\"stage\":\"session/new\",\"code\":-32603,\"message\":\"Failed to load team settings: ");

        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient(null, stdout, null));
    }

    [Fact]
    public void OversizedInput_BuriedEnvelopeOutsideWindow_StaysTerminal()
    {
        var padding = new string('x', ProviderTransientMatcher.MaxScannedChars + 4096);
        var stdout = FatalEnvelope() + "\n" + padding;

        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient(null, stdout, null));
    }

    [Theory]
    [InlineData("go build ./... timed out after 300s")]
    [InlineData("pytest timed out: test suite exceeded phase budget")]
    [InlineData("phase budget exceeded: work timeout after 30m")]
    [InlineData("agent exited 1")]
    [InlineData("Operation canceled")]
    [InlineData("timeout")]
    [InlineData("504")]
    public void BuildTestPhaseTimeoutsAndCancellations_StayTerminal(string text)
    {
        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient(text, null, null));
        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient(null, text, null));
        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient(null, null, text));
    }

    [Fact]
    public void MissingTeamConfiguration_StaysTerminal()
    {
        const string stderr = "Error: team settings not configured for this workspace";

        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient(stderr, null, null));
    }

    [Fact]
    public void MissingSessionId_StaysTerminal()
    {
        var stdout = StdoutWith(FatalEnvelope(message: "session/new response did not carry a sessionId"));

        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient(null, stdout, null));
    }

    [Fact]
    public void UnrelatedUnimplementedShape_KeepsExistingBehavior()
    {
        const string stderr = "Agent error: Client error: Protocol error (unimplemented): unknown method";

        var detection = new DevinQuotaFailureDetector().DetectProviderTransient(stderr, null, null);

        Assert.NotNull(detection);
        Assert.Equal(ProviderTransientKind.ModelCapacity, detection!.Kind);
        Assert.Equal("protocol-unimplemented", detection.MatchedSignature);
    }

    [Theory]
    [InlineData("Error: 401 Unauthorized")]
    [InlineData("Error: 403 Forbidden")]
    [InlineData("Error: Not logged in. Run `devin auth login` to authenticate.")]
    [InlineData("Authentication required")]
    public void AuthFailure_MixedWithTimeoutChatter_KeepsAuthClassification(string authLine)
    {
        var stdout = StdoutWith(FatalEnvelope());
        var stderr = authLine + "\nFailed to fetch team settings: fetch timed out after 10000ms";

        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient(stderr, stdout, null));

        var classification = AgentFailureClassifier.Classify(AgentKind.Devin, stderr, stdout, "agent exited 1");
        var quotaDetection = new DevinQuotaFailureDetector().Detect(stderr, stdout);
        Assert.True(
            classification.Kind is AgentFailureKind.AuthError or AgentFailureKind.AuthRequired
                || quotaDetection?.Kind == QuotaFailureKind.Unauthorized,
            $"expected preserved auth classification, got {classification.Kind}/{quotaDetection?.Kind}");
    }

    // Wiring: real store + scheduler with controlled clock seams.

    [Fact]
    public async Task TeamSettingsTimeout_ParksWaitingForTransientRetry_PreservingIdentity()
    {
        using var fixture = BuildScheduler(EnabledRetryOptions());
        var dependency = WorkItemId.New();
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = TestProjectId,
            Title = "devin team-settings timeout",
            Prompt = "do work",
            State = WorkItemState.Working,
            Agent = AgentKind.Devin,
            ModelId = "devin-test-model",
            BaseBranch = "main",
            WorkBranch = "codeybox/wip-timeout",
            DependsOn = [dependency],
        };

        var detection = new DevinQuotaFailureDetector().DetectProviderTransient(
            null, StdoutWith(FatalEnvelope()), null);
        Assert.NotNull(detection);
        var parkedError = ProviderTransientRetryPolicy.BuildParkedError(detection!);
        var parked = item.With(
            WorkItemState.WaitingForTransientRetry,
            parkedError,
            failureKind: ProviderTransientRetryPolicy.ParkedFailureKind);
        // The in-memory park preserves the model route (never a failover
        // model); ModelId itself is runtime-only and resolves fresh from the
        // same membership on the next pickup, so it is not a stored column.
        Assert.Equal("devin-test-model", parked.ModelId);
        await fixture.Store.CreateAsync(parked);

        var result = await fixture.Scheduler.NotifyTransientFailureAsync(parked);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemAutoRetryScheduleStatus.Scheduled, result.Status);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, stored!.State);
        Assert.Equal("transient", stored.FailureKind);
        Assert.Equal(parkedError, stored.LastError);
        Assert.Equal(_time.GetUtcNow().AddSeconds(30), stored.NextTransientRetryAt);
        Assert.Equal(0, stored.TransientRetryAttempts);
        Assert.Equal(0, stored.TerminalFailureCount);
        Assert.Equal(AgentKind.Devin, stored.Agent);
        Assert.Equal([dependency], stored.DependsOn);
        Assert.Equal("codeybox/wip-timeout", stored.WorkBranch);
        Assert.Null(stored.AgentTurnResumeCheckpoint);
        Assert.Null(stored.PreemptCheckpoint);
        Assert.Equal(0, fixture.Queue.Count);
    }

    [Fact]
    public async Task TeamSettingsTimeout_RestartPreservesBudgetAndSchedule_AllowingLaterDispatch()
    {
        var dbPath = Path.Combine(_workspace, $"state-{Guid.NewGuid():N}.db");
        WorkItemId itemId;
        IReadOnlyList<WorkItemId> dependsOn;
        {
            using var fixture = BuildScheduler(EnabledRetryOptions(), dbPath);
            var dependency = WorkItemId.New();
            dependsOn = [dependency];
            var item = new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = TestProjectId,
                Title = "devin team-settings timeout",
                Prompt = "do work",
                State = WorkItemState.Working,
                Agent = AgentKind.Devin,
                ModelId = "devin-test-model",
                BaseBranch = "main",
                WorkBranch = "codeybox/wip-timeout",
                DependsOn = dependsOn,
            };
            itemId = item.Id;

            var detection = new DevinQuotaFailureDetector().DetectProviderTransient(
                null, StdoutWith(FatalEnvelope()), null);
            Assert.NotNull(detection);
            var parked = item.With(
                WorkItemState.WaitingForTransientRetry,
                ProviderTransientRetryPolicy.BuildParkedError(detection!),
                failureKind: ProviderTransientRetryPolicy.ParkedFailureKind);
            Assert.Equal("devin-test-model", parked.ModelId);
            await fixture.Store.CreateAsync(parked);
            await fixture.Scheduler.NotifyTransientFailureAsync(parked);
        }

        using var restarted = BuildScheduler(EnabledRetryOptions(), dbPath);
        var reloaded = await restarted.Store.GetAsync(itemId);
        Assert.NotNull(reloaded);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, reloaded!.State);
        Assert.Equal("transient", reloaded.FailureKind);
        Assert.Equal(_time.GetUtcNow().AddSeconds(30), reloaded.NextTransientRetryAt);
        Assert.Equal(0, reloaded.TransientRetryAttempts);

        _time.Advance(TimeSpan.FromSeconds(31));
        await RunTransientPeriodicSweepAsync(restarted.Scheduler);

        var dispatched = await restarted.Store.GetAsync(itemId);
        Assert.NotNull(dispatched);
        Assert.Equal(1, dispatched!.TransientRetryAttempts);
        Assert.Equal(AgentKind.Devin, dispatched.Agent);
        Assert.Equal(dependsOn, dispatched.DependsOn);
        Assert.Equal("codeybox/wip-timeout", dispatched.WorkBranch);
    }

    [Fact]
    public async Task TeamSettingsTimeout_AtAttemptCap_ExhaustsViaExistingPolicy()
    {
        using var fixture = BuildScheduler(EnabledRetryOptions());
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = TestProjectId,
            Title = "devin team-settings timeout",
            Prompt = "do work",
            State = WorkItemState.WaitingForTransientRetry,
            LastError = "provider-transient(infra-transport; signature devin-team-settings-timeout)",
            FailureKind = "transient",
            Agent = AgentKind.Devin,
            ModelId = "devin-test-model",
            TransientRetryAttempts = 5,
            TransientRetryFirstFailedAt = _time.GetUtcNow(),
        };
        await fixture.Store.CreateAsync(item);

        await fixture.Scheduler.NotifyTransientFailureAsync(item);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Failed, stored!.State);
        Assert.Equal("transient-exhausted", stored.FailureKind);
        Assert.Contains("attempts=5; max=5", stored.LastError);
        Assert.Equal(0, fixture.Queue.Count);
    }

    private SchedulerFixture BuildScheduler(AutoRetryOnTransientFailureOptions transientOptions, string? dbPath = null)
    {
        var sqliteStore = new SqliteWorkItemStore(dbPath ?? Path.Combine(_workspace, $"state-{Guid.NewGuid():N}.db"));
        var queue = new InMemoryTaskQueue();
        var gitHost = new RecordingGitHost();
        var retrier = new WorkItemRetrier(sqliteStore, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = TestProjectId,
            DisplayName = "Devin team-settings timeout",
            RepositoryUrl = "file:///tmp/devin-team-settings-timeout",
            DefaultAgent = AgentKind.Devin,
        });
        var opts = new OrchestratorOptions
        {
            AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions { Enabled = false },
            AutoRetryOnTransientFailure = transientOptions,
        };
        var scheduler = new TransientRetryScheduler(
            sqliteStore,
            retrier,
            opts,
            NullLogger<TransientRetryScheduler>.Instance,
            TestSupport.CreateTerminalTransition(sqliteStore, null, projects),
            projects: projects,
            timeProvider: _time,
            transientRetryOptionsAccessor: () => transientOptions,
            jitterRandom: () => 0.5);

        return new SchedulerFixture(sqliteStore, queue, scheduler);
    }

    private static async Task RunTransientPeriodicSweepAsync(TransientRetryScheduler scheduler)
    {
        var method = typeof(TransientRetryScheduler).GetMethod(
            "RunTransientPeriodicSweepAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(scheduler, [CancellationToken.None])!;
    }

    private static AutoRetryOnTransientFailureOptions EnabledRetryOptions() => new()
    {
        Enabled = true,
        BaseDelay = TimeSpan.FromSeconds(30),
        MaxDelay = TimeSpan.FromMinutes(15),
        Multiplier = 2,
        MaxAutoRetriesPerWorkItem = 5,
        MaxElapsedTime = TimeSpan.FromHours(1),
        JitterMode = TransientRetryJitterMode.None,
    };

    private sealed record SchedulerFixture(
        SqliteWorkItemStore Store,
        InMemoryTaskQueue Queue,
        TransientRetryScheduler Scheduler) : IDisposable
    {
        public void Dispose()
        {
            Scheduler.Dispose();
            Store.Dispose();
        }
    }

    private sealed class RecordingGitHost : IGitHost
    {
        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
            => Task.FromResult(id.ToString());

        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
            => Task.FromResult(id.ToString());

        public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
            => throw new NotSupportedException();

        public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
            => Task.FromResult("main");

        public Task PushToUpstreamAsync(
            string repositoryId,
            string upstreamUrl,
            string branch,
            IReadOnlyDictionary<string, string> upstreamEnv,
            UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> BranchExistsAsync(string repositoryId, string branch, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> BranchHasCommitsAheadAsync(
            string repositoryId,
            string baseBranch,
            string workBranch,
            CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
            string repositoryId,
            string baseBranch,
            string workBranch,
            CancellationToken ct = default)
            => Task.FromResult((string.Empty, string.Empty));
    }
}
