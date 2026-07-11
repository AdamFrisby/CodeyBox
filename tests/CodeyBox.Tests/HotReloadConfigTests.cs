using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Multipass;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that the framework configuration stack supports the hot-reload
/// guarantees from <c>docs/configuration.md</c> — operators editing
/// <c>appsettings.json</c> see the change in flight without restarting
/// CodeyBox, except for fields that are open-handle-bound or otherwise
/// unsafe to swap, which are rejected by an <see cref="IValidateOptions{T}"/>.
/// </summary>
public sealed class HotReloadConfigTests
{
    [Fact]
    public async Task ProjectRepository_PicksUpNewProject_AfterOptionsMonitorFires()
    {
        var initial = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig { Id = "alpha", RepositoryUrl = "https://example.com/alpha.git" },
            ],
        };
        var monitor = new StubOptionsMonitor<ProjectsOptions>(initial);
        using var repo = new ProjectRepository(monitor, NullLogger<ProjectRepository>.Instance);

        Assert.NotNull(await repo.GetAsync(new ProjectId("alpha")));
        Assert.Null(await repo.GetAsync(new ProjectId("beta")));

        monitor.Fire(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig { Id = "alpha", RepositoryUrl = "https://example.com/alpha.git" },
                new ProjectConfig { Id = "beta",  RepositoryUrl = "https://example.com/beta.git" },
            ],
        });

        Assert.NotNull(await repo.GetAsync(new ProjectId("beta")));
        var list = await repo.ListAsync();
        Assert.Equal(new[] { "alpha", "beta" }, list.Select(p => p.Id.Value));
    }

    [Fact]
    public async Task ProjectRepository_PicksUpAuditTimeoutChange_AfterOptionsMonitorFires()
    {
        var initial = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/alpha.git",
                    Audit = new ProjectAuditConfig { PerIterationTimeoutMinutes = 10 },
                },
            ],
        };
        var monitor = new StubOptionsMonitor<ProjectsOptions>(initial);
        using var repo = new ProjectRepository(monitor, NullLogger<ProjectRepository>.Instance);

        var before = await repo.GetAsync(new ProjectId("alpha"));
        Assert.Equal(TimeSpan.FromMinutes(10), before!.Audit.PerIterationTimeout);

        monitor.Fire(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/alpha.git",
                    Audit = new ProjectAuditConfig { PerIterationTimeoutMinutes = 25 },
                },
            ],
        });

        var after = await repo.GetAsync(new ProjectId("alpha"));
        Assert.Equal(TimeSpan.FromMinutes(25), after!.Audit.PerIterationTimeout);
    }

    [Fact]
    public async Task PipelineRunner_PinsAuditTimeoutAtPickup_WhenProjectReloadsMidRun()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"codeybox-pickup-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var seed = await TestSupport.CreateSeedRepoAsync(workspace);
            var project = new Project
            {
                Id = new ProjectId("test-project"),
                DisplayName = "Test Project",
                RepositoryUrl = seed,
                DefaultBaseBranch = "main",
                DefaultAgent = AgentKind.Claude,
                Audit = new ProjectAudit
                {
                    MaxIterations = 1,
                    PerIterationTimeout = TimeSpan.FromMinutes(1),
                    AuditTypes = ["scripted"],
                },
            };
            var repo = new MutableProjectRepository(project);
            var auditor = new SlowPassingAuditor(TimeSpan.FromMilliseconds(150));
            using var fixture = TestSupport.BuildPipeline(
                workspace,
                seed,
                auditors: [auditor],
                maxAuditIterations: 1,
                projectRepository: repo);

            var workStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Agent.BeforeWorkAsync = async (_, _, ct) =>
            {
                workStarted.TrySetResult();
                await releaseWork.Task.WaitAsync(ct);
            };
            fixture.Agent.WorkPlan.Enqueue(new FileWrite("feature.txt", "hello\n"));

            var item = new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = new ProjectId("test-project"),
                Title = "hot reload pickup snapshot",
                Prompt = "write a file",
                PushUpstream = false,
            };
            await fixture.Store.CreateAsync(item);

            var runTask = fixture.Pipeline.RunAsync(item, CancellationToken.None);
            await workStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var working = await fixture.Store.GetAsync(item.Id);
            Assert.Equal(WorkItemState.Working, working!.State);

            // A zero-minute timeout would cancel the audit immediately if the
            // runner re-read project config after pickup instead of using the
            // project snapshot captured at the start of RunAsync.
            repo.Set(project with
            {
                Audit = project.Audit with { PerIterationTimeout = TimeSpan.Zero },
            });
            releaseWork.TrySetResult();

            // Generous timeout: this is a behaviour test, not a performance
            // test, and under heavy CI/sandbox load the work+audit phases can
            // take well over the nominal time.
            await runTask.WaitAsync(TimeSpan.FromMinutes(3));

            Assert.True(auditor.Completed);
            var finished = await fixture.Store.GetAsync(item.Id);
            Assert.Equal(WorkItemState.Done, finished!.State);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ProjectRepository_KeepsPriorSnapshot_WhenReloadThrows()
    {
        var initial = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig { Id = "alpha", RepositoryUrl = "https://example.com/alpha.git" },
            ],
        };
        var monitor = new StubOptionsMonitor<ProjectsOptions>(initial);
        using var repo = new ProjectRepository(monitor, NullLogger<ProjectRepository>.Instance);

        // Two entries with the same id — Build throws "Duplicate project id".
        monitor.Fire(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig { Id = "alpha", RepositoryUrl = "https://example.com/a.git" },
                new ProjectConfig { Id = "alpha", RepositoryUrl = "https://example.com/b.git" },
            ],
        });

        var list = await repo.ListAsync();
        Assert.Single(list);
        Assert.Equal("https://example.com/alpha.git", list[0].RepositoryUrl);
    }

    [Fact]
    public void ImmutableCodeyBoxOptionsValidator_RejectsStateDatabasePathChange()
    {
        var startup = new CodeyBoxOptions { StateDatabasePath = "/var/lib/codeybox/state.db" };
        var validator = new ImmutableCodeyBoxOptionsValidator(startup);

        var candidate = new CodeyBoxOptions { StateDatabasePath = "/tmp/different.db" };
        var result = validator.Validate(name: null, candidate);

        Assert.True(result.Failed);
        Assert.Contains("StateDatabasePath", result.FailureMessage);
    }

    [Fact]
    public void CodeyBoxOptionsMonitor_KeepsStartupValue_AfterRejectedStateDatabasePathReload()
    {
        var startupPath = Path.Combine(Path.GetTempPath(), $"codeybox-startup-{Guid.NewGuid():N}.db");
        var rejectedPath = Path.Combine(Path.GetTempPath(), $"codeybox-rejected-{Guid.NewGuid():N}.db");
        var values = new Dictionary<string, string?>
        {
            ["CodeyBox:SandboxProvider"] = "multipass",
            ["CodeyBox:StateDatabasePath"] = startupPath,
            ["CodeyBox:GitRootDirectory"] = Path.Combine(Path.GetTempPath(), "codeybox-repos"),
            ["CodeyBox:AgentStreams:Path"] = Path.Combine(Path.GetTempPath(), "codeybox-agent-streams"),
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var startup = config.GetSection("CodeyBox").Get<CodeyBoxOptions>() ?? new CodeyBoxOptions();

        var services = new ServiceCollection();
        services.Configure<CodeyBoxOptions>(config.GetSection("CodeyBox"));
        services.AddSingleton<IOptionsMonitorCache<CodeyBoxOptions>>(
            _ => new RetainingOptionsMonitorCache<CodeyBoxOptions>(startup));
        services.AddSingleton<IValidateOptions<CodeyBoxOptions>>(
            _ => new ImmutableCodeyBoxOptionsValidator(startup));
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>();

        Assert.Equal(startupPath, monitor.CurrentValue.StateDatabasePath);

        config["CodeyBox:StateDatabasePath"] = rejectedPath;
        _ = Record.Exception(((IConfigurationRoot)config).Reload);

        Assert.Equal(startupPath, monitor.CurrentValue.StateDatabasePath);
    }

    [Fact]
    public void ImmutableCodeyBoxOptionsValidator_RejectsSandboxProviderChange()
    {
        var startup = new CodeyBoxOptions { SandboxProvider = "multipass" };
        var validator = new ImmutableCodeyBoxOptionsValidator(startup);

        var candidate = new CodeyBoxOptions { SandboxProvider = "bubblewrap" };
        var result = validator.Validate(name: null, candidate);

        Assert.True(result.Failed);
        Assert.Contains("SandboxProvider", result.FailureMessage);
    }

    [Fact]
    public void MultipassStartup_IgnoresInvalidDormantIncusOperationalConfig()
    {
        var options = new CodeyBoxOptions
        {
            SandboxProvider = "multipass",
            Incus = new IncusSandboxConfig
            {
                BaselineNamePrefix = "unsafe/path",
                StagingDirectory = "\0invalid-staging-path",
            },
        };

        var operational = new CodeyBoxOptionsValidator().Validate(name: null, options);
        var immutable = new ImmutableCodeyBoxOptionsValidator(options)
            .Validate(name: null, options);

        Assert.True(operational.Succeeded);
        Assert.True(immutable.Succeeded);
    }

    [Fact]
    public void ImmutableCodeyBoxOptionsValidator_RejectsGitRootDirectoryChange()
    {
        var startup = new CodeyBoxOptions { GitRootDirectory = "/var/lib/codeybox/repos" };
        var validator = new ImmutableCodeyBoxOptionsValidator(startup);

        var candidate = new CodeyBoxOptions { GitRootDirectory = "/tmp/different-repos" };
        var result = validator.Validate(name: null, candidate);

        Assert.True(result.Failed);
        Assert.Contains("GitRootDirectory", result.FailureMessage);
    }

    [Fact]
    public void ImmutableCodeyBoxOptionsValidator_RejectsAgentStreamsPathChange()
    {
        var startup = new CodeyBoxOptions();
        startup.AgentStreams.Path = "logs/agents";
        var validator = new ImmutableCodeyBoxOptionsValidator(startup);

        var candidate = new CodeyBoxOptions();
        candidate.AgentStreams.Path = "logs/agents-relocated";
        var result = validator.Validate(name: null, candidate);

        Assert.True(result.Failed);
        Assert.Contains("AgentStreams:Path", result.FailureMessage);
    }

    [Fact]
    public void ImmutableCodeyBoxOptionsValidator_RejectsMaxConcurrentSandboxesChange()
    {
        var startup = new CodeyBoxOptions();
        startup.WorkerPool.MaxConcurrentSandboxes = 3;
        var validator = new ImmutableCodeyBoxOptionsValidator(startup);

        var candidate = new CodeyBoxOptions();
        candidate.WorkerPool.MaxConcurrentSandboxes = 4;
        var result = validator.Validate(name: null, candidate);

        Assert.True(result.Failed);
        Assert.Contains("WorkerPool:MaxConcurrentSandboxes", result.FailureMessage);
    }

    [Fact]
    public void ImmutableCodeyBoxOptionsValidator_RejectsSharedMirrorEnabledChange()
    {
        var startup = new CodeyBoxOptions { EnableSharedUpstreamMirror = false };
        var validator = new ImmutableCodeyBoxOptionsValidator(startup);

        var candidate = new CodeyBoxOptions { EnableSharedUpstreamMirror = true };
        var result = validator.Validate(name: null, candidate);

        Assert.True(result.Failed);
        Assert.Contains("EnableSharedUpstreamMirror", result.FailureMessage);
    }

    [Fact]
    public void ImmutableCodeyBoxOptionsValidator_RejectsSharedMirrorDirectoryChange()
    {
        var startup = new CodeyBoxOptions { SharedUpstreamMirrorDirectory = "/var/lib/codeybox/mirrors" };
        var validator = new ImmutableCodeyBoxOptionsValidator(startup);

        var candidate = new CodeyBoxOptions { SharedUpstreamMirrorDirectory = "/tmp/codeybox-mirrors" };
        var result = validator.Validate(name: null, candidate);

        Assert.True(result.Failed);
        Assert.Contains("SharedUpstreamMirrorDirectory", result.FailureMessage);
    }

    [Fact]
    public void ImmutableCodeyBoxOptionsValidator_PassesWhenAllImmutableFieldsMatch()
    {
        var startup = new CodeyBoxOptions
        {
            SandboxProvider = "multipass",
            StateDatabasePath = "/var/lib/codeybox/state.db",
            GitRootDirectory = "/var/lib/codeybox/repos",
        };
        startup.AgentStreams.Path = "logs/agents";
        var validator = new ImmutableCodeyBoxOptionsValidator(startup);

        // Same immutable fields, different mutable field (audit log retention).
        var candidate = new CodeyBoxOptions
        {
            SandboxProvider = "multipass",
            StateDatabasePath = "/var/lib/codeybox/state.db",
            GitRootDirectory = "/var/lib/codeybox/repos",
        };
        candidate.AgentStreams.Path = "logs/agents";
        candidate.AuditLog.RetainedDays = 60; // unrelated change

        var result = validator.Validate(name: null, candidate);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void CodeyBoxOptions_BindsMultipassSandboxCloudInitRetryAttempts()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:MultipassSandbox:CloudInitReadyRetryAttempts"] = "5",
            })
            .Build();

        var options = config.GetSection("CodeyBox").Get<CodeyBoxOptions>();

        Assert.NotNull(options);
        Assert.Equal(5, options.MultipassSandbox.CloudInitReadyRetryAttempts);
    }

    [Fact]
    public void ProjectsOptionsRemovalValidator_RejectsRemovalWithInFlightItem()
    {
        var store = new InMemoryWorkItemStore();
        store.Add(NewItem("wi-1", "alpha", WorkItemState.Working));
        store.Add(NewItem("wi-2", "beta", WorkItemState.Done)); // terminal — beta is removable

        var validator = new ProjectsOptionsRemovalValidator(store);

        // alpha is missing from the candidate list — must reject.
        var candidate = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig { Id = "beta", RepositoryUrl = "https://example.com/beta.git" },
            ],
        };
        var result = validator.Validate(name: null, candidate);

        Assert.True(result.Failed);
        Assert.Contains("alpha", result.FailureMessage);
    }

    [Fact]
    public void ProjectsOptionsRemovalValidator_AllowsRemovalWhenOnlyTerminalItemsRemain()
    {
        var store = new InMemoryWorkItemStore();
        store.Add(NewItem("wi-1", "alpha", WorkItemState.Done));
        store.Add(NewItem("wi-2", "alpha", WorkItemState.Cancelled));

        var validator = new ProjectsOptionsRemovalValidator(store);

        // alpha is missing — but all its items are terminal, so the removal is allowed.
        var candidate = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig { Id = "beta", RepositoryUrl = "https://example.com/beta.git" },
            ],
        };
        var result = validator.Validate(name: null, candidate);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ProjectsOptionsRemovalValidator_AllowsAddingNewProject()
    {
        var store = new InMemoryWorkItemStore();
        store.Add(NewItem("wi-1", "alpha", WorkItemState.Working));

        var validator = new ProjectsOptionsRemovalValidator(store);

        // alpha is still there, plus a fresh beta — no removal, additions are free.
        var candidate = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig { Id = "alpha", RepositoryUrl = "https://example.com/alpha.git" },
                new ProjectConfig { Id = "beta",  RepositoryUrl = "https://example.com/beta.git" },
            ],
        };
        var result = validator.Validate(name: null, candidate);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ProjectsOptionsRemovalValidator_RejectsRemovalWithQueuedItem()
    {
        var store = new InMemoryWorkItemStore();
        store.Add(NewItem("wi-1", "alpha", WorkItemState.Queued));

        var validator = new ProjectsOptionsRemovalValidator(store);

        var candidate = new ProjectsOptions(); // remove everything
        var result = validator.Validate(name: null, candidate);

        Assert.True(result.Failed);
        Assert.Contains("alpha", result.FailureMessage);
    }

    [Fact]
    public async Task DeadWorkerReaper_FuncAccessor_ReadsLatestValueOnEachSweep()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-hotreload-reaper-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new SqliteWorkItemStore(dbPath);
            using var registry = new SqliteWorkerRegistry(dbPath);
            var queue = new InMemoryTaskQueue();

            // Item just barely over the initial cap (attempts = 2 + 1 = 3 > MaxRecoveryAttempts=2)
            // but under the bumped cap (3 <= 10). With the latest accessor, the reaper sees the
            // bumped cap on the next sweep and recovers the item instead of failing it.
            var item = new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = new ProjectId("test"),
                Title = "t",
                Prompt = "p",
                State = WorkItemState.Reworking,
                RecoveryAttempts = 2,
            };
            await store.CreateAsync(item);
            await registry.RegisterAsync(new WorkerRegistration
            {
                WorkerId = Guid.NewGuid().ToString(),
                HostName = "h",
                ProcessId = 1,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                LastHeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                CurrentWorkItemId = item.Id.ToString(),
            });

            var opts = new DeadWorkerOptions
            {
                HeartbeatInterval = TimeSpan.FromSeconds(5),
                DeadWorkerThreshold = TimeSpan.FromSeconds(15),
                CheckInterval = TimeSpan.FromMinutes(60),
                MaxRecoveryAttempts = 10, // bumped before sweep — accessor reads this fresh
            };
            var reaper = new DeadWorkerReaper(
                registry, store, queue, () => opts,
                NullLogger<DeadWorkerReaper>.Instance);

            await reaper.RunOnceAsync(CancellationToken.None);

            var after = await store.GetAsync(item.Id);
            Assert.NotNull(after);
            Assert.NotEqual(WorkItemState.Failed, after.State);
            Assert.Equal(WorkItemState.WorkComplete, after.State); // Reworking -> WorkComplete
            Assert.Equal(3, after.RecoveryAttempts);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public void MultipassSandboxProvider_FuncAccessor_IsInvokedForLaunchOptionsRead()
    {
        var calls = 0;
        var current = MultipassOptions(defaultImage: "ubuntu-v1", bridge: "br-v1");
        var provider = new MultipassSandboxProvider(
            () => { calls++; return current; },
            NullLogger<MultipassSandboxProvider>.Instance);

        var afterConstruction = calls;
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy { ProfileName = "egress" },
        };

        var first = provider.BuildLaunchArgv("vm-one", spec, "/tmp/cloud-init.yaml");
        Assert.True(calls > afterConstruction, "launch argv construction should read current options");
        Assert.Contains("ubuntu-v1", first);
        Assert.Contains("name=br-v1,mode=auto", first);

        var beforeSecondRead = calls;
        current = MultipassOptions(defaultImage: "ubuntu-v2", bridge: "br-v2");

        var second = provider.BuildLaunchArgv("vm-two", spec, "/tmp/cloud-init.yaml");
        Assert.True(calls > beforeSecondRead, "subsequent launch argv construction should re-read options");
        Assert.Contains("ubuntu-v2", second);
        Assert.Contains("name=br-v2,mode=auto", second);
    }

    [Fact]
    public async Task ExtraConfigPath_HotReloads_WhenFileEditedWithoutRestart()
    {
        // Smoke test for the framework wiring: a JSON file loaded with
        // reloadOnChange:true must surface its new contents through
        // IOptionsMonitor<T> after a file edit.
        var tempDir = Directory.CreateTempSubdirectory("codeybox-hotreload-");
        var path = Path.Combine(tempDir.FullName, "appsettings.extra.json");
        try
        {
            await WriteHotReloadProjectsConfigAsync(path, includeBeta: false, generation: 0);

            using var trackedConfig = TestFileSystemWatcherLeakTracker.TrackReloadingConfiguration(
                new ConfigurationBuilder()
                    .AddJsonFile(path, optional: false, reloadOnChange: true)
                    .Build(),
                path);
            var config = trackedConfig.Configuration;

            var services = new ServiceCollection();
            services.AddOptions<ProjectsOptions>()
                .Bind(config.GetSection("CodeyBox"))
                .PostConfigure(opts => ProjectsOptionsBinder.ApplyCustomMaps(opts, config.GetSection("CodeyBox")));
            services.AddSingleton<IConfiguration>(config);
            using var provider = services.BuildServiceProvider();
            var monitor = provider.GetRequiredService<IOptionsMonitor<ProjectsOptions>>();

            Assert.Single(monitor.CurrentValue.Projects);
            Assert.Equal("alpha", monitor.CurrentValue.Projects[0].Id);

            var fired = new TaskCompletionSource<ProjectsOptions>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = monitor.OnChange(opts =>
            {
                if (opts.Projects.Count == 2) fired.TrySetResult(opts);
            });

            // FileSystemWatcher can drop an individual event under CI load. Keep
            // making real file edits inside the timeout; the assertion still
            // requires the IOptionsMonitor.OnChange path to observe the reload.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            var generation = 1;
            while (!fired.Task.IsCompleted && DateTime.UtcNow < deadline)
            {
                await WriteHotReloadProjectsConfigAsync(path, includeBeta: true, generation++);

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    break;

                var delay = remaining < TimeSpan.FromMilliseconds(750)
                    ? remaining
                    : TimeSpan.FromMilliseconds(750);
                await Task.WhenAny(fired.Task, Task.Delay(delay));
            }

            Assert.True(
                fired.Task.IsCompletedSuccessfully,
                "ProjectsOptions did not hot-reload from the edited JSON file. " +
                $"Current project ids: {string.Join(", ", monitor.CurrentValue.Projects.Select(p => p.Id))}");
            var reloaded = await fired.Task;
            Assert.Equal(new[] { "alpha", "beta" }, reloaded.Projects.Select(p => p.Id));
        }
        finally
        {
            try { Directory.Delete(tempDir.FullName, recursive: true); } catch { }
        }
    }

    private static async Task WriteHotReloadProjectsConfigAsync(string path, bool includeBeta, int generation)
    {
        var beta = includeBeta
            ? ", { \"Id\": \"beta\", \"RepositoryUrl\": \"https://example.com/beta-" +
              generation.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".git\" }"
            : string.Empty;

        await File.WriteAllTextAsync(path,
            "{ \"CodeyBox\": { \"Projects\": [ " +
            "{ \"Id\": \"alpha\", \"RepositoryUrl\": \"https://example.com/alpha.git\" }" +
            beta +
            " ] } }");

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMilliseconds(generation + 1));
    }

    private static MultipassSandboxOptions MultipassOptions(string defaultImage, string bridge) => new()
    {
        DefaultImage = defaultImage,
        NetworkProfiles = new Dictionary<string, string>
        {
            ["egress"] = bridge,
        },
    };

    private static WorkItem NewItem(string _, string projectId, WorkItemState state) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId(projectId),
        Title = "test",
        Prompt = "test",
        State = state,
    };

    private sealed class SlowPassingAuditor(TimeSpan delay) : IAuditor
    {
        public string Name => "scripted:slow-pass";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public bool Completed { get; private set; }

        public async Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = context;
            await Task.Delay(delay, ct);
            Completed = true;
            return new AuditResult(true, []);
        }
    }

    private sealed class MutableProjectRepository(Project initial) : IProjectRepository
    {
        private Project _current = initial;

        public void Set(Project project) => Volatile.Write(ref _current, project);

        public Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default)
        {
            var current = Volatile.Read(ref _current);
            return Task.FromResult<Project?>(current.Id == id ? current : null);
        }

        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Project>>([Volatile.Read(ref _current)]);
    }

    /// <summary>
    /// Minimal <see cref="IOptionsMonitor{T}"/> stub that lets tests synchronously
    /// publish a new value and run all registered OnChange callbacks before
    /// returning. Avoids spinning up the real configuration pipeline + the
    /// file-watcher debounce in test bodies.
    /// </summary>
    private sealed class StubOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private T _value;
        private readonly List<Action<T, string?>> _listeners = new();
        private readonly Lock _gate = new();

        public StubOptionsMonitor(T initial) { _value = initial; }

        public T CurrentValue => _value;
        public T Get(string? name) => _value;

        public IDisposable OnChange(Action<T, string?> listener)
        {
            lock (_gate) _listeners.Add(listener);
            return new Subscription(() => { lock (_gate) _listeners.Remove(listener); });
        }

        public void Fire(T next)
        {
            _value = next;
            Action<T, string?>[] snapshot;
            lock (_gate) snapshot = _listeners.ToArray();
            foreach (var l in snapshot) l(next, null);
        }

        private sealed class Subscription : IDisposable
        {
            private readonly Action _onDispose;
            public Subscription(Action onDispose) { _onDispose = onDispose; }
            public void Dispose() => _onDispose();
        }
    }

    /// <summary>
    /// Minimal in-memory <see cref="IWorkItemStore"/> used by the validator
    /// tests. Implements only the surface the validator touches
    /// (<c>ListAsync</c>); everything else throws.
    /// </summary>
    private sealed class InMemoryWorkItemStore : IWorkItemStore
    {
        private readonly ConcurrentDictionary<WorkItemId, WorkItem> _items = new();

        public void Add(WorkItem item) => _items[item.Id] = item;

        public async IAsyncEnumerable<WorkItem> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in _items.Values)
            {
                yield return item;
                await Task.Yield();
            }
        }

        public Task CreateAsync(WorkItem item, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateAsync(WorkItem item, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DependsOnUpdateResult> UpdateDependsOnAsync(WorkItemId id, IReadOnlyList<WorkItemId> dependsOn, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AuditBudgetUpdateResult> UpdateAuditBudgetAsync(WorkItemId id, int? auditMaxIterations, string? auditComplexity, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(IReadOnlySet<WorkItemId> skipIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WorkItem?> ReplaceExternalIdsAsync(WorkItemId id, IReadOnlyDictionary<string, string> externalIds, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlySet<string>> GetActiveBaselineImageRefsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<(WorkItemId Id, string Title, WorkItemState State)>> ListWorkItemsForBaselineAsync(string baselineImageRef, CancellationToken ct = default) => throw new NotImplementedException();
        public Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PromptReplaceResult> TryReplacePromptAsync(WorkItemId id, string newPrompt, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RecordIterationDispatchAsync(WorkItemId workItemId, int iteration, int promptRevisionAtDispatch, DateTimeOffset dispatchedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(WorkItemId workItemId, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
