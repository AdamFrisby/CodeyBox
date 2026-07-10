using CodeyBox.HostProcess;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CodeyBox.Sandbox.MultipassRemote;

internal sealed class RemoteMultipassCleanup
{
    private readonly MultipassRemoteSandboxOptions _opts;
    private readonly IRemoteHostTransport _transport;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<ProcessRunResultLike>> _runRemoteMaybeGated;
    private readonly Action<RemoteSshTransportException> _onTransportFailure;
    private readonly ILogger _log;

    public RemoteMultipassCleanup(
        MultipassRemoteSandboxOptions opts,
        IRemoteHostTransport transport,
        Func<IReadOnlyList<string>, CancellationToken, Task<ProcessRunResultLike>> runRemoteMaybeGated,
        Action<RemoteSshTransportException> onTransportFailure,
        ILogger log)
    {
        _opts = opts;
        _transport = transport;
        _runRemoteMaybeGated = runRemoteMaybeGated;
        _onTransportFailure = onTransportFailure;
        _log = log;
    }

    public async Task DeleteVmAndStagingOrThrowAsync(
        string vmName,
        string remoteSandboxRoot,
        CancellationToken ct)
    {
        ValidateCleanupTarget(vmName, remoteSandboxRoot);

        var delete = await RunMultipassDeleteAsync(vmName, ct).ConfigureAwait(false);
        if (delete.ExitCode != 0)
        {
            if (await SandboxMayStillExistAfterFailedDeleteAsync(vmName, ct).ConfigureAwait(false))
            {
                throw new RemoteHostProvisioningException(
                    _opts.HostId,
                    "delete",
                    $"Remote cleanup command 'delete' for VM '{vmName}' exited {delete.ExitCode}: {RemoteMultipassText.TruncateForLog(delete.Stderr)}");
            }

            _log.LogWarning(
                "Remote VM {Vm} on host {HostId} was already absent after delete --purge exited {ExitCode}; continuing staging cleanup",
                vmName,
                _opts.HostId,
                delete.ExitCode);
        }

        var rm = await RunStagingCleanupAsync(remoteSandboxRoot, ct).ConfigureAwait(false);
        if (rm.ExitCode != 0)
        {
            throw new RemoteHostProvisioningException(
                _opts.HostId,
                "staging-cleanup",
                $"rm -rf -- {remoteSandboxRoot} exited {rm.ExitCode}: {RemoteMultipassText.TruncateForLog(rm.Stderr)}");
        }
    }

