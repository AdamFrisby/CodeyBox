using System.Globalization;

namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// Shared, bounded VM-start and guest-control preparation used by initial
/// provisioning and by interrupted-exec recovery.
/// </summary>
internal static class IncusGuestLifecycle
{
    internal static async Task StartAndWaitForAgentAsync(
        IncusCliRunner cli,
        IncusSandboxOptions options,
        string name,
        TimeProvider timeProvider,
        Func<CancellationToken, Task> authorizeStart,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(authorizeStart);
        IncusInputValidation.ValidateInstanceName(name, nameof(name));

        // Authorization is deliberately inside the lifecycle sink so no
        // future caller can start a VM after only a distant topology check.
        await authorizeStart(ct).ConfigureAwait(false);
        await cli.RunCheckedAsync(
            "start VM",
            options,
            IncusCommandBuilder.Prefix(options, "start", name),
            stdin: null,
            options.VmStartTimeout,
            ct).ConfigureAwait(false);
        await WaitForAgentAsync(cli, options, name, timeProvider, ct).ConfigureAwait(false);
    }

    internal static async Task PrepareRuntimeDirectoryAsync(
        IncusCliRunner cli,
        IncusSandboxOptions options,
        string name,
        CancellationToken ct)
    {
        await cli.RunCheckedAsync(
            "prepare guest runtime directory",
            options,
            BuildRootExec(options, name,
            [
                "install", "-d", "-m", "0700",
                "-o", options.GuestUserId.ToString(CultureInfo.InvariantCulture),
                "-g", options.GuestGroupId.ToString(CultureInfo.InvariantCulture),
                IncusCloudInit.RuntimeDirectory,
            ]),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false).ConfigureAwait(false);
        await cli.RunCheckedAsync(
            "prepare guest exec control directory",
            options,
            BuildRootExec(options, name,
            [
                "install", "-d", "-m", "0700",
                "-o", "0", "-g", "0",
                IncusCloudInit.ControlDirectory,
            ]),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false).ConfigureAwait(false);
        await cli.RunCheckedAsync(
            "verify guest exec isolation utilities",
            options,
            BuildRootExec(
                options,
                name,
                [
                    "test",
                    "-x", "/usr/bin/setpriv",
                    "-a", "-x", "/usr/bin/setsid",
                    "-a", "-x", "/usr/bin/realpath",
                ]),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: 4096,
            maxStderrBytes: 4096).ConfigureAwait(false);
    }

    internal static async Task VerifyExecWrapperAsync(
        IncusCliRunner cli,
        IncusSandboxOptions options,
        string name,
        CancellationToken ct)
    {
        await cli.RunCheckedAsync(
            "verify Incus guest exec wrapper",
            options,
            BuildRootExec(options, name, ["test", "-x", IncusCloudInit.ExecWrapperPath]),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: 4096,
            maxStderrBytes: 4096).ConfigureAwait(false);
    }

