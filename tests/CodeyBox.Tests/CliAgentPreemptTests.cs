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

    private sealed class TestCliRunner : CliAgentRunnerBase
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
}
