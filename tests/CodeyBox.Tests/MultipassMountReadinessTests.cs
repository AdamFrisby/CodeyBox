using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox.Multipass;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <c>MultipassSandboxProvider.WaitForMountsVisibleAsync</c>,
/// the in-VM mount-readiness gate that closes the
/// "<c>multipass start</c> returns Running before the native virtiofs mount
/// is attached to the guest" race.
///
/// Background: native (virtiofs) bind mounts are registered while the VM is
/// Stopped, but <c>multipass start</c> returns as soon as QEMU is Running —
/// the guest-side mount attach can lag by seconds under audit-parallelism
/// load. Without this gate the first in-VM consumer (typically
/// <c>git clone /repo /work</c>) races the attach and exits 128
/// terminally. The gate polls <c>multipass exec &lt;name&gt; -- test -e
/// &lt;path&gt;</c> per declared mount with bounded retry; a persistent
/// absence throws a retryable provisioning-deferred exception via the
/// existing <c>ThrowProvisioningDeferred</c> path so the orchestrator
/// re-queues the work item rather than failing it as exit-128.
/// </summary>
public sealed class MultipassMountReadinessTests
{
    [Fact]
    public async Task WaitForMountsVisible_TransientAttachLag_PollsThenProceeds()
    {
        // (a) `test -e` returns non-zero for the first N polls then zero ->
        // provider waits then proceeds, no throw. Verifies the self-heal
        // path that covers the observed attach-lag race.
        var hostSource = "/host/bare/repo";
        var probeCalls = new List<IReadOnlyList<string>>();
        const int failingProbes = 3;

        var runner = new SequencedRunner(call =>
        {
            if (IsExecTestE(call.Argv))
            {
                probeCalls.Add(call.Argv);
                // Treat each test-e as a probe; non-zero (1) means
                // "not visible yet", zero means "mount is live".
                return probeCalls.Count <= failingProbes
                    ? new ProcessRunResult(1, "", "")
                    : new ProcessRunResult(0, "", "");
            }
            return new ProcessRunResult(0, "", "");
        });
        var provider = NewProvider(runner);
        var delays = new List<TimeSpan>();

        await provider.WaitForMountsVisibleAsync(
            new MultipassSandboxOptions(),
            name: "codeybox-test",
            binds: new[] { (hostSource, "/repo") },
            workItemId: null,
            ct: CancellationToken.None,
            // Test-only zero-delay backoff so the bounded budget does not
            // sleep through the run wall-clock.
            backoff: _ => TimeSpan.Zero,
            delay: (d, _) => { delays.Add(d); return Task.CompletedTask; });

        Assert.Equal(failingProbes + 1, probeCalls.Count);
        Assert.Equal(failingProbes, delays.Count);
        // Confirm the probe is the bare-repo content probe (HEAD), not the
        // mountpoint itself: a stale mountpoint can exist on the guest fs
        // even when the virtiofs attach is not yet live, so the gate
        // requires HEAD to be visible inside /repo.
        Assert.Contains("/repo/HEAD", probeCalls[0]);
        Assert.Contains("test", probeCalls[0]);
        Assert.Contains("-e", probeCalls[0]);
        Assert.Contains("exec", probeCalls[0]);
        Assert.Contains("codeybox-test", probeCalls[0]);
    }

    [Fact]
    public async Task WaitForMountsVisible_PersistentAbsence_ThrowsProvisioningDeferred()
    {
        // (b) `test -e` never returns zero -> provider throws a
        // provisioning-deferred exception (assert the deferred/retryable
        // type), and the clone is never reached.
        var hostSource = "/host/bare/repo-stuck";
        var probeCalls = new List<IReadOnlyList<string>>();
        const int maxAttempts = 4;

        var runner = new SequencedRunner(call =>
        {
            if (IsExecTestE(call.Argv))
            {
                probeCalls.Add(call.Argv);
                return new ProcessRunResult(1, "", "");
            }
            return new ProcessRunResult(0, "", "");
        });
        var provider = NewProvider(runner);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.WaitForMountsVisibleAsync(
                new MultipassSandboxOptions(),
                name: "codeybox-test",
                binds: new[] { (hostSource, "/repo") },
                workItemId: null,
                ct: CancellationToken.None,
                maxAttempts: maxAttempts,
                backoff: _ => TimeSpan.Zero,
                delay: static (_, _) => Task.CompletedTask));

