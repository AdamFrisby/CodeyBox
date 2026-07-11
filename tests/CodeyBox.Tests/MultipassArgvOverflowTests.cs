using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Multipass;

namespace CodeyBox.Tests;

public sealed class MultipassArgvOverflowTests : IDisposable
{
    private readonly string _sandboxRoot = Path.Combine(Path.GetTempPath(), $"codeybox-mp-argv-{Guid.NewGuid():N}");

    public MultipassArgvOverflowTests()
    {
        Directory.CreateDirectory(_sandboxRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task ExecAsync_EnvironmentRemovalUsesGuestFileAfterEnvironmentMerge()
    {
        var runner = new RecordingProcessRunner();
        var sandbox = NewSandbox(runner);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["echo", "ok"],
            ExtraEnvironment = new Dictionary<string, string>
            {
                ["A"] = "one",
                ["B"] = "two",
            },
            EnvironmentVariablesToUnset = ["A"],
        });

        Assert.True(result.Success);
        var exec = runner.Calls.Single(call =>
            call.Argv is ["multipass", "exec", "codeybox-test", "--", "/usr/local/bin/codeybox-exec", ..]);
        Assert.Contains("--env-file", exec.Argv);
        Assert.DoesNotContain(exec.Argv, static argument => argument.StartsWith("A=", StringComparison.Ordinal));
        var transfer = Assert.Single(
            runner.Transfers,
            item => item.Destination.Contains(".codeybox-exec-env/", StringComparison.Ordinal));
        Assert.DoesNotContain("A='one'", transfer.Content, StringComparison.Ordinal);
        Assert.Contains("B='two'", transfer.Content, StringComparison.Ordinal);
        Assert.Contains("unset -- 'A'", transfer.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecAsync_SecretEnvironment_TransfersFileAndNeverUsesHostArgv()
    {
        const string secret = "candidate-account-secret";
        var runner = new RecordingProcessRunner();
        var sandbox = NewSandbox(runner);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["printenv", "OPENAI_API_KEY"],
            ExtraEnvironment = new Dictionary<string, string>
            {
                ["OPENAI_API_KEY"] = secret,
            },
            EnvironmentContainsSecrets = true,
        });

        Assert.True(result.Success);
        Assert.DoesNotContain(
            runner.Calls.SelectMany(static call => call.Argv),
            argument => argument.Contains(secret, StringComparison.Ordinal));
        var finalExec = runner.Calls.Single(call =>
            call.Argv is ["multipass", "exec", "codeybox-test", "--", "/usr/local/bin/codeybox-exec", ..]);
        Assert.Contains("--env-file", finalExec.Argv);
        var transfer = Assert.Single(
            runner.Transfers,
            item => item.Destination.Contains(".codeybox-exec-env/", StringComparison.Ordinal));
        Assert.Contains(secret, transfer.Content, StringComparison.Ordinal);
        Assert.False(File.Exists(transfer.Source));
    }

