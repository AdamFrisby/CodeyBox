using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class ScopeFenceVerificationTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-scope-fence-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    [Fact]
    public async Task RejectsOutOfHunkChange()
    {
        var ctx = await CreateContextAsync();
        await CommitResolvedAsync(ctx, lines => lines[0] = "outside");

        var ex = await Assert.ThrowsAsync<ScopeFenceViolation>(() => VerifyAsync(ctx));
        Assert.Contains("file.txt:1", ex.Message);
    }

    [Fact]
    public async Task RejectsNewFile()
    {
        var ctx = await CreateContextAsync();
        await CommitResolvedAsync(ctx, lines => lines[10] = "inside", ("new.txt", "payload\n"));

        var ex = await Assert.ThrowsAsync<ScopeFenceViolation>(() => VerifyAsync(ctx));
        Assert.Contains("new.txt:1 new file", ex.Message);
    }

    [Fact]
    public async Task RejectsRename()
    {
        var ctx = await CreateContextAsync();
        var clone = await CloneAsync(ctx);
        await TestSupport.RunGit(clone, "mv", "file.txt", "renamed.txt");
        await TestSupport.RunGit(clone, "commit", "-m", "resolved rename");
        await TestSupport.RunGit(clone, "push", "origin", "HEAD:resolved");

        var ex = await Assert.ThrowsAsync<ScopeFenceViolation>(() => VerifyAsync(ctx));
        Assert.Contains("renamed.txt:1 rename", ex.Message);
    }

    [Fact]
    public async Task RejectsDeletedConflictedFile()
    {
        var ctx = await CreateContextAsync();
        var clone = await CloneAsync(ctx);
        await TestSupport.RunGit(clone, "rm", "file.txt");
        await TestSupport.RunGit(clone, "commit", "-m", "resolved delete");
        await TestSupport.RunGit(clone, "push", "origin", "HEAD:resolved");

        var ex = await Assert.ThrowsAsync<ScopeFenceViolation>(() => VerifyAsync(ctx));
        Assert.Contains("file.txt:1 deleted file", ex.Message);
    }

    [Fact]
    public async Task AllowsBufferZoneEdit()
    {
        var ctx = await CreateContextAsync();
        await CommitResolvedAsync(ctx, lines => lines[14] = "buffer edit");

        await VerifyAsync(ctx);
    }

    [Fact]
    public async Task RejectsCrossBufferEdit()
    {
        var ctx = await CreateContextAsync();
        await CommitResolvedAsync(ctx, lines => lines[17] = "too far");

        var ex = await Assert.ThrowsAsync<ScopeFenceViolation>(() => VerifyAsync(ctx));
        Assert.Contains("file.txt:18", ex.Message);
    }

    [Fact]
    public async Task RejectsInsertionJustOutsideBuffer()
    {
        var ctx = await CreateContextAsync(lineCount: 25);
        await CommitResolvedLinesAsync(ctx, lines => lines.Insert(17, "inserted payload"));

        var ex = await Assert.ThrowsAsync<ScopeFenceViolation>(() => VerifyAsync(ctx));
        Assert.Contains("file.txt:18", ex.Message);
    }

    [Fact]
    public async Task RejectsOversizedInsertedPayloadInAllowedReplacement()
    {
        var ctx = await CreateContextAsync(lineCount: 25);
        await CommitResolvedLinesAsync(ctx, lines =>
        {
            lines.RemoveAt(9);
            lines.InsertRange(9, Enumerable.Range(1, 10).Select(i => $"payload {i}"));
        });

        var ex = await Assert.ThrowsAsync<ScopeFenceViolation>(() => VerifyAsync(ctx));
        Assert.Contains("file.txt:18", ex.Message);
    }

    [Fact]
    public async Task RejectsWhitespaceOutsideHunk()
    {
        var ctx = await CreateContextAsync();
        await CommitResolvedAsync(ctx, lines => lines[17] += " ");

        var ex = await Assert.ThrowsAsync<ScopeFenceViolation>(() => VerifyAsync(ctx));
        Assert.Contains("file.txt:18", ex.Message);
    }

    [Fact]
    public async Task RejectsOutOfHunkInsertionAfterHunkShrink()
    {
        var ctx = await CreateContextAsync(lineCount: 60);
        var clone = await CloneAsync(ctx);
        var baseline = Enumerable.Range(1, 60).Select(i => $"line {i}").ToList();
        baseline[9] = "<<<<<<< main";
        baseline[10] = "main 11";
        baseline[11] = "main 12";
        baseline[12] = "=======";
        baseline[13] = "work 14";
        baseline[14] = "work 15";
        baseline[15] = ">>>>>>> work";
        await File.WriteAllLinesAsync(Path.Combine(clone, "file.txt"), baseline);
        await TestSupport.RunGit(clone, "commit", "-am", "canonical conflict baseline");
        await TestSupport.RunGit(clone, "branch", "baseline");

        var resolved = baseline.ToList();
        resolved.RemoveRange(9, 7);
        resolved.Insert(9, "resolved hunk");
        resolved[33] = "payload after shifted hunk";
        await File.WriteAllLinesAsync(Path.Combine(clone, "file.txt"), resolved);
        await TestSupport.RunGit(clone, "commit", "-am", "resolved with shifted payload");
        await TestSupport.RunGit(clone, "push", "origin", "baseline", "HEAD:resolved");

        var ex = await Assert.ThrowsAsync<ScopeFenceViolation>(() =>
            MergeScopeFence.VerifyAsync(
                ctx.GitHost,
                ctx.RepoId,
                "main",
                "baseline",
                "resolved",
                [new ConflictHunk("file.txt", 10, 16)],
                bufferLines: 0,
                CancellationToken.None));
        Assert.Contains("file.txt:40", ex.Message);
    }

    [Fact]
    public async Task RejectsCleanWorkBranchChangeAlreadyInConflictBaseline()
    {
        var ctx = await CreateContextAsync();
        var clone = await CloneAsync(ctx);
        await File.WriteAllTextAsync(Path.Combine(clone, "clean.txt"), "legitimate work change\n");
        await TestSupport.RunGit(clone, "add", "clean.txt");
        await TestSupport.RunGit(clone, "commit", "-m", "canonical conflict baseline");
        await TestSupport.RunGit(clone, "branch", "baseline");
        var path = Path.Combine(clone, "file.txt");
        var lines = await File.ReadAllLinesAsync(path);
        lines[10] = "inside";
        await File.WriteAllLinesAsync(path, lines);
        await TestSupport.RunGit(clone, "commit", "-am", "resolved");
        await TestSupport.RunGit(clone, "push", "origin", "baseline", "HEAD:resolved");

        var ex = await Assert.ThrowsAsync<ScopeFenceViolation>(() => MergeScopeFence.VerifyAsync(
            ctx.GitHost,
            ctx.RepoId,
            "main",
            "baseline",
            "resolved",
            [new ConflictHunk("file.txt", 10, 12)],
            bufferLines: 5,
            CancellationToken.None));

        Assert.Contains("clean.txt:1 new file", ex.Message);
    }

    private static Task VerifyAsync(Context ctx)
        => MergeScopeFence.VerifyAsync(
            ctx.GitHost,
            ctx.RepoId,
            "main",
            "main",
            "resolved",
            [new ConflictHunk("file.txt", 10, 12)],
            bufferLines: 5,
            CancellationToken.None);

    private async Task<Context> CreateContextAsync(int lineCount = 20)
    {
        var seed = Path.Combine(_workspace, "seed-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(seed);
        await TestSupport.RunGit(seed, "init", "-b", "main");
        await TestSupport.RunGit(seed, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(seed, "config", "user.name", "Test");
        await File.WriteAllLinesAsync(Path.Combine(seed, "file.txt"), Enumerable.Range(1, lineCount).Select(i => $"line {i}"));
        await TestSupport.RunGit(seed, "add", "file.txt");
        await TestSupport.RunGit(seed, "commit", "-m", "initial");

        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]) },
            NullLogger<LocalGitHost>.Instance);
        var repoId = await gitHost.EnsureRepositoryAsync(WorkItemId.New(), seed);
        return new Context(gitHost, repoId);
    }

    private async Task CommitResolvedAsync(Context ctx, Action<string[]> mutate, params (string Path, string Content)[] additions)
    {
        var clone = await CloneAsync(ctx);
        var path = Path.Combine(clone, "file.txt");
        var lines = await File.ReadAllLinesAsync(path);
        mutate(lines);
        await File.WriteAllLinesAsync(path, lines);
        foreach (var addition in additions)
            await File.WriteAllTextAsync(Path.Combine(clone, addition.Path), addition.Content);
        await TestSupport.RunGit(clone, "add", "-A");
        await TestSupport.RunGit(clone, "commit", "-m", "resolved");
        await TestSupport.RunGit(clone, "push", "origin", "HEAD:resolved");
    }

    private async Task CommitResolvedLinesAsync(Context ctx, Action<List<string>> mutate)
    {
        var clone = await CloneAsync(ctx);
        var path = Path.Combine(clone, "file.txt");
        var lines = (await File.ReadAllLinesAsync(path)).ToList();
        mutate(lines);
        await File.WriteAllLinesAsync(path, lines);
        await TestSupport.RunGit(clone, "add", "-A");
        await TestSupport.RunGit(clone, "commit", "-m", "resolved");
        await TestSupport.RunGit(clone, "push", "origin", "HEAD:resolved");
    }

    private async Task<string> CloneAsync(Context ctx)
    {
        var clone = Path.Combine(_workspace, "clone-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", ctx.GitHost.GetRepoPath(ctx.RepoId), clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        return clone;
    }

    private sealed record Context(LocalGitHost GitHost, string RepoId);
}
