using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Sandbox.Bubblewrap;
using CodeyBox.Tests;

namespace CodeyBox.Tests.Uat.SandboxProviders;

/// <summary>
/// UAT coverage for <c>Bubblewrap sandbox provider - Runs transient Linux namespace sandboxes</c>.
/// Plan anchor: docs/uat/00-plan.md#bubblewrap-sandbox-provider---runs-transient-linux-namespace-sandboxes
/// </summary>
public sealed class BubblewrapSandboxProviderTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-uat-bwrap-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public async Task NoNetworkPolicy_AddsUnshareNetAndBuildsExpectedMountArgv()
    {
        var hostSource = Path.Combine(_workspace, "host");
        Directory.CreateDirectory(hostSource);
        var provider = EchoProvider();
        await using var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Mounts =
            [
                new SandboxMount { SandboxPath = "/work", Tmpfs = true },
                new SandboxMount { SandboxPath = "/repo", HostPath = hostSource, ReadOnly = true },
            ],
            WorkingDirectory = "/work",
            Network = SandboxNetworkPolicy.Denied,
        });

        var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["agent", "run"] });

        Assert.True(result.Success, result.Stderr);
        var argv = SplitEcho(result.Stdout);
        Assert.Contains("--die-with-parent", argv);
        Assert.Contains("--unshare-user", argv);
        Assert.Contains("--unshare-pid", argv);
        Assert.Contains("--unshare-ipc", argv);
        Assert.Contains("--unshare-uts", argv);
        Assert.Contains("--unshare-net", argv);
        Assert.Contains("--tmpfs", argv);
        Assert.Contains("/tmp", argv);
        Assert.Contains("--proc", argv);
        Assert.Contains("/proc", argv);
        AssertOption(argv, "--ro-bind", hostSource, "/repo");
        AssertOption(argv, "--chdir", "/work");
        Assert.Equal(["agent", "run"], argv[^2..]);
    }

    [Fact]
    public async Task RequestedNetwork_SharesHostNetworkAndLogsWarning()
    {
        var logger = new RecordingLogger<BubblewrapSandboxProvider>();
        var provider = EchoProvider(logger);
        await using var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy { AllowedHosts = ["api.openai.com"] },
        });

        var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["true"] });

        Assert.True(result.Success, result.Stderr);
        var argv = SplitEcho(result.Stdout);
        Assert.DoesNotContain("--unshare-net", argv);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("network policy is NOT enforced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingDefaultReadOnlyHostBind_IsSkipped()
    {
        var missing = Path.Combine(_workspace, "does-not-exist");
        var provider = new BubblewrapSandboxProvider(
            new BubblewrapSandboxOptions
            {
                BwrapBinary = "/bin/echo",
                ReadOnlyHostBinds = [missing],
            },
            new RecordingLogger<BubblewrapSandboxProvider>());
        await using var sandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });

        var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["true"] });

        Assert.True(result.Success, result.Stderr);
        Assert.DoesNotContain(missing, SplitEcho(result.Stdout));
    }

    [Fact]
    public async Task StdinAndStdoutCallbacks_AreHandledByProviderProcess()
    {
        if (OperatingSystem.IsWindows()) return;
        var ackPath = Path.Combine(_workspace, "stdout-ack.fifo");
        var fakeBwrap = Path.Combine(_workspace, "fake-stdin-bwrap.sh");
        WriteExecutableScript(fakeBwrap, """
            #!/bin/sh
            mkfifo "$ACK_FIFO"
            bytes=$(wc -c | tr -d ' ')
            printf '%s\n' "$bytes"
            IFS= read -r _ < "$ACK_FIFO"
            rm -f "$ACK_FIFO"
            """);
        var provider = new BubblewrapSandboxProvider(
            new BubblewrapSandboxOptions
            {
                BwrapBinary = fakeBwrap,
                ReadOnlyHostBinds = [],
            },
            new RecordingLogger<BubblewrapSandboxProvider>());
        await using var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Environment = new Dictionary<string, string> { ["ACK_FIFO"] = ackPath },
        });
        var chunks = new List<string>();

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["ignored"],
            Stdin = "abcdef",
            StdoutChunkCallback = chunk =>
            {
                chunks.Add(chunk);
                if (chunk.Contains("6", StringComparison.Ordinal))
                    File.WriteAllText(ackPath, "ack\n");
            },
        });

        Assert.True(result.Success, result.Stderr);
        Assert.Contains("6", result.Stdout);
        Assert.NotEmpty(chunks);
    }

    [Fact]
    public async Task Cancellation_KillsProviderProcessTreeAndThrows()
    {
        if (OperatingSystem.IsWindows()) return;
        var readyPath = Path.Combine(_workspace, "fake-bwrap.ready");
        var fakeBwrap = Path.Combine(_workspace, "fake-bwrap.sh");
        WriteExecutableScript(fakeBwrap, """
            #!/bin/sh
            printf ready > "$READY_FILE"
            exec tail -f /dev/null
            """);

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = TestFileSystemWatcherLeakTracker.CreateWatcher(_workspace, Path.GetFileName(readyPath));
        watcher.EnableRaisingEvents = true;
        watcher.Created += (_, _) => ready.TrySetResult();
        var provider = new BubblewrapSandboxProvider(
            new BubblewrapSandboxOptions
            {
                BwrapBinary = fakeBwrap,
                ReadOnlyHostBinds = [],
            },
            new RecordingLogger<BubblewrapSandboxProvider>());
        await using var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            Environment = new Dictionary<string, string> { ["READY_FILE"] = readyPath },
        });
        using var cts = new CancellationTokenSource();

        var execTask = sandbox.ExecAsync(new SandboxExec { Argv = ["ignored"] }, cts.Token);
        if (File.Exists(readyPath))
            ready.TrySetResult();
        await ready.Task;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execTask);
    }

    private static BubblewrapSandboxProvider EchoProvider(
        RecordingLogger<BubblewrapSandboxProvider>? logger = null) =>
        new(
            new BubblewrapSandboxOptions
            {
                BwrapBinary = "/bin/echo",
                ReadOnlyHostBinds = [],
            },
            logger ?? new RecordingLogger<BubblewrapSandboxProvider>());

    private static string[] SplitEcho(string stdout) =>
        stdout.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries);

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void WriteExecutableScript(string path, string contents)
    {
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tempPath, contents);
        File.SetUnixFileMode(tempPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Move(tempPath, path);
    }

    private static void AssertOption(string[] argv, string option, params string[] values)
    {
        var indexes = argv
            .Select((value, index) => (value, index))
            .Where(item => item.value == option)
            .Select(item => item.index)
            .ToArray();
        Assert.Contains(indexes, index =>
            index + values.Length < argv.Length
            && values.SequenceEqual(argv.Skip(index + 1).Take(values.Length)));
    }
}
