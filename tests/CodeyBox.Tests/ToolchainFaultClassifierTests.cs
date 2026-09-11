using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the toolchain-fault classifier over gate subprocess results:
/// conservative matching, platform-agnostic defaults, config-only
/// signatures with hot-reload, auditable records queryable by fault class,
/// routing into the existing bounded WaitingForTransientRetry path, and
/// separation from flake (base-branch) attribution.
/// </summary>
public sealed class ToolchainFaultClassifierTests : IDisposable
{
    private static readonly ProjectId TestProjectId = new("toolchain-fault");
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-toolchain-fault-").FullName;
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { }
    }

    private const string RuntimeCrashOutput =
        "CodeyBox required build: dotnet build Foo.sln\n" +
        "Fatal error.\n" +
        "Internal CLR error. (0x80131506)";

    private const string GenuineCompileError =
        "Build FAILED.\n" +
        "src/Foo.cs(42,13): error CS0165: Use of unassigned local variable 'x'\n" +
        "    0 Warning(s)\n" +
        "    1 Error(s)";

    [Fact]
    public void Classify_RuntimeCrashSignature_IsRetryableToolchainFault()
    {
        var classifier = new ToolchainFaultClassifier(snapshot: null);

        var classification = classifier.Classify(new SubprocessResult(
            "dotnet build", ExitCode: 134, Stdout: RuntimeCrashOutput));

        Assert.Equal(ToolchainFaultDisposition.Retry, classification.Disposition);
        Assert.Equal("dotnet-runtime-crash", classification.FaultClass);
        Assert.Equal("builtin:dotnet-runtime-crash", classification.MatchedSignature);
    }

    [Fact]
    public void Classify_GenuineCompilationError_IsNotToolchainFault()
    {
        var classifier = new ToolchainFaultClassifier(snapshot: null);

        var classification = classifier.Classify(new SubprocessResult(
            "dotnet build", ExitCode: 1, Stdout: GenuineCompileError));

        Assert.Equal(ToolchainFaultClassification.None, classification);
    }

    [Theory]
    [InlineData(137)]
    [InlineData(139)]
    [InlineData(143)]
    [InlineData(129)]
    public void Classify_SignalTerminationExitCodes_IsRetryableWithoutLanguageSignature(int exitCode)
    {
        // No configured signatures at all: the platform-agnostic defaults
        // hold regardless of language.
        var classifier = new ToolchainFaultClassifier(snapshot: null);

        var classification = classifier.Classify(new SubprocessResult(
            "npm test", exitCode, Stdout: "", Stderr: ""));

        Assert.Equal(ToolchainFaultDisposition.Retry, classification.Disposition);
        Assert.NotNull(classification.MatchedSignature);
        Assert.StartsWith("builtin:", classification.MatchedSignature, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_Exit137_IsOomKilled()
    {
        var classifier = new ToolchainFaultClassifier(snapshot: null);

        var classification = classifier.Classify(new SubprocessResult(
            "go build ./...", ExitCode: 137));

        Assert.Equal(ToolchainFaultDisposition.Retry, classification.Disposition);
        Assert.Equal("oom-killed", classification.FaultClass);
    }

    [Theory]
    [InlineData("No space left on device")]
    [InlineData("write error: ENOSPC")]
    public void Classify_DiskExhaustion_IsRetryable(string fragment)
    {
        var classifier = new ToolchainFaultClassifier(snapshot: null);

        var classification = classifier.Classify(new SubprocessResult(
            "dotnet build", ExitCode: 1, Stdout: $"error: {fragment} while writing obj/foo.dll"));

        Assert.Equal(ToolchainFaultDisposition.Retry, classification.Disposition);
        Assert.Equal("disk-exhaustion", classification.FaultClass);
    }

    [Fact]
    public void Classify_ExitZeroWithNoOutput_IsNotToolchainFault()
    {
        var classifier = new ToolchainFaultClassifier(snapshot: null);

        var classification = classifier.Classify(new SubprocessResult("dotnet build", 0));

        Assert.Equal(ToolchainFaultClassification.None, classification);
    }

    [Fact]
    public void Classify_ConfigOnlySignature_TakesEffectAfterReplaceWithoutRestart()
    {
        // A language the repository has never built (Go proxy failure): no
        // code change, no restart — the signature arrives via configuration.
        var snapshot = new ToolchainFaultSnapshot(
            new Dictionary<string, ToolchainFaultSignatureOptions?>(StringComparer.OrdinalIgnoreCase));
        var classifier = new ToolchainFaultClassifier(snapshot);
        var failing = new SubprocessResult(
            "go build ./...", ExitCode: 1,
            Stderr: "go: downloading module: GOPROXY=off and proxy unreachable");

        Assert.Equal(
            ToolchainFaultClassification.None,
            classifier.Classify(failing));

        snapshot.Replace(new Dictionary<string, ToolchainFaultSignatureOptions?>(StringComparer.OrdinalIgnoreCase)
        {
            ["go-proxy-offline"] = new ToolchainFaultSignatureOptions
            {
                FaultClass = "go-proxy-offline",
                Disposition = ToolchainFaultDisposition.Retry,
                OutputContains = "GOPROXY=off",
            },
        });

        var classification = classifier.Classify(failing);
        Assert.Equal(ToolchainFaultDisposition.Retry, classification.Disposition);
        Assert.Equal("go-proxy-offline", classification.FaultClass);
        Assert.Equal("go-proxy-offline", classification.MatchedSignature);
    }

    [Fact]
    public void Snapshot_Replace_WithInvalidSignature_ThrowsAndKeepsPriorView()
    {
        var snapshot = new ToolchainFaultSnapshot(
            new Dictionary<string, ToolchainFaultSignatureOptions?>(StringComparer.OrdinalIgnoreCase));
        var classifier = new ToolchainFaultClassifier(snapshot);

        Assert.Throws<InvalidOperationException>(() => snapshot.Replace(
            new Dictionary<string, ToolchainFaultSignatureOptions?>(StringComparer.OrdinalIgnoreCase)
            {
                ["broken"] = new ToolchainFaultSignatureOptions
                {
                    // No FaultClass and no match: invalid.
                    Disposition = ToolchainFaultDisposition.Retry,
                },
            }));

        Assert.Equal(
            ToolchainFaultClassification.None,
            classifier.Classify(new SubprocessResult("dotnet build", 1, "error CS0001")));
        Assert.Empty(snapshot.Current);
    }

    [Fact]
    public void RecordStore_CountsAreQueryableByFaultClass()
    {
        var store = new InMemoryToolchainFaultRecordStore();

        store.Record(new ToolchainFaultRecord(
            DateTimeOffset.UtcNow, "dotnet build", 134,
            "dotnet-runtime-crash", "builtin:dotnet-runtime-crash",
            ToolchainFaultDisposition.Retry));
        store.Record(new ToolchainFaultRecord(
            DateTimeOffset.UtcNow, "dotnet build", 134,
            "dotnet-runtime-crash", "builtin:dotnet-runtime-crash",
            ToolchainFaultDisposition.Retry));
        store.Record(new ToolchainFaultRecord(
            DateTimeOffset.UtcNow, "go build ./...", 137,
            "oom-killed", "builtin:oom-killed",
            ToolchainFaultDisposition.Retry));

        var counts = store.GetCountsByFaultClass();
        Assert.Equal(2, counts["dotnet-runtime-crash"]);
        Assert.Equal(1, counts["oom-killed"]);

        var crashOnly = store.List("dotnet-runtime-crash");
        Assert.Equal(2, crashOnly.Count);
        Assert.All(crashOnly, r =>
        {
            Assert.Equal("dotnet build", r.Command);
            Assert.Equal(134, r.ExitCode);
            Assert.Equal("builtin:dotnet-runtime-crash", r.MatchedSignature);
        });
    }

    [Fact]
    public async Task Gate_RuntimeCrash_ThrowsTransientAndProducesNoAuditFinding()
    {
        var persistCalls = 0;
        var store = new InMemoryToolchainFaultRecordStore();
        var gate = new RequiredBuildGate(
            new TestRequiredBuildVerifier(
                RequiredBuildProbeResult.Applies,
                RequiredBuildVerificationResult.Failed(134, RuntimeCrashOutput)),
            persistReport: (_, _, _, _, _, _) =>
            {
                persistCalls++;
                return Task.CompletedTask;
            },
            toolchainFaultClassifier: new ToolchainFaultClassifier(snapshot: null),
            toolchainFaultRecords: store);
        var item = NewItem("feature/toolchain-crash");
        var project = NewProject(item);

        var ex = await Assert.ThrowsAsync<ToolchainFaultTransientException>(() =>
            gate.RunForAuditGateAsync(
                item, project, repoId: "ignored",
                baseBranch: item.BaseBranch!, workBranch: item.WorkBranch!,
                iteration: 1, ct: CancellationToken.None));

        Assert.Equal(ToolchainFaultDisposition.Retry, ex.Classification.Disposition);
        Assert.Equal("dotnet-runtime-crash", ex.Classification.FaultClass);
        Assert.Equal("audit", ex.Phase);
        Assert.Equal(0, persistCalls);

        var counts = store.GetCountsByFaultClass();
        Assert.Equal(1, counts["dotnet-runtime-crash"]);
        var record = Assert.Single(store.List("dotnet-runtime-crash"));
        Assert.Equal("builtin:dotnet-runtime-crash", record.MatchedSignature);
        Assert.Equal("dotnet build", record.Command);
        Assert.Equal(134, record.ExitCode);
    }

    [Fact]
    public async Task Gate_GenuineCompilationError_StillProducesFinding()
    {
        var persistCalls = 0;
        var store = new InMemoryToolchainFaultRecordStore();
        var gate = new RequiredBuildGate(
            new TestRequiredBuildVerifier(
                RequiredBuildProbeResult.Applies,
                RequiredBuildVerificationResult.Failed(1, GenuineCompileError)),
            persistReport: (_, _, _, _, _, _) =>
            {
                persistCalls++;
                return Task.CompletedTask;
            },
            toolchainFaultClassifier: new ToolchainFaultClassifier(snapshot: null),
            toolchainFaultRecords: store);
        var item = NewItem("feature/real-breakage");
        var project = NewProject(item);

        var result = await gate.RunForAuditGateAsync(
            item, project, repoId: "ignored",
            baseBranch: item.BaseBranch!, workBranch: item.WorkBranch!,
            iteration: 1, ct: CancellationToken.None);

        Assert.True(result.Applies);
        Assert.NotNull(result.Finding);
        Assert.Equal(1, persistCalls);
        Assert.Empty(store.GetCountsByFaultClass());
    }

    [Fact]
    public async Task Gate_EscalateDisposition_SurfacesAsUnavailableWithoutFinding()
    {
        var persistCalls = 0;
        var snapshot = new ToolchainFaultSnapshot(
            new Dictionary<string, ToolchainFaultSignatureOptions?>(StringComparer.OrdinalIgnoreCase)
            {
                ["sandbox-gone"] = new ToolchainFaultSignatureOptions
                {
                    FaultClass = "sandbox-gone",
                    Disposition = ToolchainFaultDisposition.Escalate,
                    OutputContains = "sandbox unreachable",
                },
            });
        var gate = new RequiredBuildGate(
            new TestRequiredBuildVerifier(
                RequiredBuildProbeResult.Applies,
                RequiredBuildVerificationResult.Failed(1, "build aborted: sandbox unreachable mid-run")),
            persistReport: (_, _, _, _, _, _) =>
            {
                persistCalls++;
                return Task.CompletedTask;
            },
            toolchainFaultClassifier: new ToolchainFaultClassifier(snapshot),
            toolchainFaultRecords: new InMemoryToolchainFaultRecordStore());
        var item = NewItem("feature/escalate");
        var project = NewProject(item);

        await Assert.ThrowsAsync<RequiredBuildVerificationUnavailableException>(() =>
            gate.RunForAuditGateAsync(
                item, project, repoId: "ignored",
                baseBranch: item.BaseBranch!, workBranch: item.WorkBranch!,
                iteration: 1, ct: CancellationToken.None));
        Assert.Equal(0, persistCalls);
    }

    [Fact]
    public async Task RetryableClassification_LandsInTransientRetryHonoursBoundThenFailsTerminally()
    {
        using var fixture = BuildScheduler(new AutoRetryOnTransientFailureOptions
        {
            Enabled = true,
            BaseDelay = TimeSpan.FromSeconds(30),
            MaxDelay = TimeSpan.FromMinutes(15),
            Multiplier = 2,
            MaxAutoRetriesPerWorkItem = 2,
            MaxElapsedTime = TimeSpan.FromHours(1),
            JitterMode = TransientRetryJitterMode.None,
        });

        // What the pipeline catch does with a ToolchainFaultTransientException:
        // park in WaitingForTransientRetry with failureKind=transient, then
        // hand to the existing scheduler — no second retry mechanism.
        var item = NewTransientItem();
        await fixture.Store.CreateAsync(item);

        var scheduled = await fixture.Scheduler.NotifyTransientFailureAsync(item);
        Assert.Equal(WorkItemAutoRetryScheduleStatus.Scheduled, scheduled.Status);
        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, stored!.State);
        Assert.Equal("transient", stored.FailureKind);
        Assert.NotNull(stored.NextTransientRetryAt);

        var atCap = stored with { TransientRetryAttempts = 2 };
        await fixture.Store.UpdateAsync(atCap);
        var exhausted = await fixture.Scheduler.NotifyTransientFailureAsync(atCap);

        Assert.Equal(WorkItemAutoRetryScheduleStatus.Exhausted, exhausted.Status);
        var terminal = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(terminal);
        Assert.Equal(WorkItemState.Failed, terminal!.State);
        Assert.Equal("transient-exhausted", terminal.FailureKind);
    }

    [Fact]
    public void ToolchainFault_And_NonDiffTestFailure_HaveDifferentDispositions()
    {
        // Same failing test on base and diff: not diff-attributable, and —
        // crucially — not a toolchain retry. The toolchain fault re-runs the
        // same commit; flake attribution consults the base branch.
        var attribution = TestFailureAttributionClassifier.Classify(new TestFailureRunPair(
            "MySuite.FlakyTest",
            BaseRun: TestFailureRunOutcome.Failed,
            DiffRun: TestFailureRunOutcome.Failed));

        var toolchain = new ToolchainFaultClassifier(snapshot: null).Classify(new SubprocessResult(
            "dotnet build", ExitCode: 134, Stdout: RuntimeCrashOutput));

        Assert.Equal(TestFailureAttribution.NotDiffAttributable, attribution.Attribution);
        Assert.Equal(ToolchainFaultDisposition.Retry, toolchain.Disposition);
        Assert.NotEqual(
            TestFailureAttribution.DiffAttributable.ToString(),
            toolchain.Disposition.ToString());
    }

    private static WorkItem NewItem(string workBranch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = TestProjectId,
        Title = "toolchain fault test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = false,
    };

    private static Project NewProject(WorkItem item) => new()
    {
        Id = item.ProjectId,
        DisplayName = "Toolchain fault test project",
        RepositoryUrl = "ignored",
        DefaultBaseBranch = "main",
    };

    private static WorkItem NewTransientItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = TestProjectId,
        Title = "Transient retry",
        Prompt = "retry after toolchain fault",
        State = WorkItemState.WaitingForTransientRetry,
        LastError = "required build hit toolchain fault 'dotnet-runtime-crash'",
        FailureKind = "transient",
        PushUpstream = false,
    };

    private SchedulerFixture BuildScheduler(AutoRetryOnTransientFailureOptions transientOptions)
    {
        var sqliteStore = new SqliteWorkItemStore(Path.Combine(_workspace, $"state-{Guid.NewGuid():N}.db"));
        var queue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(
            sqliteStore, queue, new FakeGitHost(), NullLogger<WorkItemRetrier>.Instance);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = TestProjectId,
            DisplayName = "Toolchain fault",
            RepositoryUrl = "file:///tmp/toolchain-fault",
            DefaultAgent = AgentKind.Claude,
        });
        var opts = new OrchestratorOptions
        {
            AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions { Enabled = false },
            AutoRetryOnTransientFailure = transientOptions,
        };
        var terminalTransitions = TestSupport.CreateTerminalTransition(
            sqliteStore, webhooks: null, projects);
        var scheduler = new TransientRetryScheduler(
            sqliteStore,
            retrier,
            opts,
            NullLogger<TransientRetryScheduler>.Instance,
            terminalTransitions,
            projects: projects,
            timeProvider: _time,
            transientRetryOptionsAccessor: () => transientOptions,
            jitterRandom: () => 0.0);
        return new SchedulerFixture(sqliteStore, scheduler);
    }

    private sealed record SchedulerFixture(
        SqliteWorkItemStore Store,
        TransientRetryScheduler Scheduler) : IDisposable
    {
        public void Dispose()
        {
            Scheduler.Dispose();
            Store.Dispose();
        }
    }
}
