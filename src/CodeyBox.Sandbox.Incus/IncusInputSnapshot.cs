using System.Collections.ObjectModel;
using CodeyBox.Core;

namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// Takes bounded immutable snapshots at the provider boundary so caller-owned
/// collection implementations cannot change after validation or lie through
/// their <c>Count</c> property.
/// </summary>
internal static class IncusInputSnapshot
{
    private const int MaximumNetworkAllowedHosts = 1024;

    internal static SandboxSpec CaptureSpec(SandboxSpec source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateRequiredBoundedText(source.ImageReference, 4096, nameof(SandboxSpec.ImageReference));
        ValidateRequiredBoundedText(source.WorkingDirectory, 4096, nameof(SandboxSpec.WorkingDirectory));
        ValidateOptionalBoundedText(source.BaselineImageRef, 63, nameof(SandboxSpec.BaselineImageRef));
        ValidateOptionalBoundedText(source.TimingPhase, 128, nameof(SandboxSpec.TimingPhase));
        var mounts = SnapshotList(
            source.Mounts,
            IncusMountStaging.MaximumMounts,
            nameof(SandboxSpec.Mounts),
            static mount => mount is null
                ? throw new ArgumentException("Incus sandbox mounts cannot contain null entries.", nameof(SandboxSpec.Mounts))
                : mount with { });
        var environment = SnapshotDictionary(
            source.Environment,
            IncusSandbox.MaxExecEnvironmentEntries,
            nameof(SandboxSpec.Environment),
            maximumKeyCharacters: IncusSandbox.MaxExecEnvironmentNameCharacters,
            StringComparer.Ordinal);
        var network = source.Network
            ?? throw new ArgumentException("Incus sandbox network policy cannot be null.", nameof(SandboxSpec.Network));
        var allowedHosts = SnapshotList(
            network.AllowedHosts,
            MaximumNetworkAllowedHosts,
            nameof(SandboxNetworkPolicy.AllowedHosts),
            static host => SnapshotNetworkHost(host));
        ValidateOptionalBoundedText(network.ProfileName, 63, nameof(SandboxNetworkPolicy.ProfileName));
        ValidateOptionalBoundedText(network.HostGitEndpoint, 4096, nameof(SandboxNetworkPolicy.HostGitEndpoint));
        var limits = source.Limits
            ?? throw new ArgumentException("Incus sandbox resource limits cannot be null.", nameof(SandboxSpec.Limits));

        return source with
        {
            Mounts = mounts,
            Environment = environment,
            Limits = limits with { },
            Network = network with { AllowedHosts = allowedHosts },
        };
    }

    internal static SandboxExec CaptureExec(SandboxExec source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var argv = SnapshotList(
            source.Argv,
            IncusSandbox.MaxExecArguments,
            nameof(SandboxExec.Argv),
            static argument => argument
                ?? throw new ArgumentException("Exec argv cannot contain null arguments.", nameof(SandboxExec.Argv)));
        var extraEnvironment = source.ExtraEnvironment is null
            ? null
            : SnapshotDictionary(
                source.ExtraEnvironment,
                IncusSandbox.MaxExecEnvironmentEntries,
                nameof(SandboxExec.ExtraEnvironment),
                maximumKeyCharacters: IncusSandbox.MaxExecEnvironmentNameCharacters,
                StringComparer.Ordinal);
        return source with
        {
            Argv = argv,
            ExtraEnvironment = extraEnvironment,
            EnvironmentVariablesToUnset = source.EnvironmentVariablesToUnset,
        };
    }

