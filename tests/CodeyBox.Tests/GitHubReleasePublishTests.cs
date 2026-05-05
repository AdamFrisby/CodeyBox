using CodeyBox.Core;
using CodeyBox.Upstream;

namespace CodeyBox.Tests;

/// <summary>
/// Tests TryMergeUpstreamBranchAsync on GitGenericUpstreamRemote using a
/// real local git repo. Creates two branches, makes diverging commits, then
/// verifies that the merge succeeds (or detects conflict on conflicting content).
/// </summary>
public sealed class GitHubReleasePublishTests : IAsyncLifetime
{
    private string _repoDir = "";
    private string _tmpDir = "";

    public async Task InitializeAsync()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"cb-pub-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);

        // Bare origin repo
        var originDir = Path.Combine(_tmpDir, "origin.git");
        Directory.CreateDirectory(originDir);
        await TestSupport.RunGit(originDir, "init", "--bare", "-b", "main");

        // Working clone
        _repoDir = Path.Combine(_tmpDir, "work");
        await TestSupport.RunGit(_tmpDir, "clone", originDir, "work");
        await TestSupport.RunGit(_repoDir, "config", "user.email", "t@l");
        await TestSupport.RunGit(_repoDir, "config", "user.name", "T");

        // Initial commit on main
        await File.WriteAllTextAsync(Path.Combine(_repoDir, "README.md"), "initial\n");
        await TestSupport.RunGit(_repoDir, "add", "README.md");
        await TestSupport.RunGit(_repoDir, "commit", "-m", "initial");
        await TestSupport.RunGit(_repoDir, "push", "origin", "main");

        // Release branch from main
        await TestSupport.RunGit(_repoDir, "checkout", "-b", "release/v1.0");
        await File.WriteAllTextAsync(Path.Combine(_repoDir, "release-note.txt"), "release\n");
        await TestSupport.RunGit(_repoDir, "add", "release-note.txt");
        await TestSupport.RunGit(_repoDir, "commit", "-m", "release branch commit");
        await TestSupport.RunGit(_repoDir, "push", "origin", "release/v1.0");

        // New commit on main (diverge)
        await TestSupport.RunGit(_repoDir, "checkout", "main");
        await File.WriteAllTextAsync(Path.Combine(_repoDir, "main-change.txt"), "main update\n");
        await TestSupport.RunGit(_repoDir, "add", "main-change.txt");
        await TestSupport.RunGit(_repoDir, "commit", "-m", "main branch update");
        await TestSupport.RunGit(_repoDir, "push", "origin", "main");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TryMergeUpstreamBranchAsync_CleanMerge_ReturnsTrue()
    {
        var originUrl = Path.Combine(_tmpDir, "origin.git");
        var opts = new GitGenericUpstreamOptions
        {
            UpstreamUrl = originUrl,
            ExtraEnvironment = new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Test",
                ["GIT_AUTHOR_EMAIL"] = "t@l",
                ["GIT_COMMITTER_NAME"] = "Test",
                ["GIT_COMMITTER_EMAIL"] = "t@l",
            },
        };
        var remote = new GitGenericUpstreamRemote(new ThrowingGitHost(), opts);

        var merged = await remote.TryMergeUpstreamBranchAsync("release/v1.0", "main");

        Assert.True(merged);
    }

    [Fact]
    public async Task TryMergeUpstreamBranchAsync_AlreadyUpToDate_ReturnsTrue()
    {
        var originUrl = Path.Combine(_tmpDir, "origin.git");
        var opts = new GitGenericUpstreamOptions
        {
            UpstreamUrl = originUrl,
            ExtraEnvironment = new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Test",
                ["GIT_AUTHOR_EMAIL"] = "t@l",
                ["GIT_COMMITTER_NAME"] = "Test",
                ["GIT_COMMITTER_EMAIL"] = "t@l",
            },
        };
        var remote = new GitGenericUpstreamRemote(new ThrowingGitHost(), opts);

        // Merge once (brings release/v1.0 up-to-date with main)
        await remote.TryMergeUpstreamBranchAsync("release/v1.0", "main");

        // Merge again — release already contains main's content
        var merged = await remote.TryMergeUpstreamBranchAsync("release/v1.0", "main");

        Assert.True(merged);
    }

    [Fact]
    public async Task TryMergeUpstreamBranchAsync_ConflictingContent_ReturnsFalse()
    {
        var originUrl = Path.Combine(_tmpDir, "origin.git");

        // Create conflicting commit on release branch for the same file as main
        var clone = Path.Combine(_tmpDir, "conflict-clone");
        await TestSupport.RunGit(_tmpDir, "clone", originUrl, "conflict-clone");
        await TestSupport.RunGit(clone, "config", "user.email", "t@l");
        await TestSupport.RunGit(clone, "config", "user.name", "T");
        await TestSupport.RunGit(clone, "checkout", "release/v1.0");
        // Write conflicting content to the same file that main also modified
        await File.WriteAllTextAsync(Path.Combine(clone, "main-change.txt"), "conflicting release content\n");
        await TestSupport.RunGit(clone, "add", "main-change.txt");
        await TestSupport.RunGit(clone, "commit", "-m", "conflicting change on release");
        await TestSupport.RunGit(clone, "push", "origin", "release/v1.0");

        var opts = new GitGenericUpstreamOptions
        {
            UpstreamUrl = originUrl,
            ExtraEnvironment = new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Test",
                ["GIT_AUTHOR_EMAIL"] = "t@l",
                ["GIT_COMMITTER_NAME"] = "Test",
                ["GIT_COMMITTER_EMAIL"] = "t@l",
            },
        };
        var remote = new GitGenericUpstreamRemote(new ThrowingGitHost(), opts);

        var merged = await remote.TryMergeUpstreamBranchAsync("release/v1.0", "main");

        Assert.False(merged);
    }
}