    public async Task<bool> TryDeleteVmAndStagingAsync(
        string vmName,
        string remoteSandboxRoot,
        CancellationToken ct)
    {
        try
        {
            await DeleteVmAndStagingOrThrowAsync(vmName, remoteSandboxRoot, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (RemoteSshTransportException ex)
        {
            _log.LogWarning(
                ex,
                "Best-effort remote cleanup of {Vm} on host {HostId} failed (transport); leaving for future leak reaper sweep",
                vmName,
                _opts.HostId);
            return false;
        }
        catch (RemoteHostProvisioningException ex)
        {
            _log.LogWarning(
                ex,
                "Best-effort remote cleanup of {Vm} on host {HostId} failed during {Operation}; leaving for future leak reaper sweep",
                vmName,
                _opts.HostId,
                ex.Operation);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Best-effort remote cleanup of {Vm} on host {HostId} failed", vmName, _opts.HostId);
            return false;
        }
    }

    private void ValidateCleanupTarget(string vmName, string remoteSandboxRoot)
    {
        RemoteMultipassVmNames.ValidateManagedVmNameForPrefix(vmName, _opts.VmNamePrefix);
        RemoteMultipassVmNames.ValidateRemoteSandboxRoot(_opts.RemoteStagingRoot, vmName, remoteSandboxRoot);
    }

    private async Task<ProcessRunResultLike> RunMultipassDeleteAsync(string vmName, CancellationToken ct)
    {
        try
        {
            return await _runRemoteMaybeGated(
                [_opts.RemoteMultipassPath, "delete", "--purge", vmName],
                ct).ConfigureAwait(false);
        }
        catch (RemoteSshTransportException ex)
        {
            _onTransportFailure(ex);
            throw;
        }
    }

    private async Task<ProcessRunResult> RunStagingCleanupAsync(string remoteSandboxRoot, CancellationToken ct)
    {
        try
        {
            var result = await _transport.RunAsync(
                ["rm", "-rf", "--", remoteSandboxRoot],
                stdin: null,
                ct: ct,
                maxStdoutBytes: _opts.RemoteInventoryMaxOutputBytes,
                maxStderrBytes: _opts.RemoteInventoryMaxOutputBytes,
                killOnOutputLimit: true).ConfigureAwait(false);
            if (result.StdoutLimitExceeded || result.StderrLimitExceeded)
            {
                var streams = result.StdoutLimitExceeded && result.StderrLimitExceeded
                    ? "stdout/stderr"
                    : result.StdoutLimitExceeded
                        ? "stdout"
                        : "stderr";
                throw new RemoteHostProvisioningException(
                    _opts.HostId,
                    "staging-cleanup",
                    $"rm -rf -- {remoteSandboxRoot} exceeded RemoteInventoryMaxOutputBytes={_opts.RemoteInventoryMaxOutputBytes} on {streams}");
            }

            return result;
        }
        catch (RemoteSshTransportException ex)
        {
            _onTransportFailure(ex);
            throw;
        }
    }

    private async Task<bool> SandboxMayStillExistAfterFailedDeleteAsync(string vmName, CancellationToken ct)
    {
        try
        {
            var list = await _transport.RunAsync(
                [_opts.RemoteMultipassPath, "list", "--format", "json"],
                stdin: null,
                ct,
                maxStdoutBytes: _opts.RemoteInventoryMaxOutputBytes,
                maxStderrBytes: _opts.RemoteInventoryMaxOutputBytes,
                killOnOutputLimit: true).ConfigureAwait(false);
            if (list.StdoutLimitExceeded || list.StderrLimitExceeded)
            {
                var streams = list.StdoutLimitExceeded && list.StderrLimitExceeded
                    ? "stdout/stderr"
                    : list.StdoutLimitExceeded
                        ? "stdout"
                        : "stderr";
                _log.LogWarning(
                    "Could not prove remote sandbox {Vm} on host {HostId} was absent after delete --purge failed because multipass list exceeded the {MaxBytes} byte {Streams} cap",
                    vmName,
                    _opts.HostId,
                    _opts.RemoteInventoryMaxOutputBytes,
                    streams);
                return true;
            }

            if (list.ExitCode == 0)
                return ListJsonContainsVm(list.Stdout, vmName);

            _log.LogWarning(
                "Could not prove remote sandbox {Vm} on host {HostId} was absent after delete --purge failed (list exit {ExitCode}): {Stderr}",
                vmName,
                _opts.HostId,
                list.ExitCode,
                RemoteMultipassText.TruncateForLog(list.Stderr));
            return true;
        }
        catch (RemoteSshTransportException ex)
        {
            _onTransportFailure(ex);
            _log.LogWarning(
                ex,
                "Could not prove remote sandbox {Vm} on host {HostId} was absent after delete --purge failed",
                vmName,
                _opts.HostId);
            return true;
        }
        catch (JsonException ex)
        {
            _log.LogWarning(
                ex,
                "Could not prove remote sandbox {Vm} on host {HostId} was absent after delete --purge failed because multipass list JSON was malformed",
                vmName,
                _opts.HostId);
            return true;
        }
    }

    private static bool ListJsonContainsVm(string json, string vmName)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            return true;

        foreach (var entry in list.EnumerateArray())
        {
            if (!entry.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                continue;
            if (string.Equals(nameEl.GetString(), vmName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
