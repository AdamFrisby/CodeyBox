using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Exercises the actual <see cref="SandboxWorkBranchPublisher"/> publication
/// contract against disposable real Git repositories (a bare "remote" plus
/// clones standing in for the sandbox and rival writers). Covers the resumed
/// meaningful-checkpoint/no-new-change matrix: fast-forward, stale source,
/// divergent non-conflicting histories, conflicting histories, a target that
/// advances between fetch and push, detached-HEAD/named-ref mismatch, and
/// failure or cancellation — proving bounded commands, no force pushes, both
/// original tips still reachable, no silently dropped changes, and typed
/// failures. Requires git on PATH.
/// </summary>
public sealed class SandboxWorkBranchPublishTests : IDisposable
{
    private readonly string _workspace;

    public SandboxWorkBranchPublishTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-publish-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch
        {
            // Best-effort test teardown.
        }
    }

    [Fact]
    public async Task FastForward_PublishesWithoutReconcile()
    {
        using var fixture = await PublishFixture.CreateAsync(_workspace, "work");
        await fixture.WriteAndCommitAsync(
            fixture.SandboxDir, "source.txt", "source change\n", "agent change");
        var sandboxHead = await fixture.RevParseAsync(fixture.SandboxDir, "HEAD");

        var result = await fixture.PublishAsync("work");

        Assert.False(result.Reconciled);
        Assert.Equal(sandboxHead, result.PublishedSha);
        Assert.Equal(sandboxHead, result.SourceSha);
        Assert.Null(result.TargetSha);
        Assert.Equal(sandboxHead, await fixture.RevParseAsync(fixture.RemoteDir, "refs/heads/work"));
        Assert.DoesNotContain(fixture.Commands, argv => IsGitSubcommand(argv, "fetch"));
        Assert.DoesNotContain(fixture.Commands, argv => IsGitSubcommand(argv, "rebase"));
        fixture.AssertPublicationDiscipline();
    }

    [Fact]
    public async Task StaleSource_FastForwardsToTargetWithoutLosingHistory()
    {
        using var fixture = await PublishFixture.CreateAsync(_workspace, "work");
        var targetTip = await fixture.OtherWriterCommitAsync(
            "work", "target.txt", "target change\n", "target advance");
        // The sandbox never saw the advance: its tracking ref is stale and its
        // HEAD has no new commits of its own.
        var staleHead = await fixture.RevParseAsync(fixture.SandboxDir, "HEAD");

        var result = await fixture.PublishAsync("work");

        Assert.True(result.Reconciled);
        Assert.Equal(targetTip, result.PublishedSha);
        Assert.Equal(staleHead, result.SourceSha);
        Assert.Equal(targetTip, result.TargetSha);
        Assert.Equal(targetTip, await fixture.RevParseAsync(fixture.RemoteDir, "refs/heads/work"));
        // No source commits existed, so nothing could be dropped; the target
        // tip is published verbatim.
        fixture.AssertPublicationDiscipline();
    }

    [Fact]
    public async Task DivergentNonConflicting_RebasesAndKeepsBothSides()
    {
        using var fixture = await PublishFixture.CreateAsync(_workspace, "work");
        var targetTip = await fixture.OtherWriterCommitAsync(
            "work", "target.txt", "target change\n", "target advance");
        await fixture.WriteAndCommitAsync(
            fixture.SandboxDir, "source.txt", "source change\n", "agent change");
        var sourceTip = await fixture.RevParseAsync(fixture.SandboxDir, "HEAD");

        var result = await fixture.PublishAsync("work");

        Assert.True(result.Reconciled);
        Assert.Equal(targetTip, result.TargetSha);
        Assert.Equal(sourceTip, result.SourceSha);
        var published = result.PublishedSha;
        Assert.Equal(published, await fixture.RevParseAsync(fixture.RemoteDir, "refs/heads/work"));
        // Target history retained: the observed target is an ancestor of what
        // was published.
        Assert.True(await fixture.IsAncestorAsync(targetTip, published));
        // Neither side's content was dropped by the rebase.
        Assert.Equal("target change\n", await fixture.ShowFileAsync(published, "target.txt"));
        Assert.Equal("source change\n", await fixture.ShowFileAsync(published, "source.txt"));
        // Both original tips are still reachable via the preservation refs.
        Assert.Equal(sourceTip, await fixture.RevParseAsync(
            fixture.SandboxDir, "refs/codeybox/preserved/source-tip"));
        Assert.Equal(targetTip, await fixture.RevParseAsync(
            fixture.SandboxDir, "refs/codeybox/preserved/target-tip"));
        fixture.AssertPublicationDiscipline();
    }

    [Fact]
    public async Task ConflictingHistory_ThrowsTypedConflictAndPreservesBothTips()
    {
        using var fixture = await PublishFixture.CreateAsync(_workspace, "work");
        var targetTip = await fixture.OtherWriterCommitAsync(
            "work", "shared.txt", "target side\n", "target advance");
        await fixture.WriteAndCommitAsync(
            fixture.SandboxDir, "shared.txt", "source side\n", "agent change");
        var sourceTip = await fixture.RevParseAsync(fixture.SandboxDir, "HEAD");

        var conflict = await Assert.ThrowsAsync<SandboxPushReconcileConflictException>(
            () => fixture.PublishAsync("work"));

        Assert.Equal("work", conflict.Branch);
        Assert.Equal("rebase", conflict.Strategy);
        Assert.Equal("rebase", conflict.Stage);
        Assert.Equal(sourceTip, conflict.SourceSha);
        Assert.Equal(targetTip, conflict.TargetSha);
        Assert.True(SandboxPushReconcileConflictException.TryFindIn(conflict, out var found));
        Assert.Same(conflict, found);
        // The failed rebase was aborted: no rebase state remains and the
        // sandbox HEAD is still the original source tip.
        Assert.False(await fixture.RefExistsAsync(fixture.SandboxDir, "REBASE_HEAD"));
        Assert.Equal(sourceTip, await fixture.RevParseAsync(fixture.SandboxDir, "HEAD"));
        Assert.Equal("source side\n", await fixture.ShowFileAsync(sourceTip, "shared.txt"));
        // The remote target is untouched.
        Assert.Equal(targetTip, await fixture.RevParseAsync(fixture.RemoteDir, "refs/heads/work"));
        // Both original tips stay reachable via the preservation refs.
        Assert.Equal(sourceTip, await fixture.RevParseAsync(
            fixture.SandboxDir, "refs/codeybox/preserved/source-tip"));
        Assert.Equal(targetTip, await fixture.RevParseAsync(
            fixture.SandboxDir, "refs/codeybox/preserved/target-tip"));
        fixture.AssertPublicationDiscipline();
    }

    [Fact]
    public async Task MovingTarget_ThrowsTypedConflictWithoutLoopingOrOverwriting()
    {
        using var fixture = await PublishFixture.CreateAsync(_workspace, "work");
        await fixture.OtherWriterCommitAsync(
            "work", "target.txt", "target change\n", "target advance");
        await fixture.WriteAndCommitAsync(
            fixture.SandboxDir, "source.txt", "source change\n", "agent change");
        var sourceTip = await fixture.RevParseAsync(fixture.SandboxDir, "HEAD");

        string? movedTarget = null;
        fixture.BeforeSecondPush = async () =>
        {
            // A rival writer lands between our fetch/rebase and our final push.
            movedTarget = await fixture.OtherWriterCommitAsync(
                "work", "rival.txt", "rival change\n", "rival advance");
        };

        var conflict = await Assert.ThrowsAsync<SandboxPushReconcileConflictException>(
            () => fixture.PublishAsync("work"));

        Assert.NotNull(movedTarget);
        Assert.Equal("moving-target", conflict.Strategy);
        Assert.Equal("push-after-reconcile", conflict.Stage);
        Assert.Equal(sourceTip, conflict.SourceSha);
        // Exactly one rebase: the publisher stops instead of chasing the
        // moving target.
        Assert.Equal(1, fixture.Commands.Count(argv => IsGitSubcommand(argv, "rebase") && !argv.Contains("--abort")));
        // The rival tip won the race and was not overwritten.
        Assert.Equal(movedTarget, await fixture.RevParseAsync(fixture.RemoteDir, "refs/heads/work"));
        // The original source tip is still reachable for a retry.
        Assert.Equal(sourceTip, await fixture.RevParseAsync(
            fixture.SandboxDir, "refs/codeybox/preserved/source-tip"));
        fixture.AssertPublicationDiscipline();
    }

    [Fact]
    public async Task DetachedHead_PublishesHeadRatherThanStaleNamedRef()
    {
        using var fixture = await PublishFixture.CreateAsync(_workspace, "work");
        var targetTip = await fixture.OtherWriterCommitAsync(
            "work", "target.txt", "target change\n", "target advance");
        await fixture.WriteAndCommitAsync(
            fixture.SandboxDir, "source.txt", "source change\n", "agent change");
        var sourceTip = await fixture.RevParseAsync(fixture.SandboxDir, "HEAD");
        // Detach HEAD at the agent's tip first, then leave the local branch
        // ref behind at the base — the resumed-checkpoint shape where
        // pushing the named ref would silently publish stale content.
        await PublishFixture.RunGitAsync(fixture.SandboxDir, "checkout", "--detach", "HEAD");
        var baseSha = await fixture.RevParseAsync(fixture.SandboxDir, "HEAD~1");
        await PublishFixture.RunGitAsync(fixture.SandboxDir, "update-ref", "refs/heads/work", baseSha);

        var result = await fixture.PublishAsync("work");

        Assert.True(result.Reconciled);
        Assert.Equal(sourceTip, result.SourceSha);
        Assert.Equal(targetTip, result.TargetSha);
        var published = result.PublishedSha;
        Assert.Equal("source change\n", await fixture.ShowFileAsync(published, "source.txt"));
        Assert.Equal("target change\n", await fixture.ShowFileAsync(published, "target.txt"));
        fixture.AssertPublicationDiscipline();
    }

    [Fact]
    public async Task CancelledMidReconcile_AbortsWithoutClearingPreservation()
    {
        using var fixture = await PublishFixture.CreateAsync(_workspace, "work");
        await fixture.OtherWriterCommitAsync(
            "work", "target.txt", "target change\n", "target advance");
        await fixture.WriteAndCommitAsync(
            fixture.SandboxDir, "source.txt", "source change\n", "agent change");
        var sourceTip = await fixture.RevParseAsync(fixture.SandboxDir, "HEAD");
        using var cts = new CancellationTokenSource();
        fixture.BeforeRebase = () => cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.PublishAsync("work", cts.Token));

        // Cancellation propagates (never converted to success) and both tips
        // stay reachable: the source HEAD is untouched and the pins made
        // before the rebase survive.
        Assert.Equal(sourceTip, await fixture.RevParseAsync(fixture.SandboxDir, "HEAD"));
        Assert.Equal(sourceTip, await fixture.RevParseAsync(
            fixture.SandboxDir, "refs/codeybox/preserved/source-tip"));
        fixture.AssertPublicationDiscipline();
    }

    [Fact]
    public async Task FetchFailure_ThrowsSanitizedErrorWithoutCredentials()
    {
        using var fixture = await PublishFixture.CreateAsync(_workspace, "work");
        await fixture.OtherWriterCommitAsync(
            "work", "target.txt", "target change\n", "target advance");
        await fixture.WriteAndCommitAsync(
            fixture.SandboxDir, "source.txt", "source change\n", "agent change");
        const string fakeToken = "ghp_FAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKE1234";
        fixture.FetchFailureStderr =
            $"fatal: unable to access 'https://x-access-token:{fakeToken}@example.invalid/repo.git/': Could not resolve host";

        var error = await Assert.ThrowsAsync<SandboxWorkBranchPublishException>(
            () => fixture.PublishAsync("work"));

        Assert.Equal("work", error.Branch);
        Assert.Equal("fetch", error.Stage);
        Assert.True(SandboxWorkBranchPublisher.IsPublicationFailure(error));
        Assert.False(
            SandboxPushReconcileConflictException.TryFindIn(error, out _),
            "a fetch failure is not a content conflict and must not match the conflict contract");
        Assert.Contains("stage 'fetch'", error.Message);
        Assert.DoesNotContain(fakeToken, error.Message);
        Assert.DoesNotContain(fakeToken, error.Detail);
        fixture.AssertPublicationDiscipline();
    }

    [Fact]
    public async Task DirectPushWithoutReconcile_FailsOnDivergence()
    {
        // Regression demonstration: the pre-fix resumed-checkpoint sequence —
        // a single `push HEAD:<branch>` with no fetch/rebase — fails with a
        // non-fast-forward rejection once the target has advanced, which is
        // exactly the sandbox-to-bare publication gap this fix closes. The
        // reconciling publisher then publishes the same divergence while
        // preserving both sides.
        using var fixture = await PublishFixture.CreateAsync(_workspace, "work");
        var targetTip = await fixture.OtherWriterCommitAsync(
            "work", "target.txt", "target change\n", "target advance");
        await fixture.WriteAndCommitAsync(
            fixture.SandboxDir, "source.txt", "source change\n", "agent change");
        var sourceTip = await fixture.RevParseAsync(fixture.SandboxDir, "HEAD");

        var (code, stdout, stderr) = await PublishFixture.RunGitNoThrowAsync(
            fixture.SandboxDir, "push", "origin", "HEAD:work");

        Assert.NotEqual(0, code);
        // The exact reason phrase depends on whether the sandbox's
        // remote-tracking ref observed the advance: a stale tracker (the
        // resumed-checkpoint shape, which never fetched after the target
        // moved) reports "(fetch first)"; a fresh one reports
        // "(non-fast-forward)". Both are the non-fast-forward family the
        // reconciler classifies.
        var combined = stdout + "\n" + stderr;
        Assert.Contains("[rejected]", combined, StringComparison.Ordinal);
        Assert.True(
            combined.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("fetch first", StringComparison.OrdinalIgnoreCase),
            $"expected a non-fast-forward rejection, got:{System.Environment.NewLine}{combined}");

        // The SUT reconciles the same divergence the raw push could not.
        var result = await fixture.PublishAsync("work");

        Assert.True(result.Reconciled);
        Assert.Equal(sourceTip, result.SourceSha);
        Assert.Equal(targetTip, result.TargetSha);
        var published = result.PublishedSha;
        Assert.Equal(published, await fixture.RevParseAsync(fixture.RemoteDir, "refs/heads/work"));
        Assert.True(await fixture.IsAncestorAsync(targetTip, published));
        Assert.Equal("target change\n", await fixture.ShowFileAsync(published, "target.txt"));
        Assert.Equal("source change\n", await fixture.ShowFileAsync(published, "source.txt"));
        fixture.AssertPublicationDiscipline();
    }

    private static bool IsGitSubcommand(IReadOnlyList<string> argv, string subcommand)
    {
        // Publisher argv has the shape ["git", "-C", <dir>, ...<subcommand>...];
        // the rebase invocation carries "-c" overrides before the subcommand,
        // so match the subcommand element anywhere past "git".
        return argv.Count > 1 && argv[0] == "git" && argv.Skip(1).Contains(subcommand);
    }

    private sealed class PublishFixture : IDisposable
    {
        private readonly List<string[]> _commands = new();
        private readonly object _gate = new();

        private PublishFixture(string workspace, string remoteDir, string sandboxDir, string writerDir)
        {
            RemoteDir = remoteDir;
            SandboxDir = sandboxDir;
            WriterDir = writerDir;
            Workspace = workspace;
        }

        public string Workspace { get; }
        public string RemoteDir { get; }
        public string SandboxDir { get; }
        public string WriterDir { get; }

        /// <summary>Hook run just before the publisher's final push exec.</summary>
        public Func<Task>? BeforeSecondPush { get; set; }

        /// <summary>Hook run just before the publisher's rebase exec.</summary>
        public Action? BeforeRebase { get; set; }

        /// <summary>When set, the fetch exec fails with this stderr.</summary>
        public string? FetchFailureStderr { get; set; }

        public IReadOnlyList<IReadOnlyList<string>> Commands
        {
            get
            {
                lock (_gate)
                    return _commands.Select(argv => (IReadOnlyList<string>)argv.ToArray()).ToArray();
            }
        }

        public static async Task<PublishFixture> CreateAsync(string workspace, string branch)
        {
            var root = Path.Combine(workspace, "fx-" + Guid.NewGuid().ToString("N")[..8]);
            var remoteDir = Path.Combine(root, "remote.git");
            var sandboxDir = Path.Combine(root, "sandbox");
            var writerDir = Path.Combine(root, "writer");
            Directory.CreateDirectory(root);

            await RunGitAsync(root, "init", "--bare", "-q", remoteDir);
            await RunGitAsync(root, "clone", "-q", remoteDir, sandboxDir);
            await ConfigureIdentityAsync(sandboxDir);
            await RunGitAsync(root, "clone", "-q", remoteDir, writerDir);
            await ConfigureIdentityAsync(writerDir);

            // Seed main on the remote so clones have a base to branch from.
            await File.WriteAllTextAsync(Path.Combine(sandboxDir, "README.md"), "seed\n");
            await RunGitAsync(sandboxDir, "add", "README.md");
            await RunGitAsync(sandboxDir, "commit", "-q", "-m", "initial");
            await RunGitAsync(sandboxDir, "push", "-q", "origin", "HEAD:main");
            await RunGitAsync(sandboxDir, "fetch", "-q", "origin");
            await RunGitAsync(sandboxDir, "checkout", "-q", "-B", branch, "origin/main");
            await RunGitAsync(sandboxDir, "push", "-q", "origin", $"HEAD:{branch}");

            var fixture = new PublishFixture(workspace, remoteDir, sandboxDir, writerDir);
            return fixture;
        }

        public void Dispose()
        {
        }

        public Task<SandboxWorkBranchPublication> PublishAsync(string branch, CancellationToken ct = default) =>
            SandboxWorkBranchPublisher.PublishHeadAsync(ExecAsync, branch, NullLogger.Instance, ct);

        public async Task<string> OtherWriterCommitAsync(string branch, string file, string content, string message)
        {
            await RunGitAsync(WriterDir, "fetch", "-q", "origin");
            await RunGitAsync(WriterDir, "checkout", "-q", "-B", branch, $"origin/{branch}");
            await File.WriteAllTextAsync(Path.Combine(WriterDir, file), content);
            await RunGitAsync(WriterDir, "add", file);
            await RunGitAsync(WriterDir, "commit", "-q", "-m", message);
            await RunGitAsync(WriterDir, "push", "-q", "origin", $"HEAD:{branch}");
            return await RevParseAsync(WriterDir, "HEAD");
        }

        public async Task WriteAndCommitAsync(string repoDir, string file, string content, string message)
        {
            await File.WriteAllTextAsync(Path.Combine(repoDir, file), content);
            await RunGitAsync(repoDir, "add", file);
            await RunGitAsync(repoDir, "commit", "-q", "-m", message);
        }

        public async Task<string> RevParseAsync(string repoDir, string rev)
        {
            var (code, stdout, stderr) = await RunGitNoThrowAsync(repoDir, "rev-parse", "--verify", rev);
            if (code != 0)
                throw new InvalidOperationException($"rev-parse {rev} failed: {stderr}");
            return stdout.Trim();
        }

        public async Task<bool> RefExistsAsync(string repoDir, string rev)
        {
            var (code, _, _) = await RunGitNoThrowAsync(repoDir, "rev-parse", "--verify", rev);
            return code == 0;
        }

        public async Task<bool> IsAncestorAsync(string ancestor, string descendant)
        {
            var (code, _, _) = await RunGitNoThrowAsync(
                SandboxDir, "merge-base", "--is-ancestor", ancestor, descendant);
            return code == 0;
        }

        public async Task<string> ShowFileAsync(string rev, string path)
        {
            var (code, stdout, stderr) = await RunGitNoThrowAsync(SandboxDir, "show", $"{rev}:{path}");
            if (code != 0)
                throw new InvalidOperationException($"show {rev}:{path} failed: {stderr}");
            return stdout;
        }

        public void AssertPublicationDiscipline()
        {
            var commands = Commands;
            Assert.True(
                commands.Count <= SandboxWorkBranchPublisher.MaxGitCommands,
                $"publication issued {commands.Count} git commands, exceeding the bound of {SandboxWorkBranchPublisher.MaxGitCommands}");
            foreach (var argv in commands)
            {
                Assert.True(argv.Count > 0 && argv[0] == "git", "publisher must only run git");
                Assert.DoesNotContain(argv, element =>
                    element == "--force" || element == "--force-with-lease" || element == "--delete");
                var pushIndex = Array.IndexOf(argv.ToArray(), "push");
                if (pushIndex >= 0)
                    Assert.DoesNotContain(argv.Skip(pushIndex), element => element.StartsWith("+", StringComparison.Ordinal));
                Assert.DoesNotContain("reset", argv);
            }
        }

        private async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct)
        {
            var argv = exec.Argv.ToArray();
            if (argv.Length == 0 || argv[0] != "git")
                throw new InvalidOperationException("test bridge only supports git commands");
            var mapped = argv.Select(element =>
                string.Equals(element, SandboxConventions.WorkDir, StringComparison.Ordinal)
                    ? SandboxDir
                    : element).ToArray();
            lock (_gate)
                _commands.Add(mapped.ToArray());
            var priorPushCount = CountSubcommand("push") - (IsSubcommand(mapped, "push") ? 1 : 0);

            if (IsSubcommand(mapped, "rebase") && !mapped.Contains("--abort"))
                BeforeRebase?.Invoke();

            if (IsSubcommand(mapped, "fetch") && FetchFailureStderr is not null)
                return new SandboxExecResult(128, string.Empty, FetchFailureStderr);

            if (IsSubcommand(mapped, "push") && priorPushCount >= 1 && BeforeSecondPush is not null)
            {
                var hook = BeforeSecondPush;
                BeforeSecondPush = null;
                await hook();
            }

            ct.ThrowIfCancellationRequested();
            return await RunProcessAsync("git", mapped.Skip(1).ToArray(), SandboxDir, ct);
        }

        private int CountSubcommand(string subcommand)
        {
            lock (_gate)
                return _commands.Count(argv => IsSubcommand(argv, subcommand));
        }

        private static bool IsSubcommand(string[] argv, string subcommand) =>
            argv.Length > 1 && argv[0] == "git" && argv.Skip(1).Contains(subcommand);

        public static async Task RunGitAsync(string cwd, params string[] args)
        {
            var (code, _, stderr) = await RunGitNoThrowAsync(cwd, args);
            if (code != 0)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        }

        public static Task<(int code, string stdout, string stderr)> RunGitNoThrowAsync(string cwd, params string[] args) =>
            RunProcessTupleAsync("git", args, cwd, CancellationToken.None);

        private static async Task ConfigureIdentityAsync(string repoDir)
        {
            await RunGitAsync(repoDir, "config", "user.email", "test@example.invalid");
            await RunGitAsync(repoDir, "config", "user.name", "CodeyBox Test");
        }

        private static async Task<SandboxExecResult> RunProcessAsync(
            string fileName, string[] args, string workingDirectory, CancellationToken ct)
        {
            var (code, stdout, stderr) = await RunProcessTupleAsync(fileName, args, workingDirectory, ct);
            return new SandboxExecResult(code, stdout, stderr);
        }

        private static async Task<(int code, string stdout, string stderr)> RunProcessTupleAsync(
            string fileName, string[] args, string workingDirectory, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
            psi.Environment["GIT_EDITOR"] = "true";
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var process = Process.Start(psi)!;
            try
            {
                var stdout = await process.StandardOutput.ReadToEndAsync(ct);
                var stderr = await process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                return (process.ExitCode, stdout, stderr);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort kill on cancellation.
                }
                throw;
            }
        }
    }
}
