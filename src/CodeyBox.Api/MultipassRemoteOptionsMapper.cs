using CodeyBox.Sandbox.MultipassRemote;
using System.Collections.Generic;

namespace CodeyBox.Api;

internal static class MultipassRemoteOptionsMapper
{
    public static MultipassRemoteSandboxOptions Map(
        MultipassRemoteSandboxConfig? cfg,
        IReadOnlyDictionary<string, string>? networkProfiles = null)
    {
        cfg ??= new MultipassRemoteSandboxConfig();
        var fromDefaults = new MultipassRemoteSandboxOptions();
        return new MultipassRemoteSandboxOptions
        {
            SshBinary = !string.IsNullOrWhiteSpace(cfg.SshBinary) ? cfg.SshBinary! : fromDefaults.SshBinary,
            SshTarget = cfg.SshTarget ?? "",
            SshPort = cfg.SshPort,
            SshKeyPath = cfg.SshKeyPath,
            ExtraSshOptions = cfg.ExtraSshOptions?.ToArray() ?? [],
            AcceptUnknownHostKeys = cfg.AcceptUnknownHostKeys,
            ServerAliveIntervalSeconds = cfg.ServerAliveIntervalSeconds ?? fromDefaults.ServerAliveIntervalSeconds,
            ServerAliveCountMax = cfg.ServerAliveCountMax ?? fromDefaults.ServerAliveCountMax,
            ConnectTimeoutSeconds = cfg.ConnectTimeoutSeconds ?? fromDefaults.ConnectTimeoutSeconds,
            LocalTarBinary = !string.IsNullOrWhiteSpace(cfg.LocalTarBinary) ? cfg.LocalTarBinary! : fromDefaults.LocalTarBinary,
            StageOutMaxArchiveBytes = cfg.StageOutMaxArchiveBytes ?? fromDefaults.StageOutMaxArchiveBytes,
            StageOutMaxEntries = cfg.StageOutMaxEntries ?? fromDefaults.StageOutMaxEntries,
            StageOutMaxExpansionRatio = cfg.StageOutMaxExpansionRatio ?? fromDefaults.StageOutMaxExpansionRatio,
            RemoteMultipassPath = !string.IsNullOrWhiteSpace(cfg.RemoteMultipassPath) ? cfg.RemoteMultipassPath! : fromDefaults.RemoteMultipassPath,
            RemoteStagingRoot = !string.IsNullOrWhiteSpace(cfg.RemoteStagingRoot) ? cfg.RemoteStagingRoot! : fromDefaults.RemoteStagingRoot,
            DefaultImage = cfg.DefaultImage,
            VmStartTimeout = cfg.VmStartTimeout ?? fromDefaults.VmStartTimeout,
            VmStopTimeout = cfg.VmStopTimeout ?? fromDefaults.VmStopTimeout,
            VmStateCheckInterval = cfg.VmStateCheckInterval ?? fromDefaults.VmStateCheckInterval,
            VmNamePrefix = !string.IsNullOrWhiteSpace(cfg.VmNamePrefix) ? cfg.VmNamePrefix! : fromDefaults.VmNamePrefix,
            MaxConcurrentSandboxes = cfg.MaxConcurrentSandboxes,
            Cordoned = cfg.Cordoned,
            Healthy = cfg.Healthy,
            AllowedNetworkProfiles = cfg.AllowedNetworkProfiles?.ToArray() ?? [],
            NetworkProfiles = networkProfiles is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(networkProfiles, StringComparer.OrdinalIgnoreCase),
            PlacementRecheckIn = cfg.PlacementRecheckIn ?? fromDefaults.PlacementRecheckIn,
            RuntimeUnhealthyBackoff = cfg.RuntimeUnhealthyBackoff ?? fromDefaults.RuntimeUnhealthyBackoff,
            ExecutorHosts = cfg.ExecutorHosts?.Select(h => new MultipassRemoteExecutorHostOptions
            {
                Id = h.Id,
                SshTarget = h.SshTarget,
                SshBinary = h.SshBinary,
                SshPort = h.SshPort,
                SshKeyPath = h.SshKeyPath,
                ExtraSshOptions = h.ExtraSshOptions?.ToArray(),
                AcceptUnknownHostKeys = h.AcceptUnknownHostKeys,
                ServerAliveIntervalSeconds = h.ServerAliveIntervalSeconds,
                ServerAliveCountMax = h.ServerAliveCountMax,
                ConnectTimeoutSeconds = h.ConnectTimeoutSeconds,
                LocalTarBinary = h.LocalTarBinary,
                StageOutMaxArchiveBytes = h.StageOutMaxArchiveBytes,
                StageOutMaxEntries = h.StageOutMaxEntries,
                StageOutMaxExpansionRatio = h.StageOutMaxExpansionRatio,
                RemoteMultipassPath = h.RemoteMultipassPath,
                RemoteStagingRoot = h.RemoteStagingRoot,
                DefaultImage = h.DefaultImage,
                VmStartTimeout = h.VmStartTimeout,
                VmStopTimeout = h.VmStopTimeout,
                VmStateCheckInterval = h.VmStateCheckInterval,
                VmNamePrefix = h.VmNamePrefix,
                MaxConcurrentSandboxes = h.MaxConcurrentSandboxes,
                Cordoned = h.Cordoned,
                Healthy = h.Healthy,
                AllowedNetworkProfiles = h.AllowedNetworkProfiles?.ToArray(),
            }).ToArray() ?? [],
        };
    }
}
