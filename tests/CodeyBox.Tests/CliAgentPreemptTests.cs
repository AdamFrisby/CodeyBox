using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class CliAgentPreemptTests
{
    [Fact]
    public async Task RequestPreempt_CapturesConfiguredScratchpadOnly()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(SandboxSpecWithAgentTurnScratchpad());
        var runner = new TestCliRunner();

        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                "set -e; mkdir -p \"$HOME/.testagent/scratch\" \"$HOME/.testagent/private\"; printf '%s\n' 'resume this todo' > \"$HOME/.testagent/scratch/todo.txt\"; printf '%s\n' secret > \"$HOME/.testagent/private/auth.json\""
            ],
        });
        Assert.True(write.Success, write.Stderr);

        await runner.RequestPreemptAsync(sandbox, "/work");

        var archive = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "tar -tzf \"$1\" | sort", "archive-list", SandboxConventions.AgentTurnScratchpadArchivePath],
            WorkingDirectory = "/work",
        });
        Assert.True(archive.Success, archive.Stderr);
        Assert.Contains("manifest.txt", archive.Stdout);
        Assert.Contains("manifest.tsv", archive.Stdout);
        Assert.Contains("home/.testagent/scratch/todo.txt", archive.Stdout);
        Assert.DoesNotContain("home/.testagent/private/auth.json", archive.Stdout);

        var captured = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "tmp=$(mktemp -d); tar -xzf \"$1\" -C \"$tmp\"; cat \"$tmp/home/.testagent/scratch/todo.txt\"", "archive-read", SandboxConventions.AgentTurnScratchpadArchivePath],
            WorkingDirectory = "/work",
        });
        Assert.True(captured.Success, captured.Stderr);
        Assert.Contains("resume this todo", captured.Stdout);

        var manifest = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "tar -xOzf \"$1\" ./manifest.txt 2>/dev/null || tar -xOzf \"$1\" manifest.txt", "manifest-read", SandboxConventions.AgentTurnScratchpadArchivePath],
            WorkingDirectory = "/work",
        });
        Assert.True(manifest.Success, manifest.Stderr);
        Assert.Contains("capturing home/.testagent/scratch", manifest.Stdout);
        Assert.DoesNotContain("secret", manifest.Stdout);
    }

    [Fact]
    public async Task RequestPreempt_DoesNotFollowRepositoryCodeyboxSymlink()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(SandboxSpecWithAgentTurnScratchpad());
        var runner = new TestCliRunner();

        var arrange = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                "set -e; mkdir -p legacy-target \"$HOME/.testagent/scratch\"; ln -s \"$PWD/legacy-target\" .codeybox; printf captured > \"$HOME/.testagent/scratch/todo.txt\"",
            ],
            WorkingDirectory = "/work",
        });
        Assert.True(arrange.Success, arrange.Stderr);

        await runner.RequestPreemptAsync(sandbox, "/work");

        var verify = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                "test -L .codeybox; test -z \"$(find -P legacy-target -mindepth 1 -print -quit)\"; test -f \"$1\"",
                "verify-private-capture",
                SandboxConventions.AgentTurnScratchpadArchivePath,
            ],
            WorkingDirectory = "/work",
        });
        Assert.True(verify.Success, verify.Stderr);
    }

    [Fact]
    public async Task RequestPreempt_CapturesScratchpadAfterTermSignal()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(SandboxSpecWithAgentTurnScratchpad());
        var runner = new TermFlushRunner();

        var runTask = sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                "trap 'mkdir -p \"$HOME/.testagent/scratch\"; printf flushed > \"$HOME/.testagent/scratch/flushed.txt\"; exit 0' TERM; printf ready > preempt-ready; while true; do sleep 1; done",
                "codeybox-test-preempt-flush-marker",
            ],
            WorkingDirectory = "/work",
        });

        var ready = await WaitForAsync(
            () => sandbox.ExecAsync(new SandboxExec { Argv = ["test", "-f", "preempt-ready"], WorkingDirectory = "/work" }),
            TimeSpan.FromSeconds(10));
        Assert.True(ready.Success, ready.Stderr);

        await runner.RequestPreemptAsync(sandbox, "/work");
        var stopped = await runTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(stopped.Success, stopped.Stderr);

        var captured = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "tmp=$(mktemp -d); tar -xzf \"$1\" -C \"$tmp\"; cat \"$tmp/home/.testagent/scratch/flushed.txt\"", "archive-read", SandboxConventions.AgentTurnScratchpadArchivePath],
            WorkingDirectory = "/work",
        });
        Assert.True(captured.Success, captured.Stderr);
        Assert.Equal("flushed", captured.Stdout.Trim());
    }

    [Fact]
    public async Task RequestPreempt_TargetsOnlyMatchingActiveRunnerExec()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox1 = await provider.CreateAsync(SandboxSpecWithAgentTurnScratchpad());
        await using var sandbox2 = await provider.CreateAsync(SandboxSpecWithAgentTurnScratchpad());
        var runner = new TermFlushRunner();
        using var keepSecondRunning = new CancellationTokenSource();

        var prompt =
            ": codeybox-test-preempt-flush-marker; " +
            "trap 'mkdir -p \"$HOME/.testagent/scratch\"; printf flushed > \"$HOME/.testagent/scratch/flushed.txt\"; exit 0' TERM; " +
            "printf ready > preempt-ready; while true; do sleep 1; done";

        var run1 = runner.RunAsync(sandbox1, "/work", prompt, credential: null);
        var run2 = runner.RunAsync(sandbox2, "/work", prompt, credential: null, ct: keepSecondRunning.Token);

        // Deadlines here intentionally exceed the sibling single-sandbox test's
        // 10s: this case runs two sandboxes and two trapped shells in parallel,
        // and under whole-suite CPU saturation the per-sandbox readiness and
        // post-preempt teardown can each consume several real-time seconds.
        // See watchdog-progress test (commit 36e7e54) for the same bump rationale.
        var ready1 = await WaitForAsync(
            () => sandbox1.ExecAsync(new SandboxExec { Argv = ["test", "-f", "preempt-ready"], WorkingDirectory = "/work" }),
            TimeSpan.FromSeconds(30));
        var ready2 = await WaitForAsync(
            () => sandbox2.ExecAsync(new SandboxExec { Argv = ["test", "-f", "preempt-ready"], WorkingDirectory = "/work" }),
            TimeSpan.FromSeconds(30));
        Assert.True(ready1.Success, ready1.Stderr);
        Assert.True(ready2.Success, ready2.Stderr);

        await runner.RequestPreemptAsync(sandbox1, "/work");

        var stopped = await run1.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(stopped.Success, stopped.Stderr);
        Assert.False(run2.IsCompleted);

        await keepSecondRunning.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run2.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task RunResumed_RestoresCapturedScratchpad()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(SandboxSpecWithAgentTurnScratchpad());
        var runner = new TestCliRunner();

        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "set -e; mkdir -p \"$HOME/.testagent/scratch\"; printf '%s\n' 'resume this todo' > \"$HOME/.testagent/scratch/todo.txt\""],
        });
        Assert.True(write.Success, write.Stderr);

        await runner.RequestPreemptAsync(sandbox, "/work");

        var removeOriginal = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "rm -rf \"$HOME/.testagent\""],
        });
        Assert.True(removeOriginal.Success, removeOriginal.Stderr);

        var result = await runner.RunResumedAsync(
            sandbox,
            "/work",
            "test -f \"$HOME/.testagent/scratch/todo.txt\" && grep -q 'resume this todo' \"$HOME/.testagent/scratch/todo.txt\"",
            credential: null,
            new AgentResumeContext("refs/heads/codeybox/preempt/test"));

        Assert.True(result.Success, result.Stderr);
    }

    [Fact]
    public async Task RunResumed_ExecutionUnavailableDuringScratchpadRestore_ThrowsTypedPreparationFailure()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var inner = await provider.CreateAsync(SandboxSpecWithAgentTurnScratchpad());
        await using var sandbox = new RestoreExecutionUnavailableSandbox(inner);
        var runner = new TestCliRunner();

        var failure = await Assert.ThrowsAsync<AgentResumePreparationUnavailableException>(() =>
            runner.RunResumedAsync(
                sandbox,
                "/work",
                "true",
                credential: null,
                new AgentResumeContext("refs/heads/codeybox/preempt/test")));

        Assert.Equal(RestoreExecutionUnavailableSandbox.UnavailableExitCode, failure.ExitCode);
        Assert.Equal(1, sandbox.RestoreAttempts);
    }

    [Fact]
    public async Task RunResumed_RejectsArchiveOutsideConfiguredScratchpadRoots()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(SandboxSpecWithAgentTurnScratchpad());
        var runner = new TestCliRunner();

        var archive = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "bash", "-c",
                "set -euo pipefail; root=$1; tmp=$(mktemp -d \"$root/test-archive.XXXXXX\"); trap 'rm -rf -- \"$tmp\"' EXIT; mkdir -p \"$tmp/home/.ssh\"; printf '%s\n' 'dir\thome\t.ssh' 'file\thome\t.ssh/config' > \"$tmp/manifest.tsv\"; printf '%s\n' Host evil > \"$tmp/home/.ssh/config\"; tar -czf \"$root/scratchpad.tgz\" -C \"$tmp\" .",
                "archive-create",
                SandboxConventions.AgentTurnScratchpadDir,
            ],
            WorkingDirectory = "/work",
        });
        Assert.True(archive.Success, archive.Stderr);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunResumedAsync(
            sandbox,
            "/work",
            "true",
            credential: null,
            new AgentResumeContext("refs/heads/codeybox/preempt/test")));
    }

    [Fact]
    public async Task RunResumed_RejectsSymlinkedDestinationPath()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(SandboxSpecWithAgentTurnScratchpad());
        var runner = new TestCliRunner();

        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "set -e; mkdir -p \"$HOME/.testagent/scratch\"; printf '%s\n' 'do not escape' > \"$HOME/.testagent/scratch/todo.txt\""],
        });
        Assert.True(write.Success, write.Stderr);

        await runner.RequestPreemptAsync(sandbox, "/work");

        var symlink = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "set -e; rm -rf \"$HOME/.testagent\" escape-target; mkdir -p escape-target; ln -s \"$PWD/escape-target\" \"$HOME/.testagent\""],
            WorkingDirectory = "/work",
        });
        Assert.True(symlink.Success, symlink.Stderr);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunResumedAsync(
            sandbox,
            "/work",
            "true",
            credential: null,
            new AgentResumeContext("refs/heads/codeybox/preempt/test")));

        var escaped = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["test", "-f", "escape-target/scratch/todo.txt"],
            WorkingDirectory = "/work",
        });
        Assert.False(escaped.Success);
    }

    [Fact]
    public async Task RunResumed_RejectsArchiveOverUncompressedFileLimit()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(SandboxSpecWithAgentTurnScratchpad());
        var runner = new TestCliRunner();

        var archive = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "bash", "-c",
                $"set -euo pipefail; root=$1; tmp=$(mktemp -d \"$root/test-archive.XXXXXX\"); trap 'rm -rf -- \"$tmp\"' EXIT; mkdir -p \"$tmp/home/.testagent/scratch\"; printf '%s\\n' 'dir\\thome\\t.testagent/scratch' 'file\\thome\\t.testagent/scratch/big.bin' > \"$tmp/manifest.tsv\"; head -c {AgentTurnScratchpadArchive.MaximumFileBytes + 1} /dev/zero > \"$tmp/home/.testagent/scratch/big.bin\"; tar -czf \"$root/scratchpad.tgz\" -C \"$tmp\" .",
                "archive-create",
                SandboxConventions.AgentTurnScratchpadDir,
            ],
            WorkingDirectory = "/work",
        });
        Assert.True(archive.Success, archive.Stderr);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunResumedAsync(
            sandbox,
            "/work",
            "true",
            credential: null,
            new AgentResumeContext("refs/heads/codeybox/preempt/test")));
    }

    [Fact]
    public async Task RunResumed_RejectsCompressedTarBombOverExpandedLimit()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(SandboxSpecWithAgentTurnScratchpad());
        var runner = new TestCliRunner();

        var archive = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "bash", "-c",
                $"set -euo pipefail; root=$1; tmp=$(mktemp -d \"$root/tar-bomb.XXXXXX\"); trap 'rm -rf -- \"$tmp\"' EXIT; mkdir -p \"$tmp/home/.testagent/scratch\"; printf '%s\\n' 'dir\\thome\\t.testagent/scratch' 'file\\thome\\t.testagent/scratch/bomb.bin' > \"$tmp/manifest.tsv\"; head -c {AgentTurnScratchpadArchive.MaximumExpandedBytes + 1} /dev/zero > \"$tmp/home/.testagent/scratch/bomb.bin\"; tar -czf \"$root/scratchpad.tgz\" -C \"$tmp\" .; test \"$(wc -c < \"$root/scratchpad.tgz\")\" -lt {AgentTurnScratchpadArchive.MaximumBytes}",
                "archive-create",
                SandboxConventions.AgentTurnScratchpadDir,
            ],
            WorkingDirectory = "/work",
        });
        Assert.True(archive.Success, archive.Stderr);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunResumedAsync(
            sandbox,
            "/work",
            "true",
            credential: null,
            new AgentResumeContext("refs/heads/codeybox/preempt/test")));
    }

    private class TestCliRunner : CliAgentRunnerBase
    {
        public override AgentKind Kind => new("testagent");
        protected override IReadOnlyList<string> ScratchpadHomeDirectories => [".testagent/scratch"];
        protected override string PreemptProcessPattern => "definitely-not-running-codeybox-testagent";

        protected override AgentInvocation BuildInvocation(
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            bool captureStructuredStream = false)
            => new(["sh", "-c", prompt]);
    }

    private static SandboxSpec SandboxSpecWithAgentTurnScratchpad() => new()
    {
        ImageReference = "ignored",
        Mounts =
        [
            new SandboxMount
            {
                SandboxPath = SandboxConventions.AgentTurnScratchpadDir,
                Tmpfs = true,
                SizeBytes = SandboxConventions.AgentTurnScratchpadTmpfsBytes,
            },
        ],
    };

    private sealed class TermFlushRunner : TestCliRunner
    {
        protected override string PreemptProcessPattern => "codeybox-test-preempt-flush-marker";
    }

    private sealed class RestoreExecutionUnavailableSandbox : ISandbox
    {
        public const int UnavailableExitCode = 255;
        private readonly ISandbox _inner;
        private int _restoreAttempts;

        public RestoreExecutionUnavailableSandbox(ISandbox inner)
        {
            _inner = inner;
        }

        public string Id => _inner.Id;
        public SandboxAgentOutputTransportKind AgentOutputTransportKind => _inner.AgentOutputTransportKind;
        public SandboxBatchLaunchMode BatchLaunchMode => _inner.BatchLaunchMode;
        public int RestoreAttempts => Volatile.Read(ref _restoreAttempts);

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count >= 4
                && string.Equals(exec.Argv[0], "bash", StringComparison.Ordinal)
                && string.Equals(exec.Argv[3], "codeybox-resume", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _restoreAttempts);
                return Task.FromResult(new SandboxExecResult(
                    UnavailableExitCode,
                    Stdout: string.Empty,
                    Stderr: "injected unavailable restore transport",
                    ExecutionUnavailable: true));
            }

            return _inner.ExecAsync(exec, ct);
        }

        public Task SyncStateToHostAsync(CancellationToken ct = default)
            => _inner.SyncStateToHostAsync(ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<SandboxExecResult> WaitForAsync(
        Func<Task<SandboxExecResult>> poll,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        SandboxExecResult result;
        do
        {
            result = await poll();
            if (result.Success)
                return result;
            await Task.Delay(25);
        } while (DateTimeOffset.UtcNow < deadline);

        return result;
    }
}
