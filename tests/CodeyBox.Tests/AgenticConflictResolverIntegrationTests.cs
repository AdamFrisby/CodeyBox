using System.Diagnostics;
using System.Text;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Integration coverage for <see cref="AgenticConflictResolver"/> against an
/// actually-seeded git conflict inside a real <see cref="ProcessSandboxProvider"/>.
/// The unit tests in AgenticConflictResolverTests use an in-memory
/// ConflictSandbox that simulates git semantics; they passed even when the
/// resolver-sandbox setup in PipelineRunner shipped broken (creds + network
/// gated off when conflicts present). These tests exercise the full sandbox
/// boundary so a future regression in setup, mount permissions, or sandbox
/// exec wiring would be caught here.
/// </summary>
public sealed class AgenticConflictResolverIntegrationTests
{
    /// <summary>
    /// Seeds a real git repository in mid-rebase with a conflicted file, drives
    /// <see cref="AgenticConflictResolver.ResolveAsync"/> against a real
    /// <see cref="ProcessSandbox"/>, and asserts the conflict resolves
    /// end-to-end (no unmerged paths, no markers, agent staged the resolution).
    /// </summary>
    [SkippableFact]
    public async Task ResolveAsync_RealMidRebaseConflict_AgentResolvesViaSandbox()
    {
        Skip.IfNot(HasGit(), "git not on PATH");

        using var workspace = new TempWorkspace();
        var conflictRepo = await SeedConflictedRebaseAsync(workspace.Root);

        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts =
            [
                new SandboxMount { SandboxPath = SandboxConventions.WorkDir, Tmpfs = true },
                new SandboxMount { SandboxPath = "/seed", HostPath = conflictRepo, ReadOnly = false },
            ],
            Network = new SandboxNetworkPolicy { AllowedHosts = [] },
            WorkingDirectory = SandboxConventions.WorkDir,
        };
        await using var sandbox = await provider.CreateAsync(spec);

