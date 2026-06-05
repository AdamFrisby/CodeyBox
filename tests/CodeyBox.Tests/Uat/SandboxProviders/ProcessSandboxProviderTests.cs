using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;

namespace CodeyBox.Tests.Uat.SandboxProviders;

/// <summary>
/// UAT coverage for <c>Process sandbox provider - Unsafe local-development runner</c>.
/// Plan anchor: docs/uat/00-plan.md#process-sandbox-provider---unsafe-local-development-runner
/// </summary>
public sealed class ProcessSandboxProviderTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-uat-process-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public async Task ReadOnlyDirectoryMount_IsCopiedSoSandboxWritesDoNotMutateHost()
    {
        var hostReadOnly = Path.Combine(_workspace, "readonly-host");
        Directory.CreateDirectory(hostReadOnly);
        await File.WriteAllTextAsync(Path.Combine(hostReadOnly, "data.txt"), "host-original\n");
        var provider = new ProcessSandboxProvider(new RecordingLogger<ProcessSandboxProvider>());
        await using var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts =
            [
                new SandboxMount { SandboxPath = "/readonly", HostPath = hostReadOnly, ReadOnly = true },
            ],
            WorkingDirectory = "/work",
        });

        var read = await sandbox.ExecAsync(new SandboxExec { Argv = ["cat", "/readonly/data.txt"] });
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "printf sandbox-change > \"$0\"", "/readonly/data.txt"],
        });

        Assert.True(read.Success, read.Stderr);
        Assert.Equal("host-original\n", read.Stdout);
        Assert.Equal("host-original\n", await File.ReadAllTextAsync(Path.Combine(hostReadOnly, "data.txt")));
        Assert.False(write.Success && await File.ReadAllTextAsync(Path.Combine(hostReadOnly, "data.txt")) == "sandbox-change");
    }

    [Fact]
    public async Task WritableDirectoryMount_IsSymlinkedSoGitStyleWritesReachHost()
    {
        var hostWritable = Path.Combine(_workspace, "writable-host");
        Directory.CreateDirectory(hostWritable);
        var provider = new ProcessSandboxProvider(new RecordingLogger<ProcessSandboxProvider>());
        var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts =
            [
                new SandboxMount { SandboxPath = "/repo", HostPath = hostWritable, ReadOnly = false },
            ],
            WorkingDirectory = "/work",
        });

        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "printf from-sandbox > \"$0\"", "/repo/output.txt"],
        });
        await sandbox.DisposeAsync();

        Assert.True(write.Success, write.Stderr);
        Assert.Equal("from-sandbox", await File.ReadAllTextAsync(Path.Combine(hostWritable, "output.txt")));
    }

    [Fact]
    public async Task SandboxAbsolutePathOutsideKnownMount_FailsVisibly()
    {
        var provider = new ProcessSandboxProvider(new RecordingLogger<ProcessSandboxProvider>());
        await using var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            WorkingDirectory = "/work",
        });

        var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["cat", "/not-a-mounted-path/file.txt"] });

        Assert.False(result.Success);
        Assert.Contains("No such file", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_LogsUnsafeProviderWarning()
    {
        var logger = new RecordingLogger<ProcessSandboxProvider>();
        var provider = new ProcessSandboxProvider(logger);

        await using var sandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });

        Assert.NotEmpty(sandbox.Id);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("UNSAFE provider", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TimingWorkItemId_IsExposedToExecEnvironment()
    {
        var provider = new ProcessSandboxProvider(new RecordingLogger<ProcessSandboxProvider>());
        var itemId = WorkItemId.New();
        await using var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            TimingWorkItemId = itemId,
        });

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", $"printf '%s' \"${SandboxConventions.WorkItemIdEnvironmentVariable}\""],
        });

        Assert.True(result.Success, result.Stderr);
        Assert.Equal(itemId.ToString(), result.Stdout.TrimEnd('\r', '\n'));
    }

    [Fact]
    public async Task ExecAsync_MaxStdoutBytes_KillsProcessAndFlagsTruncation()
    {
        var provider = new ProcessSandboxProvider(new RecordingLogger<ProcessSandboxProvider>());
        await using var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            WorkingDirectory = "/work",
        });

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "yes build-output"],
            MaxStdoutBytes = 128,
            MaxStderrBytes = 128,
        });

        Assert.False(result.Success);
        Assert.True(result.StdoutLimitExceeded);
        Assert.False(result.StderrLimitExceeded);
        Assert.True(result.Stdout.Length <= 128);
    }

    [Fact]
    public async Task ExecAsync_MaxStderrBytes_KillsProcessAndFlagsTruncation()
    {
        var provider = new ProcessSandboxProvider(new RecordingLogger<ProcessSandboxProvider>());
        await using var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            WorkingDirectory = "/work",
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "while :; do printf build-error >&2; done"],
            MaxStdoutBytes = 128,
            MaxStderrBytes = 128,
        }, timeout.Token);

        Assert.False(result.Success);
        Assert.False(result.StdoutLimitExceeded);
        Assert.True(result.StderrLimitExceeded);
        Assert.True(result.Stderr.Length <= 128);
    }
}
