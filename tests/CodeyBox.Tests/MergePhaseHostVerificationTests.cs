using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class MergePhaseHostVerificationTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-merge-host-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    [Fact]
    public async Task SilentSilentResolutionDetected()
    {
        var (gitHost, repoId) = await CreateConflictingRepoAsync();
        var main = await gitHost.ResolveCommitAsync(repoId, "main");
        var work = await gitHost.ResolveCommitAsync(repoId, "work");

        var hostMerge = await gitHost.ComputeMergeTreeAsync(repoId, main, work);

        Assert.True(hostMerge.HasConflicts);
        Assert.Equal(["file.txt"], hostMerge.ConflictedFiles);
        var conflicted = await gitHost.ReadTextFileAsync(repoId, hostMerge.TreeSha, "file.txt");
        Assert.Contains("<<<<<<<", conflicted);
    }

    private async Task<(LocalGitHost GitHost, string RepoId)> CreateConflictingRepoAsync()
    {
        var seed = Path.Combine(_workspace, "seed");
        Directory.CreateDirectory(seed);
        await TestSupport.RunGit(seed, "init", "-b", "main");
        await TestSupport.RunGit(seed, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(seed, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(seed, "file.txt"), "base\n");
        await TestSupport.RunGit(seed, "add", "file.txt");
        await TestSupport.RunGit(seed, "commit", "-m", "base");
        await TestSupport.RunGit(seed, "checkout", "-b", "work");
        await File.WriteAllTextAsync(Path.Combine(seed, "file.txt"), "work\n");
        await TestSupport.RunGit(seed, "commit", "-am", "work");
        await TestSupport.RunGit(seed, "checkout", "main");
        await File.WriteAllTextAsync(Path.Combine(seed, "file.txt"), "main\n");
        await TestSupport.RunGit(seed, "commit", "-am", "main");

        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos") },
            NullLogger<LocalGitHost>.Instance);
        var repoId = await gitHost.EnsureRepositoryAsync(WorkItemId.New(), seed);
        return (gitHost, repoId);
    }
}

public sealed class PromptInjectionScopeContainmentTest
{
    [Fact]
    public async Task RejectsInjectedOutOfHunkModification()
    {
        using var test = new ScopeFenceVerificationTests();
        await test.RejectsCrossBufferEdit();
    }
}

public sealed class SecurityReviewIsAdvisoryOnlyTest
{
    [Fact]
    public void AdvisoryFindingDoesNotThrowOrGate()
    {
        var findings = MergeScopeFence.ReviewResolvedDiffForSuspiciousPatterns("+ eval(userInput)\n");

        Assert.Single(findings);
        Assert.Equal(AuditSeverity.Info, findings[0].Severity);
    }
}