        // Clone the seeded conflict repo into the sandbox workdir. The seed was
        // left in mid-rebase state by SeedConflictedRebaseAsync; cloning carries
        // committed history but not the in-progress rebase, so we re-execute
        // `git rebase main` here to produce conflicted unmerged paths inside
        // the sandbox the resolver will be invoked against.
        await ExecOrThrow(sandbox, "git", "clone", "/seed", SandboxConventions.WorkDir);
        await ExecOrThrow(sandbox, "git", "-C", SandboxConventions.WorkDir, "config", "user.email", "t@l");
        await ExecOrThrow(sandbox, "git", "-C", SandboxConventions.WorkDir, "config", "user.name", "T");
        await ExecOrThrow(sandbox, "git", "-C", SandboxConventions.WorkDir, "checkout", "feature");
        var rebase = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "rebase", "origin/main"],
        });
        Assert.False(rebase.Success,
            $"expected the rebase to leave the working tree in a conflict state. stdout={rebase.Stdout} stderr={rebase.Stderr}");

        var diffU = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "diff", "--name-only", "--diff-filter=U"],
        });
        Assert.Equal(0, diffU.ExitCode);
        Assert.Equal("conflict.txt", diffU.Stdout.Trim());

        var resolver = new AgenticConflictResolver();
        var runner = new MarkerStrippingAgentRunner();
        var workItemId = WorkItemId.New();
        var result = await resolver.ResolveAsync(
            sandbox,
            SandboxConventions.WorkDir,
            workItemId,
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(1, runner.InvocationCount);
        Assert.Equal(["conflict.txt"], result.ConflictFiles.ToArray());

        var postDiff = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "diff", "--name-only", "--diff-filter=U"],
        });
        Assert.Equal(0, postDiff.ExitCode);
        Assert.Equal("", postDiff.Stdout.Trim());

        var grepMarkers = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["grep", "-n", "-E", "^(<<<<<<<|=======|>>>>>>>)", $"{SandboxConventions.WorkDir}/conflict.txt"],
        });
        Assert.NotEqual(0, grepMarkers.ExitCode);

        var staged = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "diff", "--name-only", "--cached"],
        });
        Assert.Contains("conflict.txt", staged.Stdout);
    }

    /// <summary>
    /// Pins the cross-kind candidate hook: when the resolver runs a candidate
    /// whose <see cref="AgentCredential.Files"/> are not yet on disk in the
    /// sandbox (because the sandbox was provisioned for a different primary),
    /// the orchestrator-supplied materialiser delegate is invoked before
    /// <see cref="IAgentRunner.RunAsync"/>. Without this, a sandbox bound to
    /// the primary's env-credentials cannot authenticate a fallback CLI that
    /// reads file-based credentials.
    /// </summary>
    [SkippableFact]
    public async Task ResolveAsync_CredentialFileMaterialiser_InvokedBeforeRunner()
    {
        Skip.IfNot(HasGit(), "git not on PATH");

        using var workspace = new TempWorkspace();
        var conflictRepo = await SeedConflictedRebaseAsync(workspace.Root);

        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts =
            [
                new SandboxMount { SandboxPath = SandboxConventions.WorkDir, Tmpfs = true },
                new SandboxMount { SandboxPath = "/seed", HostPath = conflictRepo, ReadOnly = false },
            ],
            Network = new SandboxNetworkPolicy { AllowedHosts = [] },
            WorkingDirectory = SandboxConventions.WorkDir,
        };
        await using var sandbox = await provider.CreateAsync(spec);

        await ExecOrThrow(sandbox, "git", "clone", "/seed", SandboxConventions.WorkDir);
        await ExecOrThrow(sandbox, "git", "-C", SandboxConventions.WorkDir, "config", "user.email", "t@l");
        await ExecOrThrow(sandbox, "git", "-C", SandboxConventions.WorkDir, "config", "user.name", "T");
        await ExecOrThrow(sandbox, "git", "-C", SandboxConventions.WorkDir, "checkout", "feature");
        var rebase = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "rebase", "origin/main"],
        });
        Assert.False(rebase.Success);

        var materialiserCalls = new List<(string SandboxId, AgentCredential Credential)>();
        Func<ISandbox, AgentCredential, CancellationToken, Task> materialiser = (sbx, cred, _) =>
        {
            materialiserCalls.Add((sbx.Id, cred));
            return Task.CompletedTask;
        };
        var resolver = new AgenticConflictResolver(credentialFileMaterialiser: materialiser);

        var fallbackCredential = new AgentCredential(
            new AgentKind("fallback"),
            EnvironmentVariables: new Dictionary<string, string>(),
            Files: new Dictionary<string, string> { [".config/fallback/creds.json"] = "{}" });
        var runner = new MarkerStrippingAgentRunner { Kind = new AgentKind("fallback") };
        var result = await resolver.ResolveAsync(
            sandbox,
            SandboxConventions.WorkDir,
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(runner, fallbackCredential)],
            CancellationToken.None);

        Assert.True(result.Success, result.Summary);
        Assert.Single(materialiserCalls);
        Assert.Equal(sandbox.Id, materialiserCalls[0].SandboxId);
        Assert.Same(fallbackCredential, materialiserCalls[0].Credential);
    }

    /// <summary>
    /// Capture path on failure: when the resolver agent exits non-zero, the
    /// stdout/stderr the runner returned MUST end up in the failure
    /// <see cref="AgenticConflictResolverResult.Summary"/> trail. This pins the
    /// Part-1 diagnostic capture so a regression that drops stdout/stderr
    /// from the failure path is caught (the prior bug shape:
    /// "agent exited 1" with no further detail, impossible to diagnose).
    /// </summary>
    [Fact]
    public async Task ResolveAsync_AgentFailsWithStderr_FailureSummaryIncludesStderr()
    {
        var sandbox = new AgenticConflictResolverTests.ConflictSandbox();
        sandbox.AddConflictedFile("conflict.txt",
            "<<<<<<< HEAD\nmain\n=======\nfeature\n>>>>>>> feature\n");

        var runner = new StubFailingAgentRunner(
            stdout: "agent printed a banner",
            stderr: "missing ANTHROPIC_API_KEY; aborting");

        var resolver = new AgenticConflictResolver(
            new AgenticConflictResolverOptionsSnapshot(new AgenticConflictResolverOptions { MaxIterations = 1 }));
        var result = await resolver.ResolveAsync(
            sandbox,
            "/work",
            WorkItemId.New(),
            new AgenticConflictResolverContext("main", "feature", AgenticConflictResolverOperation.Rebase),
            [new AgenticConflictResolverCandidate(runner, Credential: null)],
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("missing ANTHROPIC_API_KEY", result.Summary, StringComparison.Ordinal);
        Assert.Equal("agent printed a banner", result.Stdout);
        Assert.Equal("missing ANTHROPIC_API_KEY; aborting", result.Stderr);
    }

    private static async Task<string> SeedConflictedRebaseAsync(string workspace)
    {
        var repo = Path.Combine(workspace, "conflict-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        await RunGit(repo, "init", "-b", "main");
        await RunGit(repo, "config", "user.email", "seed@example");
        await RunGit(repo, "config", "user.name", "seed");
        await File.WriteAllTextAsync(Path.Combine(repo, "conflict.txt"), "original\n");
        await RunGit(repo, "add", "conflict.txt");
        await RunGit(repo, "commit", "-m", "base");

        await RunGit(repo, "checkout", "-b", "feature");
        await File.WriteAllTextAsync(Path.Combine(repo, "conflict.txt"), "feature-edit\n");
        await RunGit(repo, "commit", "-am", "feature change");

        await RunGit(repo, "checkout", "main");
        await File.WriteAllTextAsync(Path.Combine(repo, "conflict.txt"), "main-edit\n");
        await RunGit(repo, "commit", "-am", "main change");
        await RunGit(repo, "checkout", "feature");
        return repo;
    }

    private static async Task ExecOrThrow(ISandbox sandbox, params string[] argv)
    {
        var result = await sandbox.ExecAsync(new SandboxExec { Argv = argv });
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"sandbox exec failed (exit {result.ExitCode}): {string.Join(' ', argv)}\nstdout: {result.Stdout}\nstderr: {result.Stderr}");
    }

    private static bool HasGit()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {p.ExitCode}): {stderr}");
    }

    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "codeybox-resolver-int-" + Guid.NewGuid().ToString("N")[..12]);
        public TempWorkspace() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// In-test runner that does what a real coding agent would do: read each
    /// conflicted file via the sandbox, drop the conflict markers (keep both
    /// sides interleaved), write the result back, and <c>git add</c>. Exercises
    /// the same sandbox.ExecAsync surface a real CLI agent would.
    /// </summary>
    private sealed class MarkerStrippingAgentRunner : IAgentRunner
    {
        public AgentKind Kind { get; init; } = new("test-resolver");
        public int InvocationCount { get; private set; }

        public async Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
        {
            InvocationCount++;

            var diffU = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "diff", "--name-only", "--diff-filter=U"],
            }, ct);
            if (diffU.ExitCode != 0)
                return new AgentResult(false, "diff failed", diffU.Stdout, diffU.Stderr);

            var files = diffU.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var file in files)
            {
                var path = $"{workingDirectory}/{file}";
                var read = await sandbox.ExecAsync(new SandboxExec { Argv = ["cat", path] }, ct);
                if (read.ExitCode != 0)
                    return new AgentResult(false, $"read failed: {file}", read.Stdout, read.Stderr);

                var resolved = StripMarkers(read.Stdout);
                var write = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["sh", "-c", "cat > \"$0\"", path],
                    Stdin = resolved,
                }, ct);
                if (write.ExitCode != 0)
                    return new AgentResult(false, $"write failed: {file}", write.Stdout, write.Stderr);

                var add = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["git", "-C", workingDirectory, "add", "--", file],
                }, ct);
                if (add.ExitCode != 0)
                    return new AgentResult(false, $"add failed: {file}", add.Stdout, add.Stderr);
            }

            return new AgentResult(true, $"resolved {files.Length} file(s)", null, null);
        }

        private static string StripMarkers(string content)
        {
            var sb = new StringBuilder(content.Length);
            var skipping = false;
            var inSecondHalf = false;
            foreach (var line in content.Split('\n'))
            {
                if (line.StartsWith("<<<<<<<", StringComparison.Ordinal))
                {
                    skipping = false;
                    inSecondHalf = false;
                    continue;
                }
                if (line.StartsWith("|||||||", StringComparison.Ordinal))
                {
                    skipping = true;
                    continue;
                }
                if (line.StartsWith("=======", StringComparison.Ordinal))
                {
                    skipping = false;
                    inSecondHalf = true;
                    continue;
                }
                if (line.StartsWith(">>>>>>>", StringComparison.Ordinal))
                {
                    skipping = false;
                    inSecondHalf = false;
                    continue;
                }
                if (skipping) continue;
                _ = inSecondHalf;
                sb.Append(line).Append('\n');
            }
            return sb.ToString();
        }
    }

    private sealed class StubFailingAgentRunner : IAgentRunner
    {
        private readonly string _stdout;
        private readonly string _stderr;

        public StubFailingAgentRunner(string stdout, string stderr)
        {
            _stdout = stdout;
            _stderr = stderr;
        }

        public AgentKind Kind { get; init; } = new("stub-failing");

        public Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
            => Task.FromResult(new AgentResult(false, "agent exited 1", _stdout, _stderr));
    }
}
