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
    public async Task DescribeMountSourceState_ExistingDirectory_ReportsTypeAndMtime()
    {
        var dir = Path.Combine(_workspace, "live-dir");
        Directory.CreateDirectory(dir);

        var state = await NewProvider(new StubRunner()).DescribeMountSourceStateAsync(dir, CancellationToken.None);

        Assert.StartsWith("exists=dir", state);
        Assert.Contains("mtime=", state);
    }

    [Fact]
    public async Task DescribeMountSourceState_ExistingFile_ReportsTypeAndSize()
    {
        var file = Path.Combine(_workspace, "live-file.txt");
        File.WriteAllText(file, "hello multipass");

        var state = await NewProvider(new StubRunner()).DescribeMountSourceStateAsync(file, CancellationToken.None);

        Assert.StartsWith("exists=file", state);
        Assert.Contains("size=15", state);
        Assert.Contains("mtime=", state);
    }

    [Fact]
    public async Task DescribeMountSourceState_MissingPath_ReturnsExistsNo()
    {
        var missing = Path.Combine(_workspace, "never-existed");

        var state = await NewProvider(new StubRunner()).DescribeMountSourceStateAsync(missing, CancellationToken.None);

        Assert.Equal("exists=no", state);
    }

    [Fact]
    public async Task DescribeMountSourceState_PathWithEmbeddedNull_DoesNotPropagateException()
    {
        // Inputs that Directory.Exists/File.Exists return false for must not
        // accidentally surface a typed-exception via DirectoryInfo construction
        // and abort the mount loop. Whatever the runtime classifies an
        // embedded-NUL path as ("exists=no" if filtered out early; "stat-failed"
        // if it reaches DirectoryInfo), the call must return cleanly.
        var pathological = "\0not-a-real-path";

        var state = await NewProvider(new StubRunner()).DescribeMountSourceStateAsync(pathological, CancellationToken.None);

        Assert.True(state == "exists=no" || state.StartsWith("stat-failed=", StringComparison.Ordinal),
            $"unexpected state for pathological path: {state}");
    }

    [Fact]
    public async Task DescribeMountSourceState_OnLinux_RoutesStatThroughInjectedRunner()
    {
        // On Linux, the daemon's UID is what AppArmor confines; logging
        // user:group(uid:gid) mode=NNN lets operators see "the orchestrator
        // could stat it as user X, but multipass-daemon runs as Y" at a
        // glance from the audit trail. The lookup must route through the
        // injected IProcessRunner so test doubles intercept it; the previous
        // implementation spawned `stat` directly via Process.Start which
        // bypassed the mockable abstraction.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        var dir = Path.Combine(_workspace, "live-dir-owner");
        Directory.CreateDirectory(dir);

        IReadOnlyList<string>? observedArgv = null;
        var runner = new SequencedRunner(call =>
        {
            if (call.Argv.Count > 0 && call.Argv[0] == "stat")
            {
                observedArgv = call.Argv;
                return new ProcessRunResult(0, "alice:devs(1000:1000) mode=755", "");
            }
            return new ProcessRunResult(0, "", "");
        });
        var state = await NewProvider(runner).DescribeMountSourceStateAsync(dir, CancellationToken.None);

        Assert.NotNull(observedArgv);
        Assert.Contains("stat", observedArgv!);
        Assert.Contains(dir, observedArgv!);
        Assert.Contains("owner=alice:devs(1000:1000)", state);
        Assert.Contains("mode=755", state);
    }

    [Fact]
    public async Task DescribeMountSourceState_StatNonZero_RecordsExitCode()
    {
        // A non-zero stat exit (permission denied, removed mid-stat) must
        // surface in the diagnostic string rather than crash the mount loop.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        var dir = Path.Combine(_workspace, "live-dir-stat-fail");
        Directory.CreateDirectory(dir);

        var runner = new SequencedRunner(call =>
            call.Argv.Count > 0 && call.Argv[0] == "stat"
                ? new ProcessRunResult(1, "", "permission denied")
                : new ProcessRunResult(0, "", ""));

        var state = await NewProvider(runner).DescribeMountSourceStateAsync(dir, CancellationToken.None);

        Assert.Contains("owner=stat-rc=1", state);
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
    public async Task MountSingleBindWithRetry_AlreadyMounted_TreatsAsSuccess()
    {
        var hostSource = Path.Combine(_workspace, "already-mounted-source");
        Directory.CreateDirectory(hostSource);

        var mountAttempts = 0;
        var runner = new SequencedRunner(call =>
        {
            if (call.Argv.Count >= 2 && call.Argv[1] == "mount")
            {
                mountAttempts++;
                return new ProcessRunResult(1, "", "\"/repo\" is already mounted");
            }
            if (call.Argv.Count >= 2 && call.Argv[1] == "info")
                return new ProcessRunResult(0, "", "");
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

        Assert.Equal(1, mountAttempts);
    }

    [Fact]
    public async Task MountSingleBindWithRetry_AlreadyMountedWithDifferentSource_UnmountsAndRemounts()
    {
        var hostSource = Path.Combine(_workspace, "correct-source");
        Directory.CreateDirectory(hostSource);
        var calls = new List<string>();

        var runner = new SequencedRunner(call =>
        {
            if (call.Argv.Count >= 2 && call.Argv[1] == "mount")
            {
                calls.Add("mount");
                return calls.Count(c => c == "mount") == 1
                    ? new ProcessRunResult(1, "", "\"/repo\" is already mounted")
                    : new ProcessRunResult(0, "", "");
            }
            if (call.Argv.Count >= 2 && call.Argv[1] == "info")
            {
                var stdout = """
                    {"info":{"codeybox-test":{"mounts":{"/repo":{"source_path":"/old/source"}}}}}
                    """;
                return new ProcessRunResult(0, stdout, "");
            }
            if (call.Argv.Count >= 2 && call.Argv[1] == "umount")
            {
                calls.Add("umount");
                return new ProcessRunResult(0, "", "");
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

        Assert.Equal(["mount", "umount", "mount"], calls);
    }

    [Fact]
    public async Task MountSingleBindWithRetry_HostSourceMissing_ThrowsTypedExceptionWithoutRetry()
    {
        // If the source doesn't exist on the host filesystem, no number of
        // retries will help — retrying would just waste audit budget and
        // delay the surfaced error. The provider must throw the typed
        // SandboxMountSourceMissingException carrying the host path so the
        // orchestrator (the only layer with merge-staging recovery
        // knowledge) can decide whether to re-clone and retry CreateAsync.
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

        var ex = await Assert.ThrowsAsync<SandboxMountSourceMissingException>(() =>
            provider.MountSingleBindWithRetryAsync(
                new MultipassSandboxOptions(),
                name: "codeybox-test",
                host: missing,
                sandbox: "/repo",
                workItemId: null,
                ct: CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.Equal(missing, ex.HostPath);
        Assert.Contains(missing, ex.Message);
        Assert.Contains("exists=no", ex.Message);
        // Regression: previously the message hard-coded "after 3 attempts"
        // (the retry budget) even when the loop broke after the first
        // attempt. Operators reading the audit trail must see the actual
        // attempt count so a fast missing-source failure does not look like
        // an exhausted retry loop.
        Assert.Contains("after 1 attempt", ex.Message);
    }

    [Fact]
    public async Task MountSingleBindWithRetry_PersistentFailure_ThrowsWithSourceStateAndStderr()
    {
        // After exhausting retries on a non-missing-source failure (e.g.
        // exists=dir but the daemon's AppArmor profile denies the read),
        // the thrown InvalidOperationException must carry both the multipass
        // stderr and the host-side source state at mount time. Without
        // that, future incidents would need a manual reproduction step to
        // attribute the failure. This path is distinct from the
        // missing-source path (which throws SandboxMountSourceMissingException
        // immediately) because re-cloning would not heal it.
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

    /// <summary>
    /// Returns a successful empty ProcessRunResult for any call. Used by
    /// DescribeMountSourceState tests that do not care about stat output —
    /// the diagnostic format pinned by the test is the existence/type/size
    /// prefix, not the owner/mode trailer.
    /// </summary>
    private sealed class StubRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null)
            => Task.FromResult(new ProcessRunResult(0, "", ""));
    }
}
