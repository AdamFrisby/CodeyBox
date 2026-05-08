using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

[Collection("Pipeline integration")]
public sealed class MergePhaseEndToEndPropertyTest : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-merge-property-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    public static IEnumerable<object[]> GeneratedBareRepoStates()
    {
        foreach (var mainCommitCount in Enumerable.Range(1, 3))
        {
            foreach (var shape in Enum.GetValues<GeneratedMergeShape>())
                yield return [new GeneratedMergeCase(shape, mainCommitCount)];
        }
    }

    [Theory]
    [MemberData(nameof(GeneratedBareRepoStates))]
    public async Task MainNeverLosesCommitsSilently(GeneratedMergeCase generatedCase)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(
            _workspace,
            $"seed-{generatedCase.Shape}-{generatedCase.MainCommitCount}");
        for (var i = 0; i < generatedCase.MainCommitCount; i++)
            await CommitAsync(seed, $"main-before-{i}.txt", $"main before {i}\n", $"main before {i}");

        await ConfigureSeedForShapeAsync(seed, generatedCase);
        var auditor = CreateAuditorForShape(generatedCase);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: auditor is null ? [] : [auditor]);
        if (auditor is not null)
            auditor.GitRoot = tp.GitRoot;
        ConfigureAgentForShape(tp.Agent, generatedCase);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = $"property {generatedCase.Shape} {generatedCase.MainCommitCount}",
            Prompt = "write work",
            WorkBranch = $"feature/property-{generatedCase.Shape}-{generatedCase.MainCommitCount}",
        };
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.NotNull(final.MergeSha);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (_, preMergeMain, _) = await TestSupport.RunGit(barePath, "rev-parse", $"{final.MergeSha}^1");
        var (_, mainCommitsBeforeMerge, _) = await TestSupport.RunGit(barePath, "rev-list", preMergeMain.Trim());
        foreach (var commit in mainCommitsBeforeMerge.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            await TestSupport.RunGit(barePath, "merge-base", "--is-ancestor", commit, "main");
    }

    private static async Task ConfigureSeedForShapeAsync(string seed, GeneratedMergeCase generatedCase)
    {
        if (generatedCase.Shape is GeneratedMergeShape.CleanWithMainAdvance)
            await CommitAsync(seed, "main-advance.txt", "base\n", "seed main advance file");

        if (generatedCase.Shape is GeneratedMergeShape.SingleLineConflict or GeneratedMergeShape.MixedCleanAndConflict)
            await CommitAsync(seed, "shared.txt", "base\n", "seed shared conflict file");
    }

    private MainAdvancingAuditor? CreateAuditorForShape(GeneratedMergeCase generatedCase)
        => generatedCase.Shape switch
        {
            GeneratedMergeShape.CleanWithMainAdvance => new MainAdvancingAuditor(
                _workspace,
                "main-advance.txt",
                $"main advanced {generatedCase.MainCommitCount}\n"),
            GeneratedMergeShape.SingleLineConflict or GeneratedMergeShape.MixedCleanAndConflict => new MainAdvancingAuditor(
                _workspace,
                "shared.txt",
                $"main side {generatedCase.MainCommitCount}\n"),
            _ => null,
        };

    private static void ConfigureAgentForShape(ScriptedAgent agent, GeneratedMergeCase generatedCase)
    {
        switch (generatedCase.Shape)
        {
            case GeneratedMergeShape.CleanFileAdd:
                agent.WorkPlan.Enqueue(new FileWrite(
                    $"work-{generatedCase.MainCommitCount}.txt",
                    $"work {generatedCase.MainCommitCount}\n"));
                break;

            case GeneratedMergeShape.CleanWithMainAdvance:
                agent.WorkPlan.Enqueue(new FileWrite(
                    $"work-clean-{generatedCase.MainCommitCount}.txt",
                    $"work clean {generatedCase.MainCommitCount}\n"));
                break;

            case GeneratedMergeShape.SingleLineConflict:
                agent.WorkPlan.Enqueue(new FileWrite(
                    "shared.txt",
                    $"work side {generatedCase.MainCommitCount}\n"));
                EnqueueConflictResolution(agent, generatedCase);
                break;

            case GeneratedMergeShape.MixedCleanAndConflict:
                agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
                {
                    var write = await sandbox.ExecAsync(new SandboxExec
                    {
                        Argv = ["sh", "-c", "cat > \"$1\"", "codeybox-property-write", $"{workingDirectory}/shared.txt"],
                        Stdin = $"work side {generatedCase.MainCommitCount}\n",
                    }, ct);
                    if (!write.Success)
                        throw new InvalidOperationException($"failed to write shared conflict file: {write.Stderr}");
                };
                agent.WorkPlan.Enqueue(new FileWrite(
                    $"clean-work-{generatedCase.MainCommitCount}.txt",
                    $"clean work {generatedCase.MainCommitCount}\n"));
                EnqueueConflictResolution(agent, generatedCase);
                break;
        }
    }

    private static void EnqueueConflictResolution(ScriptedAgent agent, GeneratedMergeCase generatedCase)
    {
        agent.ConflictResolutionPlan.Enqueue(files =>
        {
            var file = Assert.Single(files);
            Assert.Equal("shared.txt", file.Path);
            Assert.Contains("<<<<<<<", file.Content);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["shared.txt"] = $"main side {generatedCase.MainCommitCount}\nwork side {generatedCase.MainCommitCount}\n",
            };
        });
    }

    private static async Task CommitAsync(string repo, string path, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repo, path), content);
        await TestSupport.RunGit(repo, "add", path);
        await TestSupport.RunGit(repo, "commit", "-m", message);
    }

    public sealed record GeneratedMergeCase(GeneratedMergeShape Shape, int MainCommitCount)
    {
        public override string ToString() => $"{Shape}-{MainCommitCount}";
    }

    public enum GeneratedMergeShape
    {
        CleanFileAdd,
        CleanWithMainAdvance,
        SingleLineConflict,
        MixedCleanAndConflict,
    }

    private sealed class MainAdvancingAuditor : IAuditor
    {
        private readonly string _workspace;
        private readonly string _path;
        private readonly string _content;

        public string? GitRoot { get; set; }
        public string Name => "advance-main";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public MainAdvancingAuditor(string workspace, string path, string content)
        {
            _workspace = workspace;
            _path = path;
            _content = content;
        }

        public async Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = ct;
            if (GitRoot is null)
                throw new InvalidOperationException("GitRoot must be assigned before the auditor runs.");

            var barePath = Path.Combine(GitRoot, context.WorkItemId + ".git");
            var clone = Path.Combine(_workspace, "advance-main-" + Guid.NewGuid().ToString("N")[..8]);
            await TestSupport.RunGit(_workspace, "clone", barePath, clone);
            await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
            await TestSupport.RunGit(clone, "config", "user.name", "Test");
            await TestSupport.RunGit(clone, "checkout", context.BaseBranch);
            await File.WriteAllTextAsync(Path.Combine(clone, _path), _content);
            await TestSupport.RunGit(clone, "add", _path);
            await TestSupport.RunGit(clone, "commit", "-m", "advance main during audit");
            await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{context.BaseBranch}");
            return new AuditResult(true, []);
        }
    }
}
