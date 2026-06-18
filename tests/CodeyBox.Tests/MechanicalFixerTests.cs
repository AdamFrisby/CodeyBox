using System.Globalization;
using Microsoft.Extensions.Options;
using CodeyBox.Audit.Presets;
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
    public void DotnetFormatFixer_ReusesFormatCheckCommandWithoutReadOnlyFlags()
    {
        var argv = DotnetFormatMechanicalFixer.ToFixerArgv(
            ["dotnet", "format", "--verify-no-changes", "--report", "/tmp/report", "--no-restore"]);

        Assert.Equal(["dotnet", "format", "--no-restore"], argv);
    }

    [Fact]
    public async Task Pipeline_RunsMechanicalFixerBeforeInitialAuditAndAfterRework()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var audit = new ProjectAudit
        {
            MaxIterations = 2,
            AuditTypes = ["scripted"],
            MechanicalFixers = [AppendingMechanicalFixer.FixerName],
        };
        var auditor = new OnceFailingAuditor();
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
        Assert.Equal(WorkItemState.Done, final!.State);

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

    private sealed class OnceFailingAuditor : IAuditor
    {
        private int _calls;

        public string Name => "test:once-failing";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _calls++;
            if (_calls == 1)
            {
                return Task.FromResult(new AuditResult(false,
                [
                    new AuditFinding(
                        Name,
                        AuditSeverity.Error,
                        "first audit requires rework",
                        "scripted one-time failure"),
                ]));
            }

            return Task.FromResult(new AuditResult(true, []));
        }
    }
}
