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
    public async Task MountSingleBindWithRetry_HostSourceMissing_InvokesRestoreCallbackAndRetries()
    {
        // The defensive heal hook: if the host source is gone at mount time
        // and the caller supplied a restore callback (merge phase re-clone),
        // invoke it once, then retry. The combined effect must be a clean
        // mount without surfacing the original failure to the work item.
        var hostSource = Path.Combine(_workspace, "heal-restored-source");
        // Source is initially absent. The callback re-creates it; the second
        // mount attempt must see it on disk and succeed.
        Assert.False(Directory.Exists(hostSource));

        var attempts = 0;
        var runner = new SequencedRunner(call =>
        {
            if (call.Argv.Count >= 2 && call.Argv[1] == "mount")
            {
                attempts++;
                return Directory.Exists(hostSource)
                    ? new ProcessRunResult(0, "", "")
                    : new ProcessRunResult(1, "", "Source path does not exist");
            }
            return new ProcessRunResult(0, "", "");
        });
        var provider = NewProvider(runner);

        var restorer = new RecordingRestorer(_ =>
        {
            Directory.CreateDirectory(hostSource);
            return Task.CompletedTask;
        });

        await provider.MountSingleBindWithRetryAsync(
            new MultipassSandboxOptions(),
            name: "codeybox-test",
            host: hostSource,
            sandbox: "/repo",
            workItemId: null,
            sourceRestorer: restorer,
            ct: CancellationToken.None);

        Assert.Equal(1, restorer.Invocations);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task MountSingleBindWithRetry_RestoreDoesNotRecreateSource_FailsFastWithoutSecondRestore()
    {
        // The fast-fail branch after the single-shot restore: the source
        // is missing, the restorer is invoked, but the restore does not
        // recreate the path (e.g. underlying git clone failed silently,
        // or external cleanup deleted it again). The next iteration must
        // not invoke the restorer a second time AND must not exhaust the
        // full retry budget — both would waste audit time and obscure the
        // structural failure in the audit trail.
        var hostSource = Path.Combine(_workspace, "heal-stays-missing");
        Assert.False(Directory.Exists(hostSource));

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

        var restorer = new RecordingRestorer(_ => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.MountSingleBindWithRetryAsync(
                new MultipassSandboxOptions(),
                name: "codeybox-test",
                host: hostSource,
                sandbox: "/repo",
                workItemId: null,
                sourceRestorer: restorer,
                ct: CancellationToken.None));

        // Exactly one restore call (the single-shot guard) and exactly two
        // mount attempts (first fails -> restore -> second fails -> fast-fail
        // since restoreInvoked is now true). A regression that retried beyond
        // attempt 2 or invoked the restorer twice would fail this test.
        Assert.Equal(1, restorer.Invocations);
        Assert.Equal(2, attempts);
        Assert.Contains("after 2 attempt", ex.Message);
        Assert.Contains("exists=no", ex.Message);
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
        // Regression: previously the message hard-coded "after 3 attempts"
        // (the retry budget) even when the loop broke after the first
        // attempt. Operators reading the audit trail must see the actual
        // attempt count so a fast missing-source failure does not look like
        // an exhausted retry loop.
        Assert.Contains("after 1 attempt", ex.Message);
    }

    // ── End-to-end CreateAsync wiring ──────────────────────────────────────

    [Fact]
    public async Task CreateAsync_SandboxMountWithSourceRestorer_RestorerReachesMountRetryLoop()
    {
        // Regression guard for the SandboxMount.SourceRestorer → bindMounts →
        // ApplyMountsAsync → MountSingleBindWithRetryAsync wiring inside
        // MultipassSandboxProvider.CreateAsync. All other restorer tests
        // exercise MountSingleBindWithRetryAsync directly with an explicitly
        // supplied restorer or invoke ISandboxMountSourceRestorer from a
        // ProcessSandbox wrapper at CreateAsync time. If the tuple wiring at
        // MultipassSandboxProvider.cs:257 regressed to drop the restorer
        // (e.g. bindMounts.Add((m.HostPath, m.SandboxPath, null))), the
        // existing 33-test restorer suite would still pass while the real
        // merge-phase mount-heal path silently broke. This test drives a
        // SandboxSpec carrying SourceRestorer through the full CreateAsync
        // path and asserts the restorer was invoked when multipass mount
        // reported a missing source.
        var hostSource = Path.Combine(_workspace, "create-async-heal-source");
        // Source initially absent so the first multipass mount call fails
        // with "Source path does not exist" and the post-failure state is
        // exists=no — the only path that reaches the restorer in
        // MountSingleBindWithRetryAsync.
        Assert.False(Directory.Exists(hostSource));

        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        var mountAttempts = 0;
        var runner = new SequencedRunner(call =>
        {
            var argv = call.Argv;

            if (argv.Count >= 2 && argv[1] == "mount")
            {
                mountAttempts++;
                return Directory.Exists(hostSource)
                    ? new ProcessRunResult(0, "", "")
                    : new ProcessRunResult(1, "", "Source path does not exist");
            }
            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                states[argv[3]] = "Running";
                return new ProcessRunResult(0, "", "");
            }
            if (argv.Count >= 4 && argv[1] == "info" && argv[3] == "--format=csv")
                return states.TryGetValue(argv[2], out var s)
                    ? new ProcessRunResult(0, s, "")
                    : new ProcessRunResult(1, "", "not found");
            if (argv.Count >= 5 && argv[1] == "exec" && argv[3] == "--" && argv[4] == "cloud-init")
                return new ProcessRunResult(0, "", "");
            if (argv.Count >= 3 && argv[1] == "stop")
            {
                states[argv[2]] = "Stopped";
                return new ProcessRunResult(0, "", "");
            }
            if (argv.Count >= 3 && argv[1] == "start")
            {
                states[argv[2]] = "Running";
                return new ProcessRunResult(0, "", "");
            }
            if (argv.Count >= 4 && argv[1] == "transfer")
                return new ProcessRunResult(0, "", "");
            if (argv.Count >= 5 && argv[1] == "exec" && argv[3] == "--" && argv[4] == "chmod")
                return new ProcessRunResult(0, "", "");
            if (argv.Count >= 4 && argv[1] == "delete" && argv[2] == "--purge")
            {
                states.Remove(argv[3]);
                return new ProcessRunResult(0, "", "");
            }
            // DescribeMountSourceState routes stat through IProcessRunner;
            // return a plausible owner/mode trailer so the diagnostic
            // helper does not fail.
            if (argv.Count > 0 && argv[0] == "stat")
                return new ProcessRunResult(0, "alice:devs(1000:1000) mode=755", "");
            return new ProcessRunResult(99, "", "unexpected argv: " + string.Join(" ", argv));
        });
        var provider = NewProvider(runner);

        var restorer = new RecordingRestorer(_ =>
        {
            Directory.CreateDirectory(hostSource);
            return Task.CompletedTask;
        });

        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            WorkingDirectory = "/work",
            Mounts =
            [
                new SandboxMount
                {
                    SandboxPath = "/repo",
                    HostPath = hostSource,
                    ReadOnly = false,
                    SourceRestorer = restorer,
                },
            ],
        };

        await using var sandbox = await provider.CreateAsync(spec, CancellationToken.None);

        // The wiring assertion: bindMounts carried m.SourceRestorer into
        // ApplyMountsAsync, which forwarded it to MountSingleBindWithRetryAsync,
        // which invoked the restorer when the post-failure stat showed
        // exists=no. A regression that dropped the restorer would leave
        // Invocations==0 — the first mount would fail, no retry would heal
        // it, and CreateAsync would throw before reaching this assertion.
        Assert.Equal(1, restorer.Invocations);
        Assert.Equal(2, mountAttempts);
        Assert.True(Directory.Exists(hostSource));
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

    [Fact]
    public async Task MountSingleBindWithRetry_HostSourceFirstGoesMissingOnFinalAttempt_InvokesRestoreAndRetries()
    {
        // Regression for iteration-7 audit Error finding (quality:llm-review):
        // when MountSingleBindWithRetryAsync ran the early `if (attempt ==
        // MountMaxAttempts) break` immediately after recording post-failure
        // state, an exists=no observed only on the final attempt would
        // never reach the restorer branch — defeating the entire purpose
        // of the heal hook in the racing-cleanup-after-two-flaky-attempts
        // case. The loop must invoke the one-shot restorer even on the
        // final attempt and grant one additional mount try after it heals.
        //
        // Sequence under test:
        //   attempt 1: source exists, mount fails (exists=dir transient)
        //   attempt 2: source exists, mount fails (exists=dir transient)
        //   attempt 3: source has been deleted out from under us (exists=no)
        //              → restore must run, fourth mount must succeed.
        var hostSource = Path.Combine(_workspace, "late-disappearing-source");
        Directory.CreateDirectory(hostSource);

        var attempts = 0;
        var runner = new SequencedRunner(call =>
        {
            if (call.Argv.Count >= 2 && call.Argv[1] == "mount")
            {
                attempts++;
                if (attempts == 3 && Directory.Exists(hostSource))
                {
                    // Simulate the external cleanup landing during the
                    // third mount call so the post-failure stat lands as
                    // exists=no — exactly the late-disappearance case the
                    // heal hook is meant to cover.
                    Directory.Delete(hostSource, recursive: true);
                }
                return Directory.Exists(hostSource) && attempts >= 4
                    ? new ProcessRunResult(0, "", "")
                    : new ProcessRunResult(1, "", "Source path does not exist");
            }
            return new ProcessRunResult(0, "", "");
        });
        var provider = NewProvider(runner);

        var restorer = new RecordingRestorer(_ =>
        {
            Directory.CreateDirectory(hostSource);
            return Task.CompletedTask;
        });

        await provider.MountSingleBindWithRetryAsync(
            new MultipassSandboxOptions(),
            name: "codeybox-test",
            host: hostSource,
            sandbox: "/repo",
            workItemId: null,
            sourceRestorer: restorer,
            ct: CancellationToken.None);

        // Four mount attempts (3 transients + 1 post-heal success), one
        // single-shot restore invocation. A regression that broke before
        // the restorer branch on the final attempt would yield Invocations
        // == 0 and an InvalidOperationException instead of returning.
        Assert.Equal(1, restorer.Invocations);
        Assert.Equal(4, attempts);
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
    /// In-test <see cref="ISandboxMountSourceRestorer"/> that runs a
    /// caller-supplied side effect and counts invocations. Replaces the
    /// pre-refactor <c>Func&lt;CancellationToken, Task&gt;</c> hook.
    /// </summary>
    private sealed class RecordingRestorer : ISandboxMountSourceRestorer
    {
        private readonly Func<CancellationToken, Task> _impl;
        public int Invocations { get; private set; }

        public RecordingRestorer(Func<CancellationToken, Task> impl) => _impl = impl;

        public Task RestoreAsync(CancellationToken ct)
        {
            Invocations++;
            return _impl(ct);
        }
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
