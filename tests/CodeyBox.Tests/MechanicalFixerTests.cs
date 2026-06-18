using System.Globalization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Audit;
using CodeyBox.Audit.Presets;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Core;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

[Collection("Pipeline integration")]
public sealed class MechanicalFixerTests : IDisposable
{
    private readonly string _workspace;

    public MechanicalFixerTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-mechanical-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task ProjectRepository_DerivesDotnetFormat_ForCSharpLanguage()
    {
        var repo = new ProjectRepository(Options.Create(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "p",
                    RepositoryUrl = "https://example.invalid/repo.git",
                    Audit = new ProjectAuditConfig
                    {
                        Languages = ["csharp"],
                    },
                },
            ],
        }));

        var project = await repo.GetAsync(new ProjectId("p"));

        Assert.Equal([DotnetFormatMechanicalFixer.FixerName], project!.Audit.MechanicalFixers);
    }

    [Fact]
    public async Task ProjectRepository_ExplicitEmptyMechanicalFixers_DisablesLanguageDefault()
    {
        var repo = new ProjectRepository(Options.Create(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "p",
                    RepositoryUrl = "https://example.invalid/repo.git",
                    Audit = new ProjectAuditConfig
                    {
                        Languages = ["csharp"],
                        MechanicalFixers = [],
                    },
                },
            ],
        }));

        var project = await repo.GetAsync(new ProjectId("p"));

        Assert.Empty(project!.Audit.MechanicalFixers);
    }

    [Fact]
    public async Task ProjectRepository_BuiltInUatProfile_DerivesDotnetFormat()
    {
        var repo = new ProjectRepository(Options.Create(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "p",
                    RepositoryUrl = "https://example.invalid/repo.git",
                    Audit = new ProjectAuditConfig
                    {
                        Profile = AuditProfilePresets.Uat,
                    },
                },
            ],
        }));

        var project = await repo.GetAsync(new ProjectId("p"));

        Assert.Equal([DotnetFormatMechanicalFixer.FixerName],
            project!.Audit.ResolveProfile(AuditProfilePresets.Uat).MechanicalFixers);
    }

    [Fact]
    public async Task ProjectRepository_CustomCSharpProfile_DerivesDotnetFormat()
    {
        var repo = new ProjectRepository(Options.Create(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "p",
                    RepositoryUrl = "https://example.invalid/repo.git",
                    Audit = new ProjectAuditConfig
                    {
                        Profiles = new Dictionary<string, ProjectAuditConfig>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["ci"] = new()
                            {
                                Languages = ["csharp"],
                            },
                        },
                    },
                },
            ],
        }));

        var project = await repo.GetAsync(new ProjectId("p"));

        Assert.Equal([DotnetFormatMechanicalFixer.FixerName],
            project!.Audit.ResolveProfile("ci").MechanicalFixers);
    }

    [Fact]
    public async Task ProjectRepository_ProfileExplicitEmptyMechanicalFixers_DisablesLanguageDefault()
    {
        var repo = new ProjectRepository(Options.Create(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "p",
                    RepositoryUrl = "https://example.invalid/repo.git",
                    Audit = new ProjectAuditConfig
                    {
                        Profiles = new Dictionary<string, ProjectAuditConfig>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["ci"] = new()
                            {
                                Languages = ["csharp"],
                                MechanicalFixers = [],
                            },
                        },
                    },
                },
            ],
        }));

        var project = await repo.GetAsync(new ProjectId("p"));

        Assert.Empty(project!.Audit.ResolveProfile("ci").MechanicalFixers);
    }

    [Fact]
    public void DotnetFormatFixer_ReusesFormatCheckCommandWithoutReadOnlyFlags()
    {
        var argv = DotnetFormatMechanicalFixer.ToFixerArgv(
            ["dotnet", "format", "--verify-no-changes", "--report", "/tmp/report", "--no-restore"]);

        Assert.Equal(["dotnet", "format", "--no-restore"], argv);
    }

    [Fact]
    public async Task DotnetFormatFixer_UsesActiveCSharpFormatAuditorAndDetectsTrackedChanges()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        var sandbox = new DotnetFormatSandbox(
            markerStdout: "src/App\n",
            statusOutputs: ["", " M src/App/Program.cs\0"],
            formatResult: new SandboxExecResult(0, "formatted", ""));

        var result = await new DotnetFormatMechanicalFixer().ApplyAsync(
            sandbox,
            "/work/repo",
            new MechanicalFixerContext(
                WorkItemId.New(),
                "feature/test",
                "main",
                1,
                "project",
                [auditor]));

        Assert.True(result.Changed);
        var formatExec = Assert.Single(sandbox.Execs, e => e.Argv.Count >= 2 && e.Argv[0] == "dotnet" && e.Argv[1] == "format");
        Assert.Equal(["dotnet", "format", "--verbosity", "diagnostic"], formatExec.Argv);
        Assert.Equal("/work/repo/src/App", formatExec.WorkingDirectory);
        Assert.All(
            sandbox.Execs.Where(e => e.Argv.Count >= 2 && e.Argv[0] == "git" && e.Argv.Contains("status")),
            e => Assert.Contains("--untracked-files=no", e.Argv));
    }

    [Fact]
    public async Task DotnetFormatFixer_CommandFailureThrows()
    {
        var auditor = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new ScriptedAgent([MergeStrategy.RealMerge])))
            .Single(a => a.Name == "csharp:format-check");
        var sandbox = new DotnetFormatSandbox(
            markerStdout: ".\n",
            statusOutputs: [""],
            formatResult: new SandboxExecResult(2, "", "format failed"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DotnetFormatMechanicalFixer().ApplyAsync(
                sandbox,
                "/work/repo",
                new MechanicalFixerContext(
                    WorkItemId.New(),
                    "feature/test",
                    "main",
                    1,
                    "project",
                    [auditor])));

        Assert.Contains("dotnet-format fixer command failed", ex.Message);
    }

    [Fact]
    public async Task Pipeline_RunsMechanicalFixerBeforeInitialAuditAndAfterRework()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddTrackedFileAsync(seed, "normalizer.txt", "");
        var audit = new ProjectAudit
        {
            MaxIterations = 2,
            AuditTypes = ["scripted"],
            MechanicalFixers = [AppendingMechanicalFixer.FixerName],
        };
        var auditor = new NormalizerAwareOnceFailingAuditor();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectAudit: audit,
            mechanicalFixers: [new AppendingMechanicalFixer()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("rework.txt", "rework\n"));

        var item = NewItem("feature/mechanical");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(final!.State == WorkItemState.Done, final.LastError);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, normalized, _) = await TestSupport.RunGit(barePath, "show", "main:normalizer.txt");
        Assert.Equal("1\n2\n", normalized);

        var (_, mechanicalLog, _) = await TestSupport.RunGit(
            barePath,
            "log",
            "--grep=chore: normalize mechanical edits",
            "--format=%B",
            item.WorkBranch!);
        Assert.Equal(2, CountOccurrences(mechanicalLog, "chore: normalize mechanical edits"));
        Assert.Equal(2, CountOccurrences(mechanicalLog, "CodeyBox-Agent: mechanical/fake-normalizer"));
        Assert.Equal(2, CountOccurrences(mechanicalLog, $"{CodeyBoxTrailers.PromptRevisionTrailerKey}: 1"));
    }

    [Fact]
    public async Task Pipeline_TracksOnlyFormatterChangesAndSkipsSideEffectOnlyCommit()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = [UntrackedSideEffectMechanicalFixer.FixerName],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            projectAudit: audit,
            mechanicalFixers: [new UntrackedSideEffectMechanicalFixer()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/mechanical-untracked");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(final!.State == WorkItemState.Done, final.LastError);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, files, _) = await TestSupport.RunGit(barePath, "ls-tree", "-r", "--name-only", "main");
        Assert.DoesNotContain("generated/cache.txt", files);

        var (_, mechanicalLog, _) = await TestSupport.RunGit(
            barePath,
            "log",
            "--grep=chore: normalize mechanical edits",
            "--format=%B",
            item.WorkBranch!);
        Assert.DoesNotContain("chore: normalize mechanical edits", mechanicalLog);
    }

    [Fact]
    public async Task Pipeline_MechanicalCommitOmitsPromptRevision_WhenPromptChangedAfterDispatch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await AddTrackedFileAsync(seed, "normalizer.txt", "");
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = [AppendingMechanicalFixer.FixerName],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PromptRevisionTrailerAuditor()],
            projectAudit: audit,
            mechanicalFixers: [new AppendingMechanicalFixer()]);

        const string fileName = "stale.txt";
        const string fileContent = "v1\n";
        WorkItemId? editTarget = null;
        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            if (editTarget is not null)
            {
                await tp.Store.TryReplacePromptAsync(
                    editTarget.Value,
                    "edited while work was running",
                    DateTimeOffset.UtcNow,
                    ct);
            }

            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", $"{workingDirectory}/{fileName}"],
                Stdin = fileContent,
            }, ct);
            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "add", "--", fileName],
            }, ct);
            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "commit", "-m", "agent commit without prompt trailer"],
            }, ct);
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite(fileName, fileContent));

        var item = NewItem("feature/mechanical-stale-prompt");
        editTarget = item.Id;
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotEqual(WorkItemState.Done, final!.State);
        Assert.Equal(2, final.PromptRevision);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, subject, _) = await TestSupport.RunGit(barePath, "log", "-1", "--format=%s", item.WorkBranch!);
        Assert.Equal("chore: normalize mechanical edits", subject.Trim());

        var (_, trailer, _) = await TestSupport.RunGit(
            barePath,
            "log",
            "-1",
            $"--pretty=format:%(trailers:key={CodeyBoxTrailers.PromptRevisionTrailerKey},valueonly=true,unfold=true)",
            item.WorkBranch!);
        Assert.Equal(string.Empty, trailer.Trim());
    }

    [Fact]
    public async Task Pipeline_MechanicalSandboxUsesAuditToolProfileWithoutCredentialsAndReadOnlyRepo()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var recorder = new SpecRecordingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = [NoOpMechanicalFixer.FixerName],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            projectAudit: audit,
            sandboxProvider: recorder,
            networkProfiles: new ProjectNetworkProfiles
            {
                AuditTool = "audit-tool-profile",
            },
            mechanicalFixers: [new NoOpMechanicalFixer()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/mechanical-spec");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var spec = Assert.Single(recorder.Specs, s => s.TimingPhase == "mechanical-edit");
        Assert.Equal("audit-tool-profile", spec.Network.ProfileName);
        Assert.Empty(spec.Network.AllowedHosts);
        Assert.DoesNotContain(CodeyBoxTrailers.PromptRevisionEnvVar, spec.Environment.Keys);
        Assert.DoesNotContain(spec.Mounts, m => m.SandboxPath == SandboxConventions.CredentialsDir);
        var repoMount = Assert.Single(spec.Mounts, m => m.SandboxPath == "/repo");
        Assert.True(repoMount.ReadOnly);
    }

    [Fact]
    public async Task Pipeline_MechanicalFailureIsInfrastructureFailure()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var audit = new ProjectAudit
        {
            MaxIterations = 1,
            AuditTypes = ["scripted"],
            MechanicalFixers = [ThrowingMechanicalFixer.FixerName],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()],
            projectAudit: audit,
            mechanicalFixers: [new ThrowingMechanicalFixer()]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/mechanical-failure");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.Contains("mechanical-edit failed", final.LastError);
    }

    private static WorkItem NewItem(string branch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "mechanical normalizer",
        Prompt = "change the repo",
        WorkBranch = branch,
        BaseBranch = "main",
    };

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static async Task AddTrackedFileAsync(string repo, string path, string contents)
    {
        var fullPath = Path.Combine(repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, contents);
        await TestSupport.RunGit(repo, "add", "--", path);
        await TestSupport.RunGit(repo, "commit", "-m", $"add {path}");
    }

    private sealed class AppendingMechanicalFixer : IMechanicalFixer
    {
        public const string FixerName = "fake-normalizer";

        public string Name => FixerName;
        public string Kind => "test";

        public async Task<MechanicalFixerResult> ApplyAsync(
            ISandbox sandbox,
            string workingDirectory,
            MechanicalFixerContext context,
            CancellationToken ct = default)
        {
            var result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv =
                [
                    "sh",
                    "-c",
                    "printf '%s\n' \"$1\" >> \"$0\"",
                    $"{workingDirectory}/normalizer.txt",
                    context.AuditIteration.ToString(CultureInfo.InvariantCulture),
                ],
            }, ct);

            if (!result.Success)
                throw new InvalidOperationException(result.Stderr);

            return new MechanicalFixerResult(true, $"appended iteration {context.AuditIteration}");
        }
    }

    private sealed class UntrackedSideEffectMechanicalFixer : IMechanicalFixer
    {
        public const string FixerName = "side-effect-only";

        public string Name => FixerName;
        public string Kind => "test";

        public async Task<MechanicalFixerResult> ApplyAsync(
            ISandbox sandbox,
            string workingDirectory,
            MechanicalFixerContext context,
            CancellationToken ct = default)
        {
            var result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "mkdir -p \"$0/generated\" && printf cache > \"$0/generated/cache.txt\"", workingDirectory],
            }, ct);

            if (!result.Success)
                throw new InvalidOperationException(result.Stderr);

            return new MechanicalFixerResult(true, "created untracked side effect");
        }
    }

    private sealed class NoOpMechanicalFixer : IMechanicalFixer
    {
        public const string FixerName = "noop-normalizer";

        public string Name => FixerName;
        public string Kind => "test";

        public Task<MechanicalFixerResult> ApplyAsync(
            ISandbox sandbox,
            string workingDirectory,
            MechanicalFixerContext context,
            CancellationToken ct = default)
            => Task.FromResult(new MechanicalFixerResult(false, "no changes"));
    }

    private sealed class ThrowingMechanicalFixer : IMechanicalFixer
    {
        public const string FixerName = "throwing-normalizer";

        public string Name => FixerName;
        public string Kind => "test";

        public Task<MechanicalFixerResult> ApplyAsync(
            ISandbox sandbox,
            string workingDirectory,
            MechanicalFixerContext context,
            CancellationToken ct = default)
            => throw new InvalidOperationException("normalizer unavailable");
    }

    private sealed class NormalizerAwareOnceFailingAuditor : IAuditor
    {
        private int _calls;

        public string Name => "test:once-failing";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public async Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _calls++;
            var read = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["cat", $"{workingDirectory}/normalizer.txt"],
            }, ct);
            if (!read.Success)
            {
                return new AuditResult(false,
                [
                    new AuditFinding(Name, AuditSeverity.Error, "normalizer output missing", read.Stderr),
                ]);
            }

            var expected = _calls == 1 ? "1\n" : "1\n2\n";
            if (!string.Equals(read.Stdout, expected, StringComparison.Ordinal))
            {
                return new AuditResult(false,
                [
                    new AuditFinding(
                        Name,
                        AuditSeverity.Error,
                        "auditor did not observe mechanical normalization",
                        $"Expected normalizer.txt to be {expected.Length} bytes for call {_calls}, got: {read.Stdout}"),
                ]);
            }

            if (_calls == 1)
            {
                return new AuditResult(false,
                [
                    new AuditFinding(
                        Name,
                        AuditSeverity.Error,
                        "first audit requires rework",
                        "scripted one-time failure"),
                ]);
            }

            return new AuditResult(true, []);
        }
    }

    private sealed class PassingAuditor : IAuditor
    {
        public string Name => "test:passing";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
            => Task.FromResult(new AuditResult(true, []));
    }

    private sealed class DotnetFormatSandbox : ISandbox
    {
        private readonly string _markerStdout;
        private readonly Queue<string> _statusOutputs;
        private readonly SandboxExecResult _formatResult;

        public DotnetFormatSandbox(
            string markerStdout,
            IEnumerable<string> statusOutputs,
            SandboxExecResult formatResult)
        {
            _markerStdout = markerStdout;
            _statusOutputs = new Queue<string>(statusOutputs);
            _formatResult = formatResult;
        }

        public string Id => "dotnet-format-fake";
        public List<SandboxExec> Execs { get; } = [];

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Execs.Add(exec);
            if (exec.Argv is ["sh", "-c", var script] && !script.Contains("command -v", StringComparison.Ordinal))
                return Task.FromResult(new SandboxExecResult(0, _markerStdout, ""));

            if (exec.Argv.Count >= 3 && exec.Argv[0] == "git" && exec.Argv.Contains("status"))
            {
                var stdout = _statusOutputs.Count > 0 ? _statusOutputs.Dequeue() : "";
                return Task.FromResult(new SandboxExecResult(0, stdout, ""));
            }

            if (exec.Argv.Count >= 2 && exec.Argv[0] == "dotnet" && exec.Argv[1] == "format")
                return Task.FromResult(_formatResult);

            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SpecRecordingSandboxProvider : ISandboxProvider
    {
        private readonly ISandboxProvider _inner;

        public SpecRecordingSandboxProvider(ISandboxProvider inner) => _inner = inner;

        public string Name => _inner.Name;
        public List<SandboxSpec> Specs { get; } = [];

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            Specs.Add(spec);
            return _inner.CreateAsync(spec, ct);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => _inner.DisposeLeakedAsync(name, ct);
    }
}
