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
        await using var sandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
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
            Argv = ["sh", "-c", "tar -tzf .codeybox/preempt-scratchpad.tgz | sort"],
            WorkingDirectory = "/work",
        });
        Assert.True(archive.Success, archive.Stderr);
        Assert.Contains("manifest.txt", archive.Stdout);
        Assert.Contains("manifest.tsv", archive.Stdout);
        Assert.Contains("home/.testagent/scratch/todo.txt", archive.Stdout);
        Assert.DoesNotContain("home/.testagent/private/auth.json", archive.Stdout);

        var captured = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "tmp=$(mktemp -d); tar -xzf .codeybox/preempt-scratchpad.tgz -C \"$tmp\"; cat \"$tmp/home/.testagent/scratch/todo.txt\""],
            WorkingDirectory = "/work",
        });
        Assert.True(captured.Success, captured.Stderr);
        Assert.Contains("resume this todo", captured.Stdout);

        var manifest = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["cat", ".codeybox/preempt-scratchpad.md"],
            WorkingDirectory = "/work",
        });
        Assert.True(manifest.Success, manifest.Stderr);
        Assert.Contains("capturing home/.testagent/scratch", manifest.Stdout);
        Assert.DoesNotContain("secret", manifest.Stdout);
    }

    [Fact]
    public async Task RequestPreempt_CapturesScratchpadAfterTermSignal()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
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
            Argv = ["sh", "-c", "tmp=$(mktemp -d); tar -xzf .codeybox/preempt-scratchpad.tgz -C \"$tmp\"; cat \"$tmp/home/.testagent/scratch/flushed.txt\""],
            WorkingDirectory = "/work",
        });
        Assert.True(captured.Success, captured.Stderr);
        Assert.Equal("flushed", captured.Stdout.Trim());
    }

    [Fact]
    public async Task RequestPreempt_TargetsOnlyMatchingActiveRunnerExec()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox1 = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
        await using var sandbox2 = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
        var runner = new TermFlushRunner();
        using var keepSecondRunning = new CancellationTokenSource();

        var prompt =
            ": codeybox-test-preempt-flush-marker; " +
            "trap 'mkdir -p \"$HOME/.testagent/scratch\"; printf flushed > \"$HOME/.testagent/scratch/flushed.txt\"; exit 0' TERM; " +
            "printf ready > preempt-ready; while true; do sleep 1; done";

        var run1 = runner.RunAsync(sandbox1, "/work", prompt, credential: null);
        var run2 = runner.RunAsync(sandbox2, "/work", prompt, credential: null, ct: keepSecondRunning.Token);

        var ready1 = await WaitForAsync(
            () => sandbox1.ExecAsync(new SandboxExec { Argv = ["test", "-f", "preempt-ready"], WorkingDirectory = "/work" }),
            TimeSpan.FromSeconds(10));
        var ready2 = await WaitForAsync(
            () => sandbox2.ExecAsync(new SandboxExec { Argv = ["test", "-f", "preempt-ready"], WorkingDirectory = "/work" }),
            TimeSpan.FromSeconds(10));
        Assert.True(ready1.Success, ready1.Stderr);
        Assert.True(ready2.Success, ready2.Stderr);

        await runner.RequestPreemptAsync(sandbox1, "/work");

        var stopped = await run1.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(stopped.Success, stopped.Stderr);
        Assert.False(run2.IsCompleted);

        await keepSecondRunning.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run2);
    }

    [Fact]
    public async Task RunResumed_RestoresCapturedScratchpad()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
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
    public async Task RunResumed_RejectsArchiveOutsideConfiguredScratchpadRoots()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
        var runner = new TestCliRunner();

        var archive = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "bash", "-c",
                "set -euo pipefail; mkdir -p .codeybox/tmp/home/.ssh; printf '%s\n' 'dir\thome\t.ssh' 'file\thome\t.ssh/config' > .codeybox/tmp/manifest.tsv; printf '%s\n' Host evil > .codeybox/tmp/home/.ssh/config; tar -czf .codeybox/preempt-scratchpad.tgz -C .codeybox/tmp ."
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
        await using var sandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
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
        await using var sandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
        var runner = new TestCliRunner();

        var archive = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "bash", "-c",
                "set -euo pipefail; mkdir -p .codeybox/tmp/home/.testagent/scratch; printf '%s\n' 'dir\thome\t.testagent/scratch' 'file\thome\t.testagent/scratch/big.bin' > .codeybox/tmp/manifest.tsv; head -c 2097153 /dev/zero > .codeybox/tmp/home/.testagent/scratch/big.bin; tar -czf .codeybox/preempt-scratchpad.tgz -C .codeybox/tmp ."
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
            string? reasoningMode = null)
            => new(["sh", "-c", prompt]);
    }

    private sealed class TermFlushRunner : TestCliRunner
    {
        protected override string PreemptProcessPattern => "codeybox-test-preempt-flush-marker";
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