    [Fact]
    public async Task ExecAsync_EnvironmentRemovalTransferFailureFailsClosedBeforeLaunch()
    {
        var runner = new RecordingProcessRunner { FailEnvironmentTransfer = true };
        var sandbox = NewSandbox(runner);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["printenv", "REMOVE_ME"],
            EnvironmentVariablesToUnset = ["REMOVE_ME"],
        }));

        Assert.Contains("environment delivery failed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.Calls, call =>
            call.Argv is ["multipass", "exec", "codeybox-test", "--", "/usr/local/bin/codeybox-exec", ..]);
    }

    [Fact]
    public async Task ExecAsync_LargeExtraEnvironment_TransfersEnvFileAndKeepsExecutedArgvSmall()
    {
        var runner = new RecordingProcessRunner();
        var sandbox = NewSandbox(runner);
        var largeEnv = Enumerable.Range(0, 120)
            .ToDictionary(i => $"BIG_{i:000}", i => new string((char)('a' + i % 26), 700));

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["printenv", "BIG_000"],
            ExtraEnvironment = largeEnv,
        });

        Assert.True(result.Success);
        Assert.All(runner.Calls, c =>
            Assert.True(
                MultipassSandbox.EstimateArgvBytes(c.Argv) <= MultipassSandbox.ArgvBytesWarningThreshold,
                $"argv was {MultipassSandbox.EstimateArgvBytes(c.Argv)} bytes: {string.Join(" ", c.Argv.Take(6))}"));

        var finalExec = runner.Calls.Single(c => c.Argv is ["multipass", "exec", "codeybox-test", "--", "/usr/local/bin/codeybox-exec", ..]);
        Assert.Contains("--env-file", finalExec.Argv);
        Assert.DoesNotContain(finalExec.Argv, a => a.StartsWith("BIG_000=", StringComparison.Ordinal));

        var envTransfer = Assert.Single(
            runner.Transfers,
            t => t.Destination.Contains(".codeybox-exec-env/", StringComparison.Ordinal));
        Assert.Contains("BIG_000='", envTransfer.Content);
        Assert.Contains(new string('a', 700), envTransfer.Content);
    }

    [Fact]
    public async Task ExecAsync_OversizedCommandArg_TransfersScriptAndKeepsExecutedArgvSmall()
    {
        var runner = new RecordingProcessRunner();
        var sandbox = NewSandbox(runner);
        var largeArg = new string('x', MultipassSandbox.ArgvBytesWarningThreshold + 4096);

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["printf", "%s", largeArg],
        });

        Assert.True(result.Success);
        Assert.All(runner.Calls, c =>
            Assert.True(
                MultipassSandbox.EstimateArgvBytes(c.Argv) <= MultipassSandbox.ArgvBytesWarningThreshold,
                $"argv was {MultipassSandbox.EstimateArgvBytes(c.Argv)} bytes: {string.Join(" ", c.Argv.Take(6))}"));

        var finalExec = runner.Calls.Single(c => c.Argv is ["multipass", "exec", "codeybox-test", "--", "/bin/sh", ..]);
        Assert.DoesNotContain(largeArg, finalExec.Argv);
        Assert.StartsWith("/home/ubuntu/.codeybox-exec/", finalExec.Argv[^1], StringComparison.Ordinal);

        var scriptTransfer = Assert.Single(
            runner.Transfers,
            t => t.Destination.Contains(".codeybox-exec/", StringComparison.Ordinal));
        Assert.Contains("exec '/usr/local/bin/codeybox-exec' '/work' 'printf' '%s'", scriptTransfer.Content);
        Assert.Contains(largeArg, scriptTransfer.Content);
    }

    [Fact]
    public async Task ExecAsync_WithTimingWorkItemId_TagsHostExecAndTransferCalls()
    {
        var workItemId = WorkItemId.New();
        var runner = new RecordingProcessRunner();
        var sandbox = NewSandbox(runner, workItemId);
        var largeEnv = Enumerable.Range(0, 120)
            .ToDictionary(i => $"BIG_{i:000}", i => new string((char)('a' + i % 26), 700));

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["printenv", "BIG_000"],
            ExtraEnvironment = largeEnv,
        });

        Assert.True(result.Success);
        Assert.Contains(runner.Calls, c => c.Argv is ["multipass", "transfer", ..]);
        Assert.Contains(runner.Calls, c => c.Argv is ["multipass", "exec", "codeybox-test", "--", "/usr/local/bin/codeybox-exec", ..]);
        Assert.All(runner.Calls, c =>
        {
            Assert.NotNull(c.Environment);
            Assert.Equal(
                workItemId.ToString(),
                c.Environment![SandboxConventions.WorkItemIdEnvironmentVariable]);
        });
    }

    private MultipassSandbox NewSandbox(RecordingProcessRunner runner, WorkItemId? workItemId = null) => new(
        "codeybox-test",
        _sandboxRoot,
        new SandboxSpec { ImageReference = "ignored", WorkingDirectory = "/work" },
        new MultipassSandboxOptions(),
        NullLogger.Instance,
        timingItemId: workItemId.GetValueOrDefault(),
        runner: runner);

    private sealed record RecordedCall(
        IReadOnlyList<string> Argv,
        string? Stdin,
        IReadOnlyDictionary<string, string>? Environment);
    private sealed record RecordedTransfer(string Source, string Destination, string Content);

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public List<RecordedCall> Calls { get; } = [];
        public List<RecordedTransfer> Transfers { get; } = [];
        public bool FailEnvironmentTransfer { get; init; }

        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null,
            bool killOnOutputLimit = true)
        {
            Calls.Add(new RecordedCall(
                argv.ToArray(),
                stdin,
                environment is null ? null : new Dictionary<string, string>(environment, StringComparer.Ordinal)));
            if (argv is ["multipass", "transfer", var source, var destination])
            {
                Transfers.Add(new RecordedTransfer(source, destination, File.ReadAllText(source)));
                if (FailEnvironmentTransfer
                    && destination.Contains(".codeybox-exec-env/", StringComparison.Ordinal))
                {
                    return Task.FromResult(new ProcessRunResult(1, "", "synthetic transfer failure"));
                }
            }
            stdoutChunkCallback?.Invoke("ok\n");
            return Task.FromResult(new ProcessRunResult(0, "ok\n", ""));
        }
    }
}