    internal static IncusSandboxOptions CaptureOptions(IncusSandboxOptions source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var networkProfiles = SnapshotDictionary(
            source.NetworkProfiles,
            IncusSandboxOptions.MaximumNetworkProfiles,
            nameof(IncusSandboxOptions.NetworkProfiles),
            maximumKeyCharacters: 63,
            StringComparer.OrdinalIgnoreCase);
        var allowedRoots = SnapshotList(
            source.AllowedHostMountRoots,
            IncusSandboxOptions.MaximumEffectiveHostPathEntries,
            nameof(IncusSandboxOptions.AllowedHostMountRoots),
            static root => root
                ?? throw new ArgumentException("Allowed host mount roots cannot contain null entries.", nameof(IncusSandboxOptions.AllowedHostMountRoots)));
        var extraRuncmd = SnapshotList(
            source.ExtraRuncmd,
            IncusSandboxOptions.MaximumExtraRuncmdCount,
            nameof(IncusSandboxOptions.ExtraRuncmd),
            static command => command
                ?? throw new ArgumentException("ExtraRuncmd cannot contain null entries.", nameof(IncusSandboxOptions.ExtraRuncmd)));
        var diskGuard = source.DiskGuard;
        var snappedDiskGuard = diskGuard is null
            ? null
            : diskGuard with
            {
                HostPaths = SnapshotList(
                    diskGuard.HostPaths,
                    IncusSandboxOptions.MaximumEffectiveHostPathEntries,
                    nameof(IncusDiskGuardOptions.HostPaths),
                    static path => path
                        ?? throw new ArgumentException("DiskGuard host paths cannot contain null entries.", nameof(IncusDiskGuardOptions.HostPaths))),
            };
        return source with
        {
            NetworkProfiles = networkProfiles,
            AllowedHostMountRoots = allowedRoots,
            ExtraRuncmd = extraRuncmd,
            DiskGuard = snappedDiskGuard,
        };
    }

    private static IReadOnlyList<TResult> SnapshotList<TSource, TResult>(
        IEnumerable<TSource> source,
        int maximumCount,
        string parameterName,
        Func<TSource, TResult> snapshot)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        var values = new List<TResult>(Math.Min(maximumCount, 16));
        foreach (var value in source)
        {
            if (values.Count >= maximumCount)
            {
                throw new ArgumentException(
                    $"{parameterName} cannot contain more than {maximumCount} entries.",
                    parameterName);
            }
            values.Add(snapshot(value));
        }
        return Array.AsReadOnly(values.ToArray());
    }

    private static IReadOnlyDictionary<string, string> SnapshotDictionary(
        IEnumerable<KeyValuePair<string, string>> source,
        int maximumCount,
        string parameterName,
        int maximumKeyCharacters,
        IEqualityComparer<string> comparer)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        ArgumentNullException.ThrowIfNull(comparer);
        var values = new Dictionary<string, string>(comparer);
        foreach (var (key, value) in source)
        {
            if (values.Count >= maximumCount)
            {
                throw new ArgumentException(
                    $"{parameterName} cannot contain more than {maximumCount} entries.",
                    parameterName);
            }
            if (key is null || value is null)
                throw new ArgumentException($"{parameterName} cannot contain null keys or values.", parameterName);
            if (key.Length > maximumKeyCharacters)
            {
                throw new ArgumentException(
                    $"{parameterName} contains a key longer than {maximumKeyCharacters} characters.",
                    parameterName);
            }
            if (!values.TryAdd(key, value))
                throw new ArgumentException($"{parameterName} cannot contain duplicate keys.", parameterName);
        }
        return new ReadOnlyDictionary<string, string>(values);
    }

    private static string SnapshotNetworkHost(string? host)
    {
        if (host is null)
            throw new ArgumentException("Sandbox network allowed hosts cannot contain null entries.", nameof(SandboxNetworkPolicy.AllowedHosts));
        ValidateRequiredBoundedText(host, 4096, nameof(SandboxNetworkPolicy.AllowedHosts));
        return host;
    }

    private static void ValidateRequiredBoundedText(string? value, int maximumBytes, string parameterName)
    {
        if (value is null)
            throw new ArgumentException($"{parameterName} cannot be null.", parameterName);
        _ = IncusInputValidation.GetBoundedUtf8ByteCount(
            value,
            maximumBytes,
            parameterName,
            parameterName);
    }

    private static void ValidateOptionalBoundedText(string? value, int maximumBytes, string parameterName)
    {
        if (value is not null)
            ValidateRequiredBoundedText(value, maximumBytes, parameterName);
    }
}
