using CodeyBox.Core;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Uat.AgentRunnersAndCredentials;

/// <summary>
/// UAT coverage for <c>CLI runner base and preemption - Shared one-shot CLI execution, stream capture, and scratchpad resume</c>.
/// Plan anchor: docs/uat/00-plan.md#cli-runner-base-and-preemption---shared-one-shot-cli-execution-stream-capture-and-scratchpad-resume
/// </summary>
public sealed class CliRunnerBaseUatTests
{
    [Fact]
    public async Task RunAsync_WrapsInvocationWithRunIdForwardsStdoutAndKeepsCredentialsOutOfPerExecEnvironment()
    {
        var chunks = new List<string>();
        var sandbox = new RecordingSandbox(stdoutChunk: "stream chunk");
        var runner = new UatCliRunner();
        var credential = new AgentCredential(
            AgentKind.Claude,
            new Dictionary<string, string> { ["SECRET_ENV"] = "do-not-copy" },
            new Dictionary<string, string>());

        var result = await runner.RunAsync(
            sandbox,
            "/work",
            "printf done",
            credential,
            stdoutChunkCallback: chunks.Add,
            captureStructuredStream: true);

        Assert.True(result.Success);
        var exec = Assert.Single(sandbox.Execs);
        Assert.Equal(["stream chunk"], chunks);
        Assert.Equal(["sh", "-c", "printf done"], exec.Argv);
        Assert.Equal("present", exec.ExtraEnvironment!["UAT_RUNNER_ENV"]);
        Assert.True(exec.ExtraEnvironment.ContainsKey("CODEYBOX_AGENT_RUN_ID"));
        Assert.DoesNotContain("SECRET_ENV", exec.ExtraEnvironment.Keys);
    }

    [Fact]
    public async Task RequestPreempt_CapturesAllowlistedScratchpadAndSkipsUnsafeConfiguredPaths()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(SandboxSpecWithAgentTurnScratchpad());
        var runner = new UatCliRunner([".uat-agent/scratch", "../outside", ".git"]);

        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-c",
                "set -eu; mkdir -p \"$HOME/.uat-agent/scratch\" \"$HOME/.ssh\"; printf todo > \"$HOME/.uat-agent/scratch/todo.txt\"; printf secret > \"$HOME/.ssh/config\""
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
        Assert.Contains("home/.uat-agent/scratch/todo.txt", archive.Stdout);
        Assert.DoesNotContain("home/.ssh/config", archive.Stdout);

        var manifest = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "tar -xOzf \"$1\" ./manifest.txt 2>/dev/null || tar -xOzf \"$1\" manifest.txt", "manifest-read", SandboxConventions.AgentTurnScratchpadArchivePath],
            WorkingDirectory = "/work",
        });
        Assert.True(manifest.Success, manifest.Stderr);
        Assert.Contains("capturing home/.uat-agent/scratch", manifest.Stdout);
        Assert.Contains("skipped home/../outside: invalid scratchpad path", manifest.Stdout);
    }

    [Fact]
    public async Task RunResumed_RestoresScratchpadBeforeResumeInvocation()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(SandboxSpecWithAgentTurnScratchpad());
        var runner = new UatCliRunner();

        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "set -eu; mkdir -p \"$HOME/.uat-agent/scratch\"; printf resume-me > \"$HOME/.uat-agent/scratch/todo.txt\""],
        });
        Assert.True(write.Success, write.Stderr);

        await runner.RequestPreemptAsync(sandbox, "/work");

        var remove = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["rm", "-rf", ".uat-agent"],
        });
        Assert.True(remove.Success, remove.Stderr);

        var result = await runner.RunResumedAsync(
            sandbox,
            "/work",
            "test \"$(cat \"$HOME/.uat-agent/scratch/todo.txt\")\" = resume-me",
            credential: null,
            new AgentResumeContext("refs/heads/codeybox/preempt/uat"));

        Assert.True(result.Success, result.Stderr);
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
}
