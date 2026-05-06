using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class CliAgentPreemptTests
{
    [Fact]
    public async Task RequestPreempt_CapturesConfiguredCliScratchpadArchive()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
        var runner = new TestCliRunner();

        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "set -e; mkdir -p \"$HOME/.testagent\"; printf '%s\n' session > \"$HOME/.testagent/session.txt\""],
        });
        Assert.True(write.Success, write.Stderr);

        await runner.RequestPreemptAsync(sandbox, "/work");

        var archive = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "tar -tzf .codeybox/preempt-scratchpad.tgz | sort"],
            WorkingDirectory = "/work",
        });
        Assert.True(archive.Success, archive.Stderr);
        Assert.Contains("home/.testagent/session.txt", archive.Stdout);

        var manifest = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["cat", ".codeybox/preempt-scratchpad.md"],
            WorkingDirectory = "/work",
        });
        Assert.True(manifest.Success, manifest.Stderr);
        Assert.Contains("captured HOME/.testagent", manifest.Stdout);
    }

    private sealed class TestCliRunner : CliAgentRunnerBase
    {
        public override AgentKind Kind => new("testagent");
        protected override IReadOnlyList<string> ScratchpadHomeDirectories => [".testagent"];
        protected override string PreemptProcessPattern => "definitely-not-running-codeybox-testagent";

        protected override AgentInvocation BuildInvocation(
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null)
            => new(["true"]);
    }
}