        Assert.Equal(maxAttempts, probeCalls.Count);
        Assert.Equal("multipass", ex.Provider);
        Assert.Equal("mount-readiness", ex.Operation);
        Assert.Equal("multipass-mount-not-visible", ex.ErrorClass);
        Assert.Contains("/repo", ex.Detail);
        Assert.Contains("codeybox-test", ex.Detail);
        // No mount-readiness call should bleed into a `git clone` here —
        // the SequencedRunner only ever saw `multipass exec ... test -e`
        // probes (the assertion above) before the provider threw.
        Assert.All(probeCalls, argv => Assert.Contains("/repo/HEAD", argv));

        // Provisioning-deferred carries a RecheckIn the orchestrator uses
        // to schedule re-pickup; the test does not pin the exact value
        // (production sources it from MultipassDaemonRetryPolicy), but it
        // must be a positive duration so the re-queue is not immediate.
        Assert.True(ex.RecheckIn > TimeSpan.Zero, $"expected positive RecheckIn, got {ex.RecheckIn}");
    }

    [Fact]
    public async Task WaitForMountsVisible_NonRepoMount_ProbesMountpointDirectly()
    {
        // For mounts other than the bare-repo `/repo`, there is no known
        // content file to use as a stricter liveness signal, so the probe
        // falls back to `test -e <SandboxPath>` against the mount target
        // itself. Pin this contract so a future refactor cannot silently
        // change the probe shape per mount type.
        var probeCalls = new List<IReadOnlyList<string>>();
        var runner = new SequencedRunner(call =>
        {
            if (IsExecTestE(call.Argv))
            {
                probeCalls.Add(call.Argv);
                return new ProcessRunResult(0, "", "");
            }
            return new ProcessRunResult(0, "", "");
        });
        var provider = NewProvider(runner);

        await provider.WaitForMountsVisibleAsync(
            new MultipassSandboxOptions(),
            name: "codeybox-test",
            binds: new[] { ("/host/staging/work", "/work") },
            workItemId: null,
            ct: CancellationToken.None,
            backoff: _ => TimeSpan.Zero,
            delay: static (_, _) => Task.CompletedTask);

        var probe = Assert.Single(probeCalls);
        Assert.Contains("/work", probe);
        Assert.DoesNotContain("/work/HEAD", probe);
    }

    [Fact]
    public async Task WaitForMountsVisible_NoMounts_NoOp()
    {
        // Empty bind list is a valid input (e.g. a tmpfs-only sandbox
        // spec): the gate must return without issuing any probes.
        var probeCalls = 0;
        var runner = new SequencedRunner(call =>
        {
            if (IsExecTestE(call.Argv)) probeCalls++;
            return new ProcessRunResult(0, "", "");
        });
        var provider = NewProvider(runner);

        await provider.WaitForMountsVisibleAsync(
            new MultipassSandboxOptions(),
            name: "codeybox-test",
            binds: Array.Empty<(string, string)>(),
            workItemId: null,
            ct: CancellationToken.None);

        Assert.Equal(0, probeCalls);
    }

    private static bool IsExecTestE(IReadOnlyList<string> argv)
    {
        // multipass exec <name> -- test -e <path>
        // argv[1] == "exec", and "test" must appear later in the argv.
        if (argv.Count < 6) return false;
        if (argv[1] != "exec") return false;
        return argv.Contains("test") && argv.Contains("-e");
    }

    private static MultipassSandboxProvider NewProvider(IProcessRunner runner) => new(
        new MultipassSandboxOptions { StagingDirectory = Path.GetTempPath() },
        NullLogger<MultipassSandboxProvider>.Instance,
        timings: null,
        runner: runner,
        daemonRetryPolicy: null);

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
            IReadOnlyDictionary<string, string>? environment = null,
            bool killOnOutputLimit = true)
            => Task.FromResult(_react(new RecordedCall(argv.ToArray(), stdin)));
    }
}
