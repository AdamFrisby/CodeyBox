using System.Globalization;
using CodeyBox.Core;

namespace CodeyBox.OpenStackSandboxPlugin;

/// <summary>
/// Timed outcome of one <see cref="OpenStackSandboxSmoke"/> step.
/// <see cref="Elapsed"/> is wall-clock time for the step (acquire includes
/// cloud boot plus SSH readiness; dispose covers cloud teardown).
/// </summary>
public sealed record OpenStackSmokeStep(string Name, TimeSpan Elapsed);

/// <summary>
/// Outcome of an OpenStack sandbox smoke run: acquire → <c>uname -a</c> →
/// file stage round-trip → dispose. <see cref="Succeeded"/> is false when any
/// step failed; <see cref="Failure"/> names the failing step and reason.
/// The sandbox is disposed on every path, including mid-run failure and
/// cancellation — a false <see cref="Succeeded"/> never hides a leak.
/// </summary>
public sealed record OpenStackSmokeResult(
    bool Succeeded,
    IReadOnlyList<OpenStackSmokeStep> Timings,
    string UnameOutput,
    string? Failure);

/// <summary>
/// Smoke orchestration for the <c>openstack</c> sandbox provider: acquire one
/// sandbox, run <c>uname -a</c> plus a file stage round-trip (host file staged
/// in at create, read back over exec; guest-written file synced back and
/// compared on the host), then dispose it — printing per-step timings.
/// Disposal runs on every path via <c>try/finally</c>, including mid-run
/// failure and <see cref="OperationCanceledException"/> (Ctrl-C aborts the
/// current step and the sandbox is still torn down).
///
/// <para>Pure orchestration over <see cref="ISandboxProvider"/>: the live
/// entry point builds the real provider from options, while tests inject a
/// fake provider over the fake cloud. Time comes from an injected
/// <see cref="TimeProvider"/> so tests use deterministic time.</para>
/// </summary>
public static class OpenStackSandboxSmoke
{
    /// <summary>Guest directory the smoke round-trip stages through.</summary>
    public const string SmokeMountGuestPath = "/smoke";

    /// <summary>Filename seeded on the host before create and read back over exec.</summary>
    public const string SmokeInboundFileName = "smoke-in.txt";

    /// <summary>Filename written in the guest and synced back to the host.</summary>
    public const string SmokeOutboundFileName = "smoke-out.txt";

