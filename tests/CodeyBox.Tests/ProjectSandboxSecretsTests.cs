using System.Text.Json;
using CodeyBox.Api;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Incus;
using CodeyBox.Sandbox.Multipass;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for project-scoped sandbox test secrets: declaration by
/// reference, explicit per-phase scope, separation from the agent-credential
/// channel, names-only audit listing, and no leakage into instance
/// configuration, cloud-init, or logs.
/// </summary>
[Collection("Pipeline integration")]
public sealed class ProjectSandboxSecretsTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory(
        "codeybox-project-secrets-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    private static string UniqueEnvName(string prefix)
        => $"{prefix}_{Guid.NewGuid().ToString("N")[..12].ToUpperInvariant()}";

    private sealed class HostEnvScope : IDisposable
    {
        private readonly string _name;
        public HostEnvScope(string name, string value)
        {
            _name = name;
            Environment.SetEnvironmentVariable(name, value);
        }
        public void Dispose() => Environment.SetEnvironmentVariable(_name, null);
    }

    private static WorkItem NewItem(string branch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "project secrets",
        Prompt = "do work",
        Agent = AgentKind.Claude,
        WorkBranch = branch,
    };

    private Project TestProject(
        string seed,
        IReadOnlyList<ProjectSandboxSecret>? secrets = null,
        ProjectAudit? audit = null) => new()
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            NetworkProfiles = new ProjectNetworkProfiles(),
            Upstream = ProjectUpstream.Noop,
            Audit = audit ?? new ProjectAudit { MaxIterations = 1, AuditTypes = ["scripted"] },
            SandboxSecrets = secrets ?? [],
        };

    // ── Scope matching ────────────────────────────────────────────────────

    [Fact]
    public void SecretDefaultsToWorkAndReworkScopes()
    {
        var secret = new ProjectSandboxSecret
        {
            HostEnvVar = "HOST_KEY",
            SandboxEnvVar = "SANDBOX_KEY",
        };

        Assert.True(secret.AppliesTo("work"));
        Assert.True(secret.AppliesTo("rework"));
        Assert.False(secret.AppliesTo("audit-agent"));
        Assert.False(secret.AppliesTo("audit-tool"));
        Assert.False(secret.AppliesTo("merge"));
    }

    [Fact]
    public void AuditScopeAliasCoversBothAuditSandboxKinds()
    {
        var secret = new ProjectSandboxSecret
        {
            HostEnvVar = "HOST_KEY",
            SandboxEnvVar = "SANDBOX_KEY",
            Scopes = ["audit"],
        };

        Assert.True(secret.AppliesTo("audit-agent"));
        Assert.True(secret.AppliesTo("audit-tool"));
        Assert.False(secret.AppliesTo("work"));
        Assert.False(secret.AppliesTo("merge"));
    }

    [Fact]
    public void DelegationSandboxReceivesReworkScopedSecrets()
    {
        var secret = new ProjectSandboxSecret
        {
            HostEnvVar = "HOST_KEY",
            SandboxEnvVar = "SANDBOX_KEY",
            Scopes = ["rework"],
        };

        Assert.True(secret.AppliesTo("delegation"));
        Assert.True(secret.AppliesTo("REWORK"));
    }

    // ── Resolver ──────────────────────────────────────────────────────────

    [Fact]
    public void ResolveForScope_InjectsDeclaredSecretUnderConfiguredName()
    {
        var hostName = UniqueEnvName("CODEYBOX_TEST_PS_HOST");
        var project = new Project
        {
            Id = new ProjectId("p"),
            DisplayName = "P",
            RepositoryUrl = "https://example.com/x.git",
            SandboxSecrets =
            [
                new ProjectSandboxSecret { HostEnvVar = hostName, SandboxEnvVar = "OPENROUTER_API_KEY" },
            ],
        };
        using var _ = new HostEnvScope(hostName, "live-test-value");

        var resolved = ProjectSandboxSecretResolver.ResolveForScope(
            project, "work", Environment.GetEnvironmentVariable);

        var pair = Assert.Single(resolved);
        Assert.Equal("OPENROUTER_API_KEY", pair.Key);
        Assert.Equal("live-test-value", pair.Value);
    }

    [Fact]
    public void ResolveForScope_EmptyWhenProjectDeclaresNothing()
    {
        var project = new Project
        {
            Id = new ProjectId("p"),
            DisplayName = "P",
            RepositoryUrl = "https://example.com/x.git",
        };

        var resolved = ProjectSandboxSecretResolver.ResolveForScope(
            project, "work", _ => "must-not-be-read");

        Assert.Empty(resolved);
    }

    [Fact]
    public void ResolveForScope_SkipsUnsetOrEmptyHostVariable()
    {
        var missing = UniqueEnvName("CODEYBOX_TEST_PS_MISSING");
        var empty = UniqueEnvName("CODEYBOX_TEST_PS_EMPTY");
        Environment.SetEnvironmentVariable(missing, null);
        using var _ = new HostEnvScope(empty, string.Empty);
        var project = new Project
        {
            Id = new ProjectId("p"),
            DisplayName = "P",
            RepositoryUrl = "https://example.com/x.git",
            SandboxSecrets =
            [
                new ProjectSandboxSecret { HostEnvVar = missing, SandboxEnvVar = "FIRST_KEY" },
                new ProjectSandboxSecret { HostEnvVar = empty, SandboxEnvVar = "SECOND_KEY" },
            ],
        };

        var resolved = ProjectSandboxSecretResolver.ResolveForScope(
            project, "work", Environment.GetEnvironmentVariable);

        Assert.Empty(resolved);
    }

    [Fact]
    public void ResolveForScope_RejectsReservedSandboxNameAtSink()
    {
        var hostName = UniqueEnvName("CODEYBOX_TEST_PS_HOST");
        var project = new Project
        {
            Id = new ProjectId("p"),
            DisplayName = "P",
            RepositoryUrl = "https://example.com/x.git",
            SandboxSecrets =
            [
                new ProjectSandboxSecret { HostEnvVar = hostName, SandboxEnvVar = "PATH" },
            ],
        };

        Assert.Throws<ArgumentException>(() =>
            ProjectSandboxSecretResolver.ResolveForScope(
                project, "work", _ => "value"));
    }

    [Fact]
    public void ResolveForScope_RejectsNulByteValue()
    {
        var project = new Project
        {
            Id = new ProjectId("p"),
            DisplayName = "P",
            RepositoryUrl = "https://example.com/x.git",
            SandboxSecrets =
            [
                new ProjectSandboxSecret { HostEnvVar = "ANY_HOST", SandboxEnvVar = "TEST_KEY" },
            ],
        };

        Assert.Throws<ArgumentException>(() =>
            ProjectSandboxSecretResolver.ResolveForScope(
                project, "work", _ => "a\0b"));
    }

    // ── Separation from the agent-credential channel ──────────────────────

    [Fact]
    public void AgentCredentialGate_StillRejectsCrossAgentSelection()
    {
        var credential = new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "claude-key" },
            new Dictionary<string, string>());
        var codexRunner = new ScriptedAgent([]) { Kind = new AgentKind("codex") };

        var ex = Assert.Throws<ArgumentException>(() =>
            SandboxEnvironmentVariablePolicy.SelectDirectCredentialEnvironment(
                credential, codexRunner, "credential"));
        Assert.Contains("claude", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectSecret_NeverFlowsThroughAgentCredentialSelection()
    {
        var hostName = UniqueEnvName("CODEYBOX_TEST_PS_HOST");
        using var _ = new HostEnvScope(hostName, "project-secret-value");
        var credential = new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "claude-key" },
            new Dictionary<string, string>());

        var direct = SandboxEnvironmentVariablePolicy.SelectDirectCredentialEnvironment(
            credential, new ScriptedAgent([]), "credential");

        Assert.DoesNotContain("OPENROUTER_API_KEY", direct.Keys);
        Assert.DoesNotContain(hostName, direct.Keys);
    }

    // ── Config load ───────────────────────────────────────────────────────

    [Fact]
    public async Task ConfigLoad_BindsSecretsWithWorkReworkDefault()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    SandboxSecrets =
                    [
                        new ProjectSandboxSecretConfig
                        {
                            HostEnvVar = "CODEYBOX_OPENROUTER_API_KEY",
                            SandboxEnvVar = "OPENROUTER_API_KEY",
                        },
                    ],
                },
            ],
        };

        var repo = new ProjectRepository(Options.Create(opts));
        var project = await repo.GetAsync(new ProjectId("alpha"));

        var secret = Assert.Single(project!.SandboxSecrets);
        Assert.Equal("CODEYBOX_OPENROUTER_API_KEY", secret.HostEnvVar);
        Assert.Equal("OPENROUTER_API_KEY", secret.SandboxEnvVar);
        Assert.Equal(["work", "rework"], secret.Scopes);
    }

    [Fact]
    public async Task ConfigLoad_BindsExplicitPhases()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    SandboxSecrets =
                    [
                        new ProjectSandboxSecretConfig
                        {
                            HostEnvVar = "HOST_KEY",
                            SandboxEnvVar = "SANDBOX_KEY",
                            Phases = ["Audit", "MERGE"],
                        },
                    ],
                },
            ],
        };

        var repo = new ProjectRepository(Options.Create(opts));
        var project = await repo.GetAsync(new ProjectId("alpha"));

        var secret = Assert.Single(project!.SandboxSecrets);
        Assert.Equal(["audit", "merge"], secret.Scopes);
        Assert.True(secret.AppliesTo("audit-tool"));
        Assert.False(secret.AppliesTo("work"));
    }

    [Fact]
    public void ConfigLoad_RejectsLiteralSecretValue()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    SandboxSecrets =
                    [
                        new ProjectSandboxSecretConfig
                        {
                            HostEnvVar = "sk-live-abc123 xyz",
                            SandboxEnvVar = "OPENROUTER_API_KEY",
                        },
                    ],
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new ProjectRepository(Options.Create(opts)));
        Assert.Contains("host environment variable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigLoad_RejectsLiteralSandboxName()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    SandboxSecrets =
                    [
                        new ProjectSandboxSecretConfig
                        {
                            HostEnvVar = "HOST_KEY",
                            SandboxEnvVar = "not a name!",
                        },
                    ],
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new ProjectRepository(Options.Create(opts)));
        Assert.Contains("host environment variable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigLoad_RejectsUnknownPhase()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    SandboxSecrets =
                    [
                        new ProjectSandboxSecretConfig
                        {
                            HostEnvVar = "HOST_KEY",
                            SandboxEnvVar = "SANDBOX_KEY",
                            Phases = ["planetary"],
                        },
                    ],
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new ProjectRepository(Options.Create(opts)));
        Assert.Contains("planetary", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigLoad_RejectsDuplicateSandboxName()
    {
        var opts = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    SandboxSecrets =
                    [
                        new ProjectSandboxSecretConfig { HostEnvVar = "HOST_A", SandboxEnvVar = "SHARED_KEY" },
                        new ProjectSandboxSecretConfig { HostEnvVar = "HOST_B", SandboxEnvVar = "SHARED_KEY" },
                    ],
                },
            ],
        };

        Assert.Throws<InvalidOperationException>(() => new ProjectRepository(Options.Create(opts)));
    }

    // ── BuildSandboxSpec merge order ──────────────────────────────────────

    [Fact]
    public async Task BuildSandboxSpec_InjectsSecretsAndCredentialWinsOnCollision()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var access = new SandboxRepositoryAccess(
            CloneUrlInsideSandbox: "/repo",
            Mounts: [],
            Network: SandboxNetworkPolicy.Denied);
        var credential = new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string> { ["WORK_TOKEN"] = "from-credential" },
            new Dictionary<string, string>());

        var method = typeof(PipelineRunner).GetMethod(
            "BuildSandboxSpec",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(PipelineRunner), "BuildSandboxSpec");
        var spec = Assert.IsType<SandboxSpec>(method.Invoke(
            tp.Pipeline,
            [
                access,
                credential,
                true,
                null,
                null,
                null,
                SandboxProfileFlavor.Headless,
                null,
                null,
                null,
                false,
                false,
                false,
                tp.Agent,
                new Dictionary<string, string>
                {
                    ["WORK_TOKEN"] = "from-project-secret",
                    ["ONLY_SECRET"] = "secret-only",
                },
            ]));

        Assert.Equal("from-credential", spec.Environment["WORK_TOKEN"]);
        Assert.Equal("secret-only", spec.Environment["ONLY_SECRET"]);
    }

    // ── End to end through the real pipeline ──────────────────────────────

    [Fact]
    public async Task WorkSandbox_ReceivesDeclaredSecret_AndGuestProcessCanAuthenticate()
    {
        var hostName = UniqueEnvName("CODEYBOX_TEST_PS_HOST");
        const string SandboxName = "OPENROUTER_API_KEY";
        const string SecretValue = "or-test-key-authenticates";
        using var _ = new HostEnvScope(hostName, SecretValue);

        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var recorder = new RecordingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var projects = new InMemoryProjectRepository(TestProject(seed, secrets:
        [
            new ProjectSandboxSecret { HostEnvVar = hostName, SandboxEnvVar = SandboxName },
        ]));
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassAuditor()],
            sandboxProvider: recorder,
            projectRepository: projects);

        var authenticated = false;
        tp.Agent.BeforeWorkAsync = async (sandbox, _, ct) =>
        {
            var check = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", $"test \"${SandboxName}\" = \"{SecretValue}\" && echo authenticated"],
            }, ct);
            authenticated = check.Success
                && check.Stdout.Contains("authenticated", StringComparison.Ordinal);
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/project-secret-e2e");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.True(authenticated, "guest process could not authenticate with the injected key");
        var workSpec = Assert.Single(recorder.SpecsForPhase("work"));
        Assert.Equal(SecretValue, workSpec.Environment[SandboxName]);
        Assert.All(
            recorder.SpecsForPhase("audit"),
            spec => Assert.DoesNotContain(SandboxName, spec.Environment.Keys));
    }

    [Fact]
    public async Task ProjectWithoutDeclaration_GetsNothingInjected()
    {
        var hostName = UniqueEnvName("CODEYBOX_TEST_PS_HOST");
        using var _ = new HostEnvScope(hostName, "undeclared-value");

        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var recorder = new RecordingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var projects = new InMemoryProjectRepository(TestProject(seed));
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassAuditor()],
            sandboxProvider: recorder,
            projectRepository: projects);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/project-secret-absent");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        var workSpec = Assert.Single(recorder.SpecsForPhase("work"));
        Assert.DoesNotContain("OPENROUTER_API_KEY", workSpec.Environment.Keys);
    }

    [Fact]
    public async Task AuditScopedSecret_ReachesToolAuditSandboxOnlyWhenDeclared()
    {
        var hostName = UniqueEnvName("CODEYBOX_TEST_PS_HOST");
        const string SecretValue = "audit-test-key";
        using var _ = new HostEnvScope(hostName, SecretValue);

        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var recorder = new RecordingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var auditor = new PassAuditor();
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
        };
        var projects = new InMemoryProjectRepository(TestProject(seed,
            audit: audit,
            secrets:
            [
                new ProjectSandboxSecret
                {
                    HostEnvVar = hostName,
                    SandboxEnvVar = "TEST_SERVICE_KEY",
                    Scopes = ["audit-tool"],
                },
            ]));
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            sandboxProvider: recorder,
            projectRepository: projects);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/project-secret-audit");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        var workSpec = Assert.Single(recorder.SpecsForPhase("work"));
        Assert.DoesNotContain("TEST_SERVICE_KEY", workSpec.Environment.Keys);
        var auditSpec = Assert.Single(recorder.SpecsForPhase("audit"));
        Assert.Equal(SecretValue, auditSpec.Environment["TEST_SERVICE_KEY"]);
    }

    // ── Audit listing shows names, never values ───────────────────────────

    [Fact]
    public void AuditListing_Describe_ExposesNamesButNeverValues()
    {
        var project = new Project
        {
            Id = new ProjectId("p"),
            DisplayName = "P",
            RepositoryUrl = "https://example.com/x.git",
            SandboxSecrets =
            [
                new ProjectSandboxSecret
                {
                    HostEnvVar = "HOST_KEY",
                    SandboxEnvVar = "SANDBOX_KEY",
                    Scopes = ["work"],
                },
            ],
        };

        var described = ProjectSandboxSecretResolver.Describe(project);
        var entry = Assert.Single(described);
        Assert.Equal("HOST_KEY", entry.HostEnvVar);
        Assert.Equal("SANDBOX_KEY", entry.SandboxEnvVar);

        var dto = new ProjectSandboxSecretDto(entry.HostEnvVar, entry.SandboxEnvVar, entry.Scopes);
        var json = JsonSerializer.Serialize(dto);
        Assert.Contains("HOST_KEY", json, StringComparison.Ordinal);
        Assert.Contains("SANDBOX_KEY", json, StringComparison.Ordinal);
        Assert.DoesNotContain("live-value", json, StringComparison.Ordinal);
        Assert.False(
            typeof(ProjectSandboxSecretDto).GetProperties().Any(p =>
                p.Name.Contains("value", StringComparison.OrdinalIgnoreCase)),
            "audit DTO must not gain a value-carrying property");
    }

    // ── No leakage into instance config, cloud-init, or logs ──────────────

    [Fact]
    public void SecretValue_AbsentFromCloudInitRenderings()
    {
        const string SecretValue = "or-cloud-init-must-not-contain-9f2c";
        var multipassInit = MultipassSandboxProvider.BuildCloudInit(null, null);
        Assert.DoesNotContain(SecretValue, multipassInit, StringComparison.Ordinal);

        var incusOptions = new IncusSandboxOptions
        {
            ProjectName = "test",
            StoragePoolName = "test",
        };
        var incusInit = IncusCloudInit.Build(incusOptions, SandboxProfileFlavor.Headless);
        Assert.DoesNotContain(SecretValue, incusInit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretValue_AbsentFromHostLogsDuringPipelineRun()
    {
        var hostName = UniqueEnvName("CODEYBOX_TEST_PS_HOST");
        var secretValue = $"or-log-must-not-contain-{Guid.NewGuid():N}";
        using var _ = new HostEnvScope(hostName, secretValue);

        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var recorder = new RecordingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var projects = new InMemoryProjectRepository(TestProject(seed, secrets:
        [
            new ProjectSandboxSecret { HostEnvVar = hostName, SandboxEnvVar = "OPENROUTER_API_KEY" },
        ]));
        var logs = new ListLogger<PipelineRunner>();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassAuditor()],
            sandboxProvider: recorder,
            projectRepository: projects,
            logger: logs);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/project-secret-logs");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.DoesNotContain(logs.Lines, line =>
            line.Message.Contains(secretValue, StringComparison.Ordinal));
    }

    private sealed class PassAuditor : IAuditor
    {
        public string Name => "pass:tool";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
            => Task.FromResult(new AuditResult(true, []));
    }

    private sealed class RecordingSandboxProvider(ISandboxProvider inner) : ISandboxProvider
    {
        private readonly List<SandboxSpec> _specs = new();
        public string Name => inner.Name;
        public IReadOnlyList<SandboxSpec> Specs
        {
            get { lock (_specs) return _specs.ToList(); }
        }
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            lock (_specs) _specs.Add(spec);
            return inner.CreateAsync(spec, ct);
        }
        public IReadOnlyList<SandboxSpec> SpecsForPhase(string phase)
        {
            lock (_specs) return _specs.Where(s => s.TimingPhase == phase).ToList();
        }
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => inner.ListAllManagedAsync(ct);
        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => inner.DisposeLeakedAsync(name, ct);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = new();
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add((logLevel, formatter(state, exception)));
        }
    }
}
