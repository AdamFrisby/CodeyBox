using CodeyBox.Agents;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Process;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the merge phase's isolated bare-repo clone lands under the
/// host bare-repo root rather than <c>/tmp</c>. The snap-confined Multipass
/// daemon can only read paths under <c>~/snap/multipass/common/</c>, so a
/// host bind-mount source under <c>/tmp</c> surfaces as
/// "Source path does not exist" even though the directory was just created.
/// The durable bare repo already lives in the multipass-allowed location;
/// staging the merge clone as a sibling inherits that property.
/// </summary>
public sealed class MergePhaseIsolatedRepoStagingTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-merge-staging-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    [Fact]
    public async Task IsolatedMergeRepo_IsClonedAsSiblingOfDurableBareRepo_NotUnderTempPath()
    {
        var gitRoot = Path.Combine(_workspace, "git-root");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);

        var seed = await CreateSeedRepoAsync();
        var workItemId = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(workItemId, seed);

        var pipeline = CreatePipeline(gitHost);
        var clonedPath = await pipeline.CreateIsolatedMergeRepositoryAsync(repoId, workItemId, CancellationToken.None);

        try
        {
            // Primary invariant: the cloned bare repo lives under the same
            // root as the durable bare repo. That root is the operator's
            // configured GitRootDirectory — under ~/snap/multipass/common/...
            // in snap-Multipass installations — so the host bind-mount source
            // is in a path the multipass daemon's AppArmor profile allows.
            Assert.Equal(gitRoot, Path.GetDirectoryName(clonedPath));
            Assert.True(Directory.Exists(clonedPath), $"expected isolated bare clone at {clonedPath}");
            Assert.True(File.Exists(Path.Combine(clonedPath, "HEAD")),
                "isolated clone must be a valid bare git repository (HEAD missing)");

            // Regression guard: the old code unconditionally staged into
            // Path.GetTempPath(); make sure we never land back there directly.
            // (gitRoot itself may be a subdirectory of /tmp in tests — that is
            // fine; the failure mode we're guarding against is staging into
            // /tmp itself rather than into the configured bare-repo root.)
            var tempPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            Assert.NotEqual(tempPath, Path.TrimEndingDirectorySeparator(Path.GetFullPath(gitRoot)));
            Assert.NotEqual(
                tempPath,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(clonedPath)!)));
        }
        finally
        {
            try { Directory.Delete(clonedPath, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task IsolatedMergeRepo_FilenameEncodesWorkItemId_ForOperatorTraceability()
    {
        var gitRoot = Path.Combine(_workspace, "git-root");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var seed = await CreateSeedRepoAsync();
        var workItemId = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(workItemId, seed);

        var pipeline = CreatePipeline(gitHost);
        var clonedPath = await pipeline.CreateIsolatedMergeRepositoryAsync(repoId, workItemId, CancellationToken.None);
        try
        {
            var leafName = Path.GetFileName(clonedPath);
            Assert.StartsWith("codeybox-merge-", leafName);
            Assert.EndsWith(".git", leafName);
            Assert.Contains(workItemId.ToString(), leafName);
        }
        finally
        {
            try { Directory.Delete(clonedPath, recursive: true); } catch { /* best-effort */ }
        }
    }

    private async Task<string> CreateSeedRepoAsync()
    {
        var seed = Path.Combine(_workspace, "seed-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(seed);
        await TestSupport.RunGit(seed, "init", "-b", "main");
        await TestSupport.RunGit(seed, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(seed, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(seed, "file.txt"), "base\n");
        await TestSupport.RunGit(seed, "add", "file.txt");
        await TestSupport.RunGit(seed, "commit", "-m", "base");
        return seed;
    }

    private PipelineRunner CreatePipeline(LocalGitHost gitHost)
    {
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = "unused",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit(),
        };
        return new PipelineRunner(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            gitHost,
            new AgentRegistry([new ScriptedAgent([])]),
            new StaticCredentialProvider(),
            new InMemoryPullRequestService(),
            new InMemoryProjectRepository(project),
            new TestUpstreamFactory(),
            new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            new SqliteWorkItemStore(stateDb),
            new NullWebhookDispatcher(),
            new PipelineOptions { SandboxImageReference = "ignored" },
            NullLogger<PipelineRunner>.Instance);
    }
}