    internal static async Task MountTmpfsAsync(
        IncusCliRunner cli,
        IncusSandboxOptions options,
        string name,
        string guestPath,
        long sizeBytes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(options);
        var mount = new CodeyBox.Core.SandboxMount
        {
            SandboxPath = guestPath,
            Tmpfs = true,
            ReadOnly = false,
            SizeBytes = sizeBytes,
        };
        IncusInputValidation.ValidateInstanceName(name, nameof(name));
        IncusInputValidation.ValidateAbsoluteGuestPath(guestPath, nameof(guestPath));
        if (string.Equals(guestPath, CodeyBox.Sandbox.SandboxConventions.WorkDir, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Incus /work directory must remain backed by the VM root disk.");
        }
        IncusMountStaging.ValidateAuthorizedMountGuestPath(
            mount,
            authorizedExistingHostSource: null,
            hostSourceIsDirectory: false);
        if (sizeBytes is < 1 || sizeBytes > options.MaxTmpfsDeviceBytes)
            throw new InvalidOperationException("An Incus guest tmpfs size is outside the configured per-device bound.");

        await cli.RunCheckedAsync(
            "create guest tmpfs mount point",
            options,
            BuildRootExec(options, name,
            [
                "install", "-d", "-m", "0700",
                "-o", options.GuestUserId.ToString(CultureInfo.InvariantCulture),
                "-g", options.GuestGroupId.ToString(CultureInfo.InvariantCulture),
                guestPath,
            ]),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false).ConfigureAwait(false);
        var mountOptions = string.Join(',',
            $"size={sizeBytes.ToString(CultureInfo.InvariantCulture)}",
            "mode=0700",
            $"uid={options.GuestUserId.ToString(CultureInfo.InvariantCulture)}",
            $"gid={options.GuestGroupId.ToString(CultureInfo.InvariantCulture)}");
        await cli.RunCheckedAsync(
            "mount guest tmpfs",
            options,
            BuildRootExec(options, name, ["mount", "-t", "tmpfs", "-o", mountOptions, "tmpfs", guestPath]),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false).ConfigureAwait(false);
    }

    internal static async Task VerifyTmpfsAsync(
        IncusCliRunner cli,
        IncusSandboxOptions options,
        string name,
        string guestPath,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(options);
        IncusInputValidation.ValidateInstanceName(name, nameof(name));
        IncusInputValidation.ValidateAbsoluteGuestPath(guestPath, nameof(guestPath));
        var filesystem = await cli.RunCheckedAsync(
            "verify recovered guest tmpfs filesystem",
            options,
            BuildRootExec(options, name, ["findmnt", "-n", "-o", "FSTYPE", "--target", guestPath]),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: 128,
            maxStderrBytes: 4096).ConfigureAwait(false);
        if (!string.Equals(filesystem.Stdout.Trim(), "tmpfs", StringComparison.Ordinal))
            throw new InvalidOperationException("Recovered Incus guest tmpfs did not report the expected filesystem type.");
        var ownership = await cli.RunCheckedAsync(
            "verify recovered guest tmpfs ownership",
            options,
            BuildRootExec(options, name, ["stat", "-Lc", "%u:%g:%a", "--", guestPath]),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: 128,
            maxStderrBytes: 4096).ConfigureAwait(false);
        var expectedOwnership =
            $"{options.GuestUserId.ToString(CultureInfo.InvariantCulture)}:" +
            $"{options.GuestGroupId.ToString(CultureInfo.InvariantCulture)}:700";
        if (!string.Equals(ownership.Stdout.Trim(), expectedOwnership, StringComparison.Ordinal))
            throw new InvalidOperationException("Recovered Incus guest tmpfs did not preserve its configured ownership and mode.");
    }

    private static async Task WaitForAgentAsync(
        IncusCliRunner cli,
        IncusSandboxOptions options,
        string name,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var deadline = timeProvider.GetUtcNow() + options.VmStartTimeout;
        // Poll starting at the base interval and back off exponentially up to
        // the configured cap. Under a concurrent boot storm this spreads probes
        // across incusd instead of hammering it every base interval on every
        // still-booting VM, which is part of what starves the readiness window.
        var pollInterval = options.ReadinessPollInterval;
        var maxPollInterval = options.MaxReadinessPollInterval < options.ReadinessPollInterval
            ? options.ReadinessPollInterval
            : options.MaxReadinessPollInterval;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var probe = await cli.RunAllowFailureAsync(
                options,
                BuildRootExec(options, name, ["/bin/true"]),
                stdin: null,
                options.OperationTimeout,
                ct,
                heavyOperation: false,
                maxStdoutBytes: 4096,
                maxStderrBytes: 4096).ConfigureAwait(false);
            if (probe.Success)
                return;
            if (timeProvider.GetUtcNow() >= deadline)
            {
                // Transient under boot-storm contention: the VM booted but its
                // guest agent missed the window. Surfaced as a transient
                // timeout so the provider defers the creation for auto-retry
                // rather than failing the work item deterministically.
                throw new IncusTransientTimeoutException(
                    "guest-agent readiness",
                    $"Incus VM '{name}' did not expose its guest agent within {options.VmStartTimeout.TotalSeconds:F0} seconds.");
            }
            await Task.Delay(pollInterval, timeProvider, ct).ConfigureAwait(false);
            pollInterval = NextReadinessPollInterval(pollInterval, maxPollInterval);
        }
    }

    /// <summary>
    /// Doubles the readiness poll interval, clamped to <paramref name="max"/>.
    /// Pure so the exponential backoff schedule is unit-testable without
    /// driving the whole wait loop.
    /// </summary>
    internal static TimeSpan NextReadinessPollInterval(TimeSpan current, TimeSpan max) =>
        TimeSpan.FromTicks(Math.Min(current.Ticks * 2, max.Ticks));

    private static IReadOnlyList<string> BuildRootExec(
        IncusSandboxOptions options,
        string name,
        IReadOnlyList<string> command)
    {
        var argv = IncusCommandBuilder.Prefix(options, "exec", name, "--");
        argv.AddRange(command);
        return argv;
    }
}
