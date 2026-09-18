using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CodeyBox.Agents;
using CodeyBox.Api;
using CodeyBox.Audit;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Upstream;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end coverage for the configurable work-phase timeout:
/// the global default binds from configuration and reloads into subsequently
/// dispatched work, the project override beats the default, the per-item value
/// beats both, the timeout failure message names the budget and its source,
/// and a raised budget on retry is observed by the next run.
/// </summary>
[Collection("Pipeline integration")]
public sealed class WorkTimeoutConfigurationTests : IDisposable
{
    private readonly string _workspace;

    public WorkTimeoutConfigurationTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-worktimeout-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    // ── Global default: binding ──────────────────────────────────────────

    [Fact]
    public void GlobalDefault_Unset_BindsShipped240()
    {
        using var factory = new WorkTimeoutConfigFactory(new Dictionary<string, string?>());

        var options = factory.Services.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;

        Assert.Equal(240, options.DefaultWorkTimeoutMinutes);
    }

    [Fact]
    public void GlobalDefault_Set_BindsFromConfiguration()
    {
        using var factory = new WorkTimeoutConfigFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:DefaultWorkTimeoutMinutes"] = "300",
        });

        var options = factory.Services.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;

        Assert.Equal(300, options.DefaultWorkTimeoutMinutes);
    }

    [Fact]
    public void ReloadClassification_ReportsGlobalDefaultAsHotReload()
    {
        Assert.True(ConfigReloadClassification.TryGetEffect(
            "CodeyBox:DefaultWorkTimeoutMinutes", out var effect));
        Assert.Equal(ConfigReloadEffect.HotReload, effect);
    }

    // ── Global default: reload reaches subsequently dispatched work ──────

    [Fact]
    public async Task Reload_ChangesBudgetInForceForSubsequentlyDispatchedWork()
    {
        // The full options stack (not a test double): the pipeline reads the
        // global default through a live IOptionsMonitor, so a config reload
        // changes what the next dispatch resolves without a restart.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DefaultWorkTimeoutMinutes"] = "300",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOptions<CodeyBoxOptions>().Bind(config.GetSection("CodeyBox"));
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>();
        Assert.Equal(300, monitor.CurrentValue.DefaultWorkTimeoutMinutes);

        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var harness = BuildHarness(
            seed,
            project: TestProject(),
            defaultWorkTimeoutMinutesAccessor: () => monitor.CurrentValue.DefaultWorkTimeoutMinutes);

        var item = NewItem(); // no per-item timeout, no project override → global default
        var (firstBudget, firstSource) = harness.Pipeline.ResolveEffectiveWorkTimeout(item, harness.Project);
        Assert.Equal(TimeSpan.FromMinutes(300), firstBudget);
        Assert.Equal(WorkTimeoutSource.Default, firstSource);

        config["CodeyBox:DefaultWorkTimeoutMinutes"] = "180";
        ((IConfigurationRoot)config).Reload();
        Assert.Equal(180, monitor.CurrentValue.DefaultWorkTimeoutMinutes);

        var (secondBudget, secondSource) = harness.Pipeline.ResolveEffectiveWorkTimeout(item, harness.Project);
        Assert.Equal(TimeSpan.FromMinutes(180), secondBudget);
        Assert.Equal(WorkTimeoutSource.Default, secondSource);
    }

    // ── Precedence at dispatch ───────────────────────────────────────────

    [Fact]
    public async Task Dispatch_ProjectOverride_BeatsGlobalDefault()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var harness = BuildHarness(
            seed,
            project: TestProject() with { WorkTimeoutMinutes = 300 },
            defaultWorkTimeoutMinutesAccessor: () => 240);

        var (budget, source) = harness.Pipeline.ResolveEffectiveWorkTimeout(NewItem(), harness.Project);

        Assert.Equal(TimeSpan.FromMinutes(300), budget);
        Assert.Equal(WorkTimeoutSource.Project, source);
    }

    [Fact]
    public async Task Dispatch_ItemValue_BeatsProjectAndDefault()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var harness = BuildHarness(
            seed,
            project: TestProject() with { WorkTimeoutMinutes = 300 },
            defaultWorkTimeoutMinutesAccessor: () => 240);

        var item = NewItem() with { WorkTimeout = TimeSpan.FromMinutes(60) };
        var (budget, source) = harness.Pipeline.ResolveEffectiveWorkTimeout(item, harness.Project);

        Assert.Equal(TimeSpan.FromMinutes(60), budget);
        Assert.Equal(WorkTimeoutSource.Item, source);
    }

    // ── Project config surface ───────────────────────────────────────────

    [Fact]
    public async Task ProjectRepository_MergesWorkTimeout_ProjectWinsOverDefaults()
    {
        var monitor = new MutableMonitor<ProjectsOptions>(new ProjectsOptions
        {
            Defaults = new ProjectDefaultsConfig { WorkTimeoutMinutes = 200 },
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/alpha.git",
                    WorkTimeoutMinutes = 300,
                },
                new ProjectConfig
                {
                    Id = "beta",
                    RepositoryUrl = "https://example.com/beta.git",
                },
            ],
        });
        using var repo = new ProjectRepository(monitor, NullLogger<ProjectRepository>.Instance);

        Assert.Equal(300, (await repo.GetAsync(new ProjectId("alpha")))! .WorkTimeoutMinutes);
        Assert.Equal(200, (await repo.GetAsync(new ProjectId("beta")))! .WorkTimeoutMinutes);
    }

    [Fact]
    public async Task ProjectRepository_PicksUpWorkTimeoutChange_AfterOptionsMonitorFires()
    {
        var monitor = new MutableMonitor<ProjectsOptions>(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/alpha.git",
                    WorkTimeoutMinutes = 300,
                },
            ],
        });
        using var repo = new ProjectRepository(monitor, NullLogger<ProjectRepository>.Instance);
        Assert.Equal(300, (await repo.GetAsync(new ProjectId("alpha")))! .WorkTimeoutMinutes);

        monitor.Fire(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/alpha.git",
                    WorkTimeoutMinutes = 120,
                },
            ],
        });

        Assert.Equal(120, (await repo.GetAsync(new ProjectId("alpha")))! .WorkTimeoutMinutes);
    }

    // ── Timeout failure message ──────────────────────────────────────────

    [Fact]
    public async Task WorkTimeoutFailure_MessageNamesBudgetSourceAndRemedy()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var harness = BuildHarness(seed, TestProject(), () => null);

        var item = NewItem() with { WorkTimeout = TimeSpan.FromMilliseconds(250) };
        await harness.Store.CreateAsync(item);
        harness.Agent.Behaviour = BlockForever;

        await harness.Pipeline.RunAsync(item, CancellationToken.None, CancellationToken.None);

        var after = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Failed, after!.State);
        Assert.Equal("timeout", after.FailureKind);
        Assert.Equal(CancellationSources.PhaseTimeout("work"), after.CancellationSource);
        Assert.NotNull(after.LastError);
        Assert.Contains("phase 'work'", after.LastError);
        Assert.Contains("250 ms", after.LastError);
        Assert.Contains("per-item", after.LastError);
        Assert.Contains("workTimeoutMinutes", after.LastError);
    }

    // ── Retry with a raised budget: the next run observes it ─────────────

    [Fact]
    public async Task RetryWithRaisedBudget_NextRunObservesNewBudget()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var harness = BuildHarness(seed, TestProject(), () => null);

        // Control leg: the old 250 ms budget genuinely fires, so the second
        // leg cannot pass vacuously.
        var item = NewItem() with { WorkTimeout = TimeSpan.FromMilliseconds(250) };
        await harness.Store.CreateAsync(item);
        harness.Agent.Behaviour = BlockForever;
        await harness.Pipeline.RunAsync(item, CancellationToken.None, CancellationToken.None);

        var failed = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(failed);
        Assert.Equal(WorkItemState.Failed, failed!.State);
        Assert.Equal("timeout", failed.FailureKind);

        // The retry stamps the new budget in the same atomic conditional write
        // as the Queued transition: the synchronous read below already sees it,
        // so no pickup race can make this pass spuriously.
        var retrier = new WorkItemRetrier(
            harness.Store,
            harness.Queue,
            harness.GitHost,
            NullLogger<WorkItemRetrier>.Instance);
        var (success, error, _, _, _) = await retrier.RetryAsync(
            failed, "work", "manual", CancellationToken.None, workTimeoutMinutes: 480);
        Assert.True(success, error);

        var retried = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(retried);
        Assert.Equal(WorkItemState.Queued, retried!.State);
        Assert.Equal(TimeSpan.FromMinutes(480), retried.WorkTimeout);

        // An agent turn that outlives the OLD budget by orders of magnitude now
        // completes the work phase: the next run observed the new budget. The
        // fake agent writes a file through the sandbox (the pipeline stages
        // and commits the work tree itself) so the run has a commit to carry
        // forward; without it the run would stop at the no-changes breaker
        // (which itself already proves the turn was no longer timed out).
        harness.Agent.Behaviour = async (sandbox, dir, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            var write = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0/change.txt\"", dir],
                Stdin = "x",
            }, ct);
            if (!write.Success)
                throw new InvalidOperationException($"fake agent could not write change.txt: {write.Stderr}");
            return new AgentResult(true, "ok", null, null);
        };
        await harness.Pipeline.RunAsync(retried, CancellationToken.None, CancellationToken.None);

        var after = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.NotEqual("timeout", after!.FailureKind);
        Assert.NotEqual(CancellationSources.PhaseTimeout("work"), after.CancellationSource);
        Assert.True(
            after.State is WorkItemState.WorkComplete or WorkItemState.Auditing
                or WorkItemState.AuditPassed or WorkItemState.Merged or WorkItemState.Done,
            $"expected the run to advance past the work phase, but state is {after.State} (error: {after.LastError})");
    }

    // ── Stall detection stays independent of the budget ──────────────────

    [Fact]
    public async Task LongBudget_DoesNotWeakenStallDetection()
    {
        // A wedged agent must still be caught by the frozen-item probe when the
        // phase budget is at its maximum — stall detection never leans on the
        // work timeout.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var harness = BuildHarness(
            seed,
            TestProject() with
            {
                Audit = new ProjectAudit
                {
                    MaxIterations = 1,
                    StuckThresholdMinutes = 1,
                },
            },
            () => null,
            stuckProbe: true);

        var item = NewItem() with { WorkTimeout = TimeSpan.FromMinutes(480) };
        await harness.Store.CreateAsync(item);
        harness.Agent.Behaviour = BlockForever;

        await harness.Pipeline.RunAsync(item, CancellationToken.None, CancellationToken.None);

        var after = await harness.Store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Failed, after!.State);
        Assert.Contains("stuck", after.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("timeout", after.FailureKind);
        Assert.NotEqual(CancellationSources.PhaseTimeout("work"), after.CancellationSource);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static Project TestProject() => new()
    {
        Id = new ProjectId("test-project"),
        DisplayName = "Test Project",
        RepositoryUrl = "https://example.com/seed.git",
        DefaultBaseBranch = "main",
        DefaultAgent = AgentKind.Claude,
        Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
    };

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
        PushUpstream = false,
    };

    private static async Task<AgentResult> BlockForever(ISandbox sandbox, string dir, CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return new AgentResult(false, "unreachable", null, null);
    }

    private WorkTimeoutHarness BuildHarness(
        string seedRepoUrl,
        Project project,
        Func<int?> defaultWorkTimeoutMinutesAccessor,
        bool stuckProbe = false)
    {
        // Refresh the repository URL to the throwaway seed repo.
        project = project with { RepositoryUrl = seedRepoUrl };

        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var agent = new ControllableAgent();
        var registry = new AgentRegistry([agent]);
        var queue = new RecordingTaskQueue();
        var projects = new InMemoryProjectRepository(project);
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog([]));
        var webhooks = new NullWebhookDispatcher();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer, store,
            webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            taskQueue: queue,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions,
            defaultWorkTimeoutMinutesAccessor: defaultWorkTimeoutMinutesAccessor);

        if (stuckProbe)
        {
            pipeline.ActivitySourceFactory = () => new ZeroActivitySource();
            pipeline.StuckProbePollInterval = TimeSpan.FromMilliseconds(1);
        }

        return new WorkTimeoutHarness(pipeline, store, gitHost, agent, queue, project);
    }

    private sealed class ControllableAgent : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public Func<ISandbox, string, CancellationToken, Task<AgentResult>> Behaviour { get; set; } =
            (_, _, _) => Task.FromResult(new AgentResult(true, "ok", null, null));

        public Task<AgentResult> RunAsync(
            ISandbox sandbox, string workingDirectory, string prompt,
            AgentCredential? credential, string? modelId = null, string? reasoningMode = null,
            CancellationToken ct = default, Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
            => Behaviour(sandbox, workingDirectory, ct);
    }

    private sealed class ZeroActivitySource : IAgentActivitySource
    {
        public ActivitySample? TryRead() => new ActivitySample(0, 0);
    }

    private sealed class WorkTimeoutHarness : IDisposable
    {
        public PipelineRunner Pipeline { get; }
        public SqliteWorkItemStore Store { get; }
        public LocalGitHost GitHost { get; }
        public ControllableAgent Agent { get; }
        public RecordingTaskQueue Queue { get; }
        public Project Project { get; }

        public WorkTimeoutHarness(
            PipelineRunner pipeline, SqliteWorkItemStore store, LocalGitHost gitHost,
            ControllableAgent agent, RecordingTaskQueue queue, Project project)
        {
            Pipeline = pipeline;
            Store = store;
            GitHost = gitHost;
            Agent = agent;
            Queue = queue;
            Project = project;
        }

        public void Dispose() => Store.Dispose();
    }

    private sealed class MutableMonitor<T>(T initial) : IOptionsMonitor<T>
    {
        private T _value = initial;
        private readonly List<Action<T, string?>> _listeners = [];
        private readonly Lock _gate = new();

        public T CurrentValue => _value;
        public T Get(string? name) => _value;

        public IDisposable OnChange(Action<T, string?> listener)
        {
            lock (_gate) _listeners.Add(listener);
            return new Subscription(() => { lock (_gate) _listeners.Remove(listener); });
        }

        public void Fire(T value)
        {
            _value = value;
            Action<T, string?>[] snapshot;
            lock (_gate) snapshot = _listeners.ToArray();
            foreach (var listener in snapshot) listener(value, null);
        }

        private sealed class Subscription(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }

    private sealed class WorkTimeoutConfigFactory : WebApplicationFactory<Program>
    {
        private readonly Dictionary<string, string?> _extraConfig;
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-worktimeout-cfg-{Guid.NewGuid():N}.db");

        public WorkTimeoutConfigFactory(Dictionary<string, string?> extraConfig)
        {
            _extraConfig = extraConfig;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var tmp = Path.GetTempPath();
                var baseConfig = new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                };
                foreach (var kvp in _extraConfig)
                    baseConfig[kvp.Key] = kvp.Value;
                cfg.AddInMemoryCollection(baseConfig);
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IProjectRepository>();
                services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
    }
}
