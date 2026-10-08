using System.Globalization;
using CodeyBox.Core;

namespace CodeyBox.HetznerSandboxPlugin;

/// <summary>Outcome of one smoke run through <see cref="HetznerSandboxSmoke"/>.</summary>
public sealed record HetznerSmokeResult(
    bool Succeeded,
    IReadOnlyList<HetznerSmokeStep> Timings,
    string UnameOutput,
    string? Failure);

/// <summary>One timed step of a smoke run (step name plus elapsed wall time).</summary>
public sealed record HetznerSmokeStep(string Name, TimeSpan Elapsed);

/// <summary>
/// Real end-to-end smoke flow for the Hetzner Cloud provider (no fake
/// injected): acquires a server, runs <c>uname -a</c>, stages a token file
/// in, reads it back, writes one out, syncs state back, and reads the echo on
/// the host. Never throws: failures (including disposal failures, which may
/// mean the server leaked) are reported in the returned
/// <see cref="HetznerSmokeResult"/>.
/// </summary>
public static class HetznerSandboxSmoke
{
    private const string SmokeInboundFileName = "inbound-token.txt";
    private const string SmokeOutboundFileName = "outbound-echo.txt";
    private const string SmokeMountGuestPath = "/work/smoke";

    /// <summary>
    /// Runs the smoke flow against a provider built from explicit
    /// <paramref name="options"/> (production path).
    /// </summary>
    public static async Task<HetznerSmokeResult> RunAsync(
        HetznerSandboxOptions options,
        TextWriter output,
        TimeProvider? clock = null,
        string? roundTripToken = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        using var provider = new HetznerSandboxProvider(
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
    public static async Task<HetznerSmokeResult> RunAsync(
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
        var timings = new List<HetznerSmokeStep>();
        var unameOutput = string.Empty;

        var hostDir = Path.Combine(
            Path.GetTempPath(),
            "codeybox-hetzner-smoke-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(hostDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(hostDir, SmokeInboundFileName), token, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DeleteHostDir(hostDir);
            return new HetznerSmokeResult(false, timings, string.Empty, "seed host dir: " + ex.Message);
        }

        ISandbox? sandbox = null;
        HetznerSmokeResult? pending = null;
        string? disposeFailure = null;
        try
        {
            var start = time.GetTimestamp();
            sandbox = await provider.CreateAsync(new SandboxSpec
            {
                ImageReference = string.Empty,
                Mounts = [new SandboxMount { SandboxPath = SmokeMountGuestPath, HostPath = hostDir, ReadOnly = false }],
            }, ct).ConfigureAwait(false);
            timings.Add(new HetznerSmokeStep("acquire", time.GetElapsedTime(start)));
            await output.WriteLineAsync($"[smoke] acquired {sandbox.Id} in {timings[^1].Elapsed}").ConfigureAwait(false);

            start = time.GetTimestamp();
            var uname = await sandbox.ExecAsync(new SandboxExec { Argv = ["uname", "-a"] }, ct).ConfigureAwait(false);
            var execFailure = ExecFailure("uname -a", uname);
            if (execFailure is not null)
            {
                pending = new HetznerSmokeResult(false, timings, string.Empty, execFailure);
            }
            else
            {
                unameOutput = uname.Stdout.Trim();
                timings.Add(new HetznerSmokeStep("uname", time.GetElapsedTime(start)));
                await output.WriteLineAsync($"[smoke] uname -a in {timings[^1].Elapsed}: {unameOutput}").ConfigureAwait(false);

                start = time.GetTimestamp();
                var inbound = await sandbox.ExecAsync(
                    new SandboxExec { Argv = ["cat", SmokeMountGuestPath + "/" + SmokeInboundFileName] }, ct).ConfigureAwait(false);
                execFailure = ExecFailure("stage read", inbound);
                if (execFailure is not null)
                {
                    pending = new HetznerSmokeResult(false, timings, unameOutput, execFailure);
                }
                else if (!string.Equals(inbound.Stdout, token, StringComparison.Ordinal))
                {
                    pending = new HetznerSmokeResult(false, timings, unameOutput,
                        "stage read mismatch: guest content differs from the seeded host file");
                }
                else
                {
                    timings.Add(new HetznerSmokeStep("stage-read", time.GetElapsedTime(start)));
                    await output.WriteLineAsync($"[smoke] stage read ok in {timings[^1].Elapsed}").ConfigureAwait(false);

                    start = time.GetTimestamp();
                    var outbound = await sandbox.ExecAsync(
                        new SandboxExec { Argv = ["tee", SmokeMountGuestPath + "/" + SmokeOutboundFileName], Stdin = token }, ct).ConfigureAwait(false);
                    execFailure = ExecFailure("stage write", outbound);
                    if (execFailure is not null)
                    {
                        pending = new HetznerSmokeResult(false, timings, unameOutput, execFailure);
                    }
                    else
                    {
                        await sandbox.SyncStateToHostAsync(ct).ConfigureAwait(false);
                        var echoed = await File.ReadAllTextAsync(Path.Combine(hostDir, SmokeOutboundFileName), ct).ConfigureAwait(false);
                        if (!string.Equals(echoed, token, StringComparison.Ordinal))
                        {
                            pending = new HetznerSmokeResult(false, timings, unameOutput,
                                "stage round-trip mismatch: synced-back content differs from what the guest wrote");
                        }
                        else
                        {
                            timings.Add(new HetznerSmokeStep("stage-write", time.GetElapsedTime(start)));
                            await output.WriteLineAsync($"[smoke] stage round-trip ok in {timings[^1].Elapsed}").ConfigureAwait(false);
                            pending = new HetznerSmokeResult(true, timings, unameOutput, null);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var reason = ex is OperationCanceledException ? "cancelled" : ex.GetType().Name + ": " + ex.Message;
            pending = new HetznerSmokeResult(false, timings, unameOutput, reason);
        }
        finally
        {
            if (sandbox is not null)
            {
                var start = time.GetTimestamp();
                try
                {
                    await sandbox.DisposeAsync().ConfigureAwait(false);
                    timings.Add(new HetznerSmokeStep("dispose", time.GetElapsedTime(start)));
                    await output.WriteLineAsync($"[smoke] disposed in {timings[^1].Elapsed}").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    disposeFailure = $"dispose failed (sandbox may leak server/SSH key/firewall/floating IP): {ex.GetType().Name}: {ex.Message}";
                    await output.WriteLineAsync($"[smoke] {disposeFailure}").ConfigureAwait(false);
                }
            }
            DeleteHostDir(hostDir);
        }

        if (disposeFailure is not null)
        {
            var stepFailure = pending?.Failure;
            var combined = stepFailure is null ? disposeFailure : stepFailure + "; " + disposeFailure;
            return new HetznerSmokeResult(false, timings, pending?.UnameOutput ?? unameOutput, combined);
        }

        return pending ?? new HetznerSmokeResult(false, timings, unameOutput, "smoke did not complete");
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
