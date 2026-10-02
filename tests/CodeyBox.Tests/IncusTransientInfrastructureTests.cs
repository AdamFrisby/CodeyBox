using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox.Incus;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for the typed transient classification of Incus control-plane
/// faults: every evidence-shaped error classifies as transient with its fault
/// class, unrelated incus errors stay terminal, the signatures are
/// hot-reloadable configuration, and a signature-derived deferral rides the
/// existing bounded transient-retry path.
/// </summary>
public sealed class IncusTransientInfrastructureTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-incus-transient-").FullName;
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(
        "Incus verify effective VM device topology failed with exit code 1: Error: Failed to begin transaction: context deadline exceeded",
        IncusTransientInfrastructure.BeginTransactionFaultClass,
        false)]
    [InlineData(
        "Incus could not verify required build: Incus verify effective VM device topology failed with exit code 1: Error: Failed to begin transaction: context deadline exceeded",
        IncusTransientInfrastructure.BeginTransactionFaultClass,
        false)]
    [InlineData(
        "Incus query failed with exit code 1: Error: context deadline exceeded",
        IncusTransientInfrastructure.CliDeadlineFaultClass,
        false)]
    [InlineData(
        "Incus delete sandbox VM failed with exit code 1: Error: Stopping the instance codeybox-48a63b85 failed: Failed unmounting instance: Failed to unmount \"/var/lib/incus/storage-pools/pool\" on \"/mnt\": device busy",
        IncusTransientInfrastructure.TeardownUnmountFaultClass,
        true)]
    [InlineData(
        "Incus delete sandbox VM failed with exit code 1: Error: Failed to unmount \"/var/lib/incus/storage-pools/pool\"",
        IncusTransientInfrastructure.TeardownUnmountFaultClass,
        true)]
    [InlineData(
        "Incus exec completed, but transient guest control-file cleanup could not be verified; the VM was stopped and must be disposed.",
        IncusTransientInfrastructure.ExecCleanupFaultClass,
        false)]
    public void TryClassifyMessage_EvidenceSignatures_AreTransient(
        string message,
        string expectedFaultClass,
        bool expectedTeardown)
    {
        var fault = IncusTransientInfrastructure.TryClassifyMessage(
            message,
            IncusTransientInfrastructure.DefaultSignatures);

        Assert.NotNull(fault);
        Assert.Equal(expectedFaultClass, fault!.FaultClass);
        Assert.Equal(expectedTeardown, fault.IsTeardown);
        Assert.Equal(expectedTeardown, IncusTransientInfrastructure.IsTeardownFault(fault));
    }

    [Theory]
    [InlineData("Incus start VM failed with exit code 1: Error: instance already exists")]
    [InlineData("Incus delete sandbox VM failed with exit code 1: Error: Device or resource busy")]
    [InlineData("Incus stop returned success without a positively verified STOPPED state.")]
    [InlineData("some unrelated failure without any known marker")]
    [InlineData("")]
    public void TryClassifyMessage_UnrelatedErrors_StayTerminal(string message)
    {
        Assert.Null(IncusTransientInfrastructure.TryClassifyMessage(
            message,
            IncusTransientInfrastructure.DefaultSignatures));
    }

    [Fact]
    public void TryClassify_NullException_IsNull()
    {
        Assert.Null(IncusTransientInfrastructure.TryClassifyMessage(null, IncusTransientInfrastructure.DefaultSignatures));
        Assert.Null(IncusTransientInfrastructure.TryClassify(null, IncusTransientInfrastructure.DefaultSignatures));
    }

    [Fact]
    public void TryClassify_WalksInnerExceptions()
    {
        var inner = new InvalidOperationException(
            "Incus verify effective VM device topology failed with exit code 1: Error: Failed to begin transaction: context deadline exceeded");
        var outer = new InvalidOperationException("sandbox creation failed", inner);

        var fault = IncusTransientInfrastructure.TryClassify(outer, IncusTransientInfrastructure.DefaultSignatures);

        Assert.NotNull(fault);
        Assert.Equal(IncusTransientInfrastructure.BeginTransactionFaultClass, fault!.FaultClass);
    }

    [Fact]
    public void TryClassify_PrefersMostSpecificSignature()
    {
        // The begin-transaction message also contains the generic CLI-deadline
        // text; the precise DB fault class must win regardless of order.
        var fault = IncusTransientInfrastructure.TryClassifyMessage(
            "Error: Failed to begin transaction: context deadline exceeded",
            ["context deadline exceeded", "Failed to begin transaction: context deadline exceeded"]);

        Assert.NotNull(fault);
        Assert.Equal(IncusTransientInfrastructure.BeginTransactionFaultClass, fault!.FaultClass);
    }

    [Fact]
    public void TryClassify_EmptyConfiguredList_MatchesNothing()
    {
        Assert.Null(IncusTransientInfrastructure.TryClassifyMessage(
            "Error: Failed to begin transaction: context deadline exceeded",
            []));
    }

    [Fact]
    public void TryClassify_CustomSignature_UsesGenericFaultClass()
    {
        var fault = IncusTransientInfrastructure.TryClassifyMessage(
            "Error: something operator-known happened",
            ["something operator-known"]);

        Assert.NotNull(fault);
        Assert.Equal("incus-transient-infrastructure", fault!.FaultClass);
        Assert.Equal("something operator-known", fault.MatchedSignature);
    }

    [Theory]
    [InlineData(
        "Incus verify effective VM device topology failed with exit code 1: Error: Failed to begin transaction: context deadline exceeded",
        IncusTransientInfrastructure.BeginTransactionFaultClass)]
    [InlineData(
        "Incus exec completed, but transient guest control-file cleanup could not be verified; the VM was stopped and must be disposed.",
        IncusTransientInfrastructure.ExecCleanupFaultClass)]
    [InlineData(
        "Incus delete sandbox VM failed with exit code 1: Error: Failed unmounting instance: Failed to unmount \"/pool\"",
        IncusTransientInfrastructure.TeardownUnmountFaultClass)]
    public void TryBuildTransientProvisioningDeferral_SignatureErrors_Defer(
        string message,
        string expectedErrorClass)
    {
        var options = new IncusSandboxOptions
        {
            ProvisioningRetryRecheckIn = TimeSpan.FromSeconds(42),
        };

        var deferral = IncusSandboxProvider.TryBuildTransientProvisioningDeferral(
            new InvalidOperationException(message),
            options);

        Assert.NotNull(deferral);
        Assert.True(SandboxDeferralGuard.IsDeferral(deferral!));
        Assert.Equal(IncusSandboxProvider.ProviderId, deferral!.Provider);
        Assert.Equal(expectedErrorClass, deferral.ErrorClass);
        Assert.Equal(TimeSpan.FromSeconds(42), deferral.RecheckIn);
        Assert.Contains(message, deferral.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuildTransientProvisioningDeferral_UsesLiveSignatureList()
    {
        var options = new IncusSandboxOptions
        {
            TransientInfrastructureSignatures = ["operator-known-flake"],
        };

        Assert.Null(IncusSandboxProvider.TryBuildTransientProvisioningDeferral(
            new InvalidOperationException("Error: Failed to begin transaction: context deadline exceeded"),
            options));

        var deferral = IncusSandboxProvider.TryBuildTransientProvisioningDeferral(
            new InvalidOperationException("Error: operator-known-flake during start"),
            options);

        Assert.NotNull(deferral);
    }

    [Fact]
    public void TryBuildTransientProvisioningDeferral_UnrelatedError_StaysTerminal()
    {
        var options = new IncusSandboxOptions();

        Assert.Null(IncusSandboxProvider.TryBuildTransientProvisioningDeferral(
            new InvalidOperationException("Incus start VM failed with exit code 1: Error: instance already exists"),
            options));
    }

    [Fact]
    public void OptionsValidate_DefaultSignatures_Pass()
    {
        Assert.Empty(IncusSandboxOptions.Validate(new IncusSandboxOptions()));
    }

    [Fact]
    public void OptionsValidate_RejectsBadSignatureLists()
    {
        Assert.Contains(
            IncusSandboxOptions.Validate(new IncusSandboxOptions { TransientInfrastructureSignatures = null! }),
            error => error.Contains("TransientInfrastructureSignatures", StringComparison.Ordinal));
        Assert.Contains(
            IncusSandboxOptions.Validate(new IncusSandboxOptions { TransientInfrastructureSignatures = [""] }),
            error => error.Contains("TransientInfrastructureSignatures", StringComparison.Ordinal));
        Assert.Contains(
            IncusSandboxOptions.Validate(new IncusSandboxOptions
            {
                TransientInfrastructureSignatures = [new string('x', IncusSandboxOptions.MaximumTransientInfrastructureSignatureUtf8Bytes + 1)],
            }),
            error => error.Contains("TransientInfrastructureSignatures", StringComparison.Ordinal));
        var tooMany = Enumerable.Range(0, IncusSandboxOptions.MaximumTransientInfrastructureSignatures + 1)
            .Select(index => $"signature-{index}")
            .ToArray();
        Assert.Contains(
            IncusSandboxOptions.Validate(new IncusSandboxOptions { TransientInfrastructureSignatures = tooMany }),
            error => error.Contains("TransientInfrastructureSignatures", StringComparison.Ordinal));
    }

    [Fact]
    public void MapperSnapshot_NullSignatures_YieldsDefaults()
    {
        var mapped = IncusSandboxConfigMapper.SnapshotTransientInfrastructureSignatures(null);

        Assert.Equal(IncusTransientInfrastructure.DefaultSignatures, mapped);
    }

    [Fact]
    public void MapperSnapshot_CopiesSignatures()
    {
        var configured = new List<string> { "operator-known-flake" };

        var mapped = IncusSandboxConfigMapper.SnapshotTransientInfrastructureSignatures(configured);
        configured[0] = "mutated";

        Assert.Equal(["operator-known-flake"], mapped);
    }

    [Fact]
    public void MapperSnapshot_RejectsBadEntries()
    {
        Assert.Throws<InvalidOperationException>(() =>
            IncusSandboxConfigMapper.SnapshotTransientInfrastructureSignatures(["ok", null!]));
        Assert.Throws<InvalidOperationException>(() =>
            IncusSandboxConfigMapper.SnapshotTransientInfrastructureSignatures(["   "]));
        var tooMany = Enumerable.Range(0, IncusSandboxOptions.MaximumTransientInfrastructureSignatures + 1)
            .Select(index => $"signature-{index}")
            .ToList();
        Assert.Throws<InvalidOperationException>(() =>
            IncusSandboxConfigMapper.SnapshotTransientInfrastructureSignatures(tooMany));
    }

    [Fact]
    public async Task SignatureDeferral_RequeuesWithAttemptsIncremented_UntilBounded()
    {
        var maxRetries = 2;
        using var fixture = BuildScheduler(new AutoRetryOnTransientFailureOptions
        {
            Enabled = true,
            BaseDelay = TimeSpan.FromSeconds(30),
            MaxDelay = TimeSpan.FromMinutes(15),
            Multiplier = 2,
            MaxAutoRetriesPerWorkItem = maxRetries,
            MaxElapsedTime = TimeSpan.FromHours(1),
            JitterMode = TransientRetryJitterMode.None,
        });
        var deferral = IncusSandboxProvider.TryBuildTransientProvisioningDeferral(
            new InvalidOperationException(
                "Incus verify effective VM device topology failed with exit code 1: Error: Failed to begin transaction: context deadline exceeded"),
            new IncusSandboxOptions());
        Assert.NotNull(deferral);
        var item = NewTransientItem() with
        {
            LastError = deferral!.Message,
            NextTransientRetryAt = null,
            TransientRetryAttempts = 0,
            TransientRetryFirstFailedAt = null,
        };
        await fixture.Store.CreateAsync(item);

        var scheduled = await fixture.Scheduler.NotifyTransientFailureAsync(item);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemAutoRetryScheduleStatus.Scheduled, scheduled.Status);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, stored!.State);

        _time.Advance(TimeSpan.FromMinutes(1));
        await RunTransientPeriodicSweepAsync(fixture.Scheduler);

        var requeued = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(requeued);
        Assert.Equal(WorkItemState.Queued, requeued!.State);
        Assert.Equal(1, requeued.TransientRetryAttempts);

        var failedAgain = requeued.With(
            WorkItemState.WaitingForTransientRetry,
            deferral.Message,
            failureKind: "transient");
        await fixture.Store.UpdateAsync(failedAgain);
        await fixture.Scheduler.NotifyTransientFailureAsync(failedAgain);
        _time.Advance(TimeSpan.FromMinutes(2));
        await RunTransientPeriodicSweepAsync(fixture.Scheduler);

        var second = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(second);
        Assert.Equal(WorkItemState.Queued, second!.State);
        Assert.Equal(2, second.TransientRetryAttempts);

        var failedAtCap = second.With(
            WorkItemState.WaitingForTransientRetry,
            deferral.Message,
            failureKind: "transient");
        await fixture.Store.UpdateAsync(failedAtCap);
        await fixture.Scheduler.NotifyTransientFailureAsync(failedAtCap);

        var exhausted = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(exhausted);
        Assert.Equal("transient-exhausted", exhausted!.FailureKind);
        Assert.Equal(maxRetries, exhausted.TransientRetryAttempts);
        Assert.Contains($"max={maxRetries}", exhausted.LastError, StringComparison.Ordinal);
    }

    private SchedulerFixture BuildScheduler(AutoRetryOnTransientFailureOptions transientOptions)
    {
        var sqliteStore = new SqliteWorkItemStore(Path.Combine(_workspace, $"state-{Guid.NewGuid():N}.db"));
        var queue = new InMemoryTaskQueue();
        var gitHost = new SchedulerTestGitHost();
        var retrier = new WorkItemRetrier(sqliteStore, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("transient-retry"),
            DisplayName = "Transient retry",
            RepositoryUrl = "file:///tmp/transient-retry",
            DefaultAgent = AgentKind.Claude,
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
            TestSupport.CreateTerminalTransition(sqliteStore, webhooks: null, projects),
            projects: projects,
            timeProvider: _time,
            transientRetryOptionsAccessor: () => transientOptions,
            jitterRandom: () => 0.0);
        return new SchedulerFixture(sqliteStore, queue, scheduler);
    }

    private static async Task RunTransientPeriodicSweepAsync(TransientRetryScheduler scheduler)
    {
        var method = typeof(TransientRetryScheduler).GetMethod(
            "RunTransientPeriodicSweepAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(scheduler, [CancellationToken.None])!;
    }

    private static WorkItem NewTransientItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("transient-retry"),
        Title = "Transient retry",
        Prompt = "retry after transient Incus infrastructure failure",
        State = WorkItemState.WaitingForTransientRetry,
        LastError = "Incus verify effective VM device topology failed with exit code 1: Error: Failed to begin transaction: context deadline exceeded",
        FailureKind = "transient",
        PushUpstream = false,
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

    private sealed class SchedulerTestGitHost : IGitHost
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
            => Task.FromResult(false);

        public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
            string repositoryId,
            string baseBranch,
            string workBranch,
            CancellationToken ct = default)
            => Task.FromResult((string.Empty, string.Empty));
    }
}