    /// <summary>
    /// Live entry point: builds the real <c>openstack</c> provider from
    /// <paramref name="options"/> (same options and <c>OS_*</c> credential
    /// chain as production) and runs the smoke flow. The provider is disposed
    /// after the run; the sandbox is disposed before that on every path.
    /// </summary>
    public static async Task<OpenStackSmokeResult> RunAsync(
        OpenStackSandboxOptions options,
        TextWriter output,
        TimeProvider? clock = null,
        string? roundTripToken = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        using var provider = new OpenStackSandboxProvider(
            readOptions: () => options,
            http: null,
            keys: null,
            dns: null,
            transports: null,
            environment: null,
            clock: clock,
            log: null);
        return await RunAsync(provider, output, clock, roundTripToken, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Test seam: runs the smoke flow against any <see cref="ISandboxProvider"/>
    /// (fake cloud in tests, real provider in production).
    /// </summary>
    public static async Task<OpenStackSmokeResult> RunAsync(
        ISandboxProvider provider,
        TextWriter output,
        TimeProvider? clock = null,
        string? roundTripToken = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(output);
        var time = clock ?? TimeProvider.System;
        var token = roundTripToken ?? "smoke-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var timings = new List<OpenStackSmokeStep>();
        var unameOutput = string.Empty;

        var hostDir = Path.Combine(
            Path.GetTempPath(),
            "codeybox-openstack-smoke-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(hostDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(hostDir, SmokeInboundFileName), token, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DeleteHostDir(hostDir);
            return new OpenStackSmokeResult(false, timings, string.Empty, "seed host dir: " + ex.Message);
        }

        ISandbox? sandbox = null;
        OpenStackSmokeResult? pending = null;
        string? disposeFailure = null;
        try
        {
            var start = time.GetTimestamp();
            sandbox = await provider.CreateAsync(new SandboxSpec
            {
                ImageReference = string.Empty,
                Mounts = [new SandboxMount { SandboxPath = SmokeMountGuestPath, HostPath = hostDir, ReadOnly = false }],
            }, ct).ConfigureAwait(false);
            timings.Add(new OpenStackSmokeStep("acquire", time.GetElapsedTime(start)));
            await output.WriteLineAsync($"[smoke] acquired {sandbox.Id} in {timings[^1].Elapsed}").ConfigureAwait(false);

            start = time.GetTimestamp();
            var uname = await sandbox.ExecAsync(new SandboxExec { Argv = ["uname", "-a"] }, ct).ConfigureAwait(false);
            var execFailure = ExecFailure("uname -a", uname);
            if (execFailure is not null)
            {
                pending = new OpenStackSmokeResult(false, timings, string.Empty, execFailure);
            }
            else
            {
                unameOutput = uname.Stdout.Trim();
                timings.Add(new OpenStackSmokeStep("uname", time.GetElapsedTime(start)));
                await output.WriteLineAsync($"[smoke] uname -a in {timings[^1].Elapsed}: {unameOutput}").ConfigureAwait(false);

                start = time.GetTimestamp();
                var inbound = await sandbox.ExecAsync(
                    new SandboxExec { Argv = ["cat", SmokeMountGuestPath + "/" + SmokeInboundFileName] }, ct).ConfigureAwait(false);
                execFailure = ExecFailure("stage read", inbound);
                if (execFailure is not null)
                {
                    pending = new OpenStackSmokeResult(false, timings, unameOutput, execFailure);
                }
                else if (!string.Equals(inbound.Stdout, token, StringComparison.Ordinal))
                {
                    pending = new OpenStackSmokeResult(false, timings, unameOutput,
                        "stage read mismatch: guest content differs from the seeded host file");
                }
                else
                {
                    timings.Add(new OpenStackSmokeStep("stage-read", time.GetElapsedTime(start)));
                    await output.WriteLineAsync($"[smoke] stage read ok in {timings[^1].Elapsed}").ConfigureAwait(false);

                    start = time.GetTimestamp();
                    var outbound = await sandbox.ExecAsync(
                        new SandboxExec { Argv = ["tee", SmokeMountGuestPath + "/" + SmokeOutboundFileName], Stdin = token }, ct).ConfigureAwait(false);
                    execFailure = ExecFailure("stage write", outbound);
                    if (execFailure is not null)
                    {
                        pending = new OpenStackSmokeResult(false, timings, unameOutput, execFailure);
                    }
                    else
                    {
                        await sandbox.SyncStateToHostAsync(ct).ConfigureAwait(false);
                        var echoed = await File.ReadAllTextAsync(Path.Combine(hostDir, SmokeOutboundFileName), ct).ConfigureAwait(false);
                        if (!string.Equals(echoed, token, StringComparison.Ordinal))
                        {
                            pending = new OpenStackSmokeResult(false, timings, unameOutput,
                                "stage round-trip mismatch: synced-back content differs from what the guest wrote");
                        }
                        else
                        {
                            timings.Add(new OpenStackSmokeStep("stage-write", time.GetElapsedTime(start)));
                            await output.WriteLineAsync($"[smoke] stage round-trip ok in {timings[^1].Elapsed}").ConfigureAwait(false);
                            pending = new OpenStackSmokeResult(true, timings, unameOutput, null);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var reason = ex is OperationCanceledException ? "cancelled" : ex.GetType().Name + ": " + ex.Message;
            pending = new OpenStackSmokeResult(false, timings, unameOutput, reason);
        }
        finally
        {
            if (sandbox is not null)
            {
                var start = time.GetTimestamp();
                try
                {
                    await sandbox.DisposeAsync().ConfigureAwait(false);
                    timings.Add(new OpenStackSmokeStep("dispose", time.GetElapsedTime(start)));
                    await output.WriteLineAsync($"[smoke] disposed in {timings[^1].Elapsed}").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    disposeFailure = $"dispose failed (sandbox may leak server/keypair/security-group): {ex.GetType().Name}: {ex.Message}";
                    await output.WriteLineAsync($"[smoke] {disposeFailure}").ConfigureAwait(false);
                }
            }
            DeleteHostDir(hostDir);
        }

        if (disposeFailure is not null)
        {
            var stepFailure = pending?.Failure;
            var combined = stepFailure is null ? disposeFailure : stepFailure + "; " + disposeFailure;
            return new OpenStackSmokeResult(false, timings, pending?.UnameOutput ?? unameOutput, combined);
        }

        return pending ?? new OpenStackSmokeResult(false, timings, unameOutput, "smoke did not complete");
    }

    private static string? ExecFailure(string step, SandboxExecResult result) =>
        result.ExitCode != 0 || result.ExecutionUnavailable
            ? $"{step} exited {result.ExitCode} (unavailable={result.ExecutionUnavailable}): {result.Stderr}"
            : null;

    private static void DeleteHostDir(string hostDir)
    {
        try
        {
            if (Directory.Exists(hostDir))
                Directory.Delete(hostDir, recursive: true);
        }
        // WHY: best-effort temp cleanup must not mask the smoke result or
        // throw from the finally path; the OS reclaims temp dirs on reboot.
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
