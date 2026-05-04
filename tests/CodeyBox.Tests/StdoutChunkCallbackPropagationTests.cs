using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Sandbox.Process;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that the <see cref="SandboxExec.StdoutChunkCallback"/> is invoked
/// by <see cref="ProcessSandboxProvider"/> as the process emits output. This
/// covers the bottom half of the propagation chain:
/// PipelineRunner → IAgentRunner → SandboxExec.StdoutChunkCallback → IStdoutBroadcaster.
/// </summary>
public sealed class StdoutChunkCallbackPropagationTests
{
    private static readonly SandboxSpec MinimalSpec = new() { ImageReference = "ignored" };

    [Fact]
    public async Task StdoutChunkCallback_InvokedForEachOutputLine()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(MinimalSpec);

        var chunks = new List<string>();
        var exec = new SandboxExec
        {
            Argv = ["sh", "-c", "echo hello && echo world"],
            StdoutChunkCallback = chunk => { lock (chunks) chunks.Add(chunk); },
        };

        var result = await sandbox.ExecAsync(exec);

        Assert.True(result.Success);
        Assert.Contains(chunks, c => c.Contains("hello"));
        Assert.Contains(chunks, c => c.Contains("world"));
    }

    [Fact]
    public async Task StdoutChunkCallback_WithoutCallback_NoError()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(MinimalSpec);

        var exec = new SandboxExec
        {
            Argv = ["sh", "-c", "echo no-callback"],
        };

        var result = await sandbox.ExecAsync(exec);
        Assert.True(result.Success);
        Assert.Contains("no-callback", result.Stdout);
    }

    [Fact]
    public async Task StdoutChunkCallback_MultiLineOutput_AllLinesDelivered()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(MinimalSpec);

        var chunks = new List<string>();
        var exec = new SandboxExec
        {
            Argv = ["sh", "-c", "for i in 1 2 3 4 5; do echo line$i; done"],
            StdoutChunkCallback = chunk => { lock (chunks) chunks.Add(chunk); },
        };

        await sandbox.ExecAsync(exec);

        // All 5 lines must arrive via the callback
        for (var i = 1; i <= 5; i++)
            Assert.Contains(chunks, c => c.Contains($"line{i}"));
    }

    [Fact]
    public async Task StdoutChunkCallback_ChunksMatchStdoutField()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(MinimalSpec);

        var chunks = new List<string>();
        var exec = new SandboxExec
        {
            Argv = ["sh", "-c", "printf 'alpha\\nbeta\\n'"],
            StdoutChunkCallback = chunk => { lock (chunks) chunks.Add(chunk); },
        };

        var result = await sandbox.ExecAsync(exec);

        var joined = string.Concat(chunks);
        // The accumulated chunks should contain the same content as result.Stdout
        Assert.Contains("alpha", joined);
        Assert.Contains("beta", joined);
        Assert.Contains("alpha", result.Stdout);
        Assert.Contains("beta", result.Stdout);
    }

    [Fact]
    public async Task StdoutChunkCallback_ProcessFails_CallbackStillInvoked()
    {
        var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        await using var sandbox = await provider.CreateAsync(MinimalSpec);

        var chunks = new List<string>();
        var exec = new SandboxExec
        {
            Argv = ["sh", "-c", "echo partial && exit 1"],
            StdoutChunkCallback = chunk => { lock (chunks) chunks.Add(chunk); },
        };

        var result = await sandbox.ExecAsync(exec);

        Assert.False(result.Success);
        Assert.Contains(chunks, c => c.Contains("partial"));
    }
}
