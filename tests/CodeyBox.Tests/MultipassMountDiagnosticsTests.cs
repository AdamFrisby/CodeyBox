using System.Runtime.InteropServices;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox.Multipass;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <c>MultipassSandboxProvider.DescribeMountSourceState</c>
/// and the bounded mount-step retry in <c>MountSingleBindWithRetryAsync</c>.
///
/// Background: the snap-confined multipass daemon can only read host paths
/// under <c>~/snap/multipass/common/</c>. A bind-mount source outside that
/// subtree fails as "Source path does not exist" even though the directory
/// exists on the host. <c>DescribeMountSourceState</c> exists to capture
/// host-side state (existence, type, owner, mtime) in the audit trail so
/// operators can distinguish AppArmor denial from genuinely missing
/// directories without reproducing the failure. The mount retry covers
/// transient FS-visibility races between <c>git clone --bare</c> and
/// <c>multipass mount</c>.
/// </summary>
public sealed class MultipassMountDiagnosticsTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-mount-diag-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    // ── DescribeMountSourceState branches ──────────────────────────────────

    [Fact]
    public void DescribeMountSourceState_ExistingDirectory_ReportsTypeAndMtime()
    {
        var dir = Path.Combine(_workspace, "live-dir");
        Directory.CreateDirectory(dir);

        var state = MultipassSandboxProvider.DescribeMountSourceState(dir);

        Assert.StartsWith("exists=dir", state);
        Assert.Contains("mtime=", state);
    }

    [Fact]
    public void DescribeMountSourceState_ExistingFile_ReportsTypeAndSize()
    {
        var file = Path.Combine(_workspace, "live-file.txt");
        File.WriteAllText(file, "hello multipass");

        var state = MultipassSandboxProvider.DescribeMountSourceState(file);

        Assert.StartsWith("exists=file", state);
        Assert.Contains("size=15", state);
        Assert.Contains("mtime=", state);
    }

    [Fact]
    public void DescribeMountSourceState_MissingPath_ReturnsExistsNo()
    {
        var missing = Path.Combine(_workspace, "never-existed");

        var state = MultipassSandboxProvider.DescribeMountSourceState(missing);

        Assert.Equal("exists=no", state);
    }

    [Fact]
    public void DescribeMountSourceState_PathWithEmbeddedNull_DoesNotPropagateException()
    {
        // Inputs that Directory.Exists/File.Exists return false for must not
        // accidentally surface a typed-exception via DirectoryInfo construction
        // and abort the mount loop. Whatever the runtime classifies an
        // embedded-NUL path as ("exists=no" if filtered out early; "stat-failed"
        // if it reaches DirectoryInfo), the call must return cleanly.
        var pathological = "\0not-a-real-path";

        var state = MultipassSandboxProvider.DescribeMountSourceState(pathological);

        Assert.True(state == "exists=no" || state.StartsWith("stat-failed=", StringComparison.Ordinal),
            $"unexpected state for pathological path: {state}");
    }

    [Fact]
    public void DescribeMountSourceState_OnLinux_IncludesOwnerAndMode()
    {
        // On Linux, the daemon's UID is what AppArmor confines; logging
        // user:group(uid:gid) mode=NNN lets operators see "the orchestrator
        // could stat it as user X, but multipass-daemon runs as Y" at a
        // glance from the audit trail. Skip on macOS/Windows: macOS uses a
        // different stat flag and Windows has no concept of POSIX owner.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        var dir = Path.Combine(_workspace, "live-dir-owner");
        Directory.CreateDirectory(dir);

        var state = MultipassSandboxProvider.DescribeMountSourceState(dir);

        Assert.Contains("owner=", state);
        Assert.Contains("mode=", state);
    }

    // ── Mount retry behaviour ──────────────────────────────────────────────

    [Fact]
    public async Task MountSingleBindWithRetry_SourceExistsAndMountTransientlyFails_RetriesAndSucceeds()
    {
        // Reproduces the transient mount failure path: source exists on
        // disk, multipass mount fails the first attempt, succeeds the
        // second. The retry must mask the transient and not throw.
        var hostSource = Path.Combine(_workspace, "live-source");
        Directory.CreateDirectory(hostSource);

        var attempts = 0;
        var runner = new SequencedRunner(call =>
        {
            if (call.Argv.Count >= 2 && call.Argv[1] == "mount")
            {
                attempts++;
                return attempts == 1
                    ? new ProcessRunResult(1, "", "mount failed: transient daemon glitch")
                    : new ProcessRunResult(0, "", "");
            }
            return new ProcessRunResult(0, "", "");
        });
        var provider = NewProvider(runner);

        await provider.MountSingleBindWithRetryAsync(
            new MultipassSandboxOptions(),
            name: "codeybox-test",
            host: hostSource,
            sandbox: "/repo",
            workItemId: null,
            ct: CancellationToken.None);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task MountSingleBindWithRetry_HostSourceMissing_FailsFastWithoutRetry()
    {
        // If the source doesn't exist on the host filesystem, no number of
        // retries will help — retrying would just waste audit budget and
        // delay the surfaced error. Verify a single attempt + a clear
        // exception that names the host source.
        var missing = Path.Combine(_workspace, "does-not-exist");

        var attempts = 0;
        var runner = new SequencedRunner(call =>
        {
            if (call.Argv.Count >= 2 && call.Argv[1] == "mount")
            {
                attempts++;
                return new ProcessRunResult(1, "", "Source path does not exist");
            }
            return new ProcessRunResult(0, "", "");
        });
        var provider = NewProvider(runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.MountSingleBindWithRetryAsync(
                new MultipassSandboxOptions(),
                name: "codeybox-test",
                host: missing,
                sandbox: "/repo",
                workItemId: null,
                ct: CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.Contains(missing, ex.Message);
        Assert.Contains("exists=no", ex.Message);
    }

    [Fact]
    public async Task MountSingleBindWithRetry_PersistentFailure_ThrowsWithSourceStateAndStderr()
    {
        // After exhausting retries, the thrown InvalidOperationException must
        // carry both the multipass stderr and the host-side source state at
        // mount time. Without that, future incidents would need a manual
        // reproduction step to attribute the failure.
        var hostSource = Path.Combine(_workspace, "live-but-mount-rejected");
        Directory.CreateDirectory(hostSource);

        var attempts = 0;
        var runner = new SequencedRunner(call =>
        {
            if (call.Argv.Count >= 2 && call.Argv[1] == "mount")
            {
                attempts++;
                return new ProcessRunResult(1, "", "Source path does not exist");
            }
            return new ProcessRunResult(0, "", "");
        });
        var provider = NewProvider(runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.MountSingleBindWithRetryAsync(
                new MultipassSandboxOptions(),
                name: "codeybox-test",
                host: hostSource,
                sandbox: "/repo",
                workItemId: null,
                ct: CancellationToken.None));

        Assert.Equal(3, attempts);
        Assert.Contains("exists=dir", ex.Message);
        Assert.Contains("Source path does not exist", ex.Message);
        Assert.Contains(hostSource, ex.Message);
    }

    private static MultipassSandboxProvider NewProvider(IProcessRunner runner) => new(
        new MultipassSandboxOptions { StagingDirectory = Path.GetTempPath() },
        NullLogger<MultipassSandboxProvider>.Instance,
        timings: null,
        runner: runner);

    private sealed record RecordedCall(IReadOnlyList<string> Argv, string? Stdin);

    private sealed class SequencedRunner : IProcessRunner
    {
        private readonly Func<RecordedCall, ProcessRunResult> _react;

        public SequencedRunner(Func<RecordedCall, ProcessRunResult> react) => _react = react;

        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null)
            => Task.FromResult(_react(new RecordedCall(argv.ToArray(), stdin)));
    }
}
