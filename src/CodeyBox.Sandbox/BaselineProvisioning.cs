namespace CodeyBox.Sandbox;

/// <summary>Provider-neutral hard limits for baseline provisioning contracts.</summary>
public static class BaselineProvisioningLimits
{
    /// <summary>Maximum package-cache seed entries accepted for one provisioning operation.</summary>
    public const int MaximumPackageCacheSeeds = 32;

    /// <summary>Maximum executable provisions accepted for one provisioning operation.</summary>
    public const int MaximumExecutableProvisions = 64;

    /// <summary>Maximum symlinks accepted for one executable provision.</summary>
    public const int MaximumExecutableSymlinks = 32;

    /// <summary>Maximum strict UTF-8 bytes accepted for one provisioning config text field.</summary>
    public const int MaximumProvisioningTextUtf8Bytes = 4096;

    /// <summary>Maximum verification commands accepted for one baseline.</summary>
    public const int MaximumVerificationCommands = 64;

    /// <summary>Maximum argv entries accepted for one verification command.</summary>
    public const int MaximumVerificationArguments = 64;

    /// <summary>Maximum strict UTF-8 bytes accepted for one verification text field.</summary>
    public const int MaximumVerificationTextUtf8Bytes = MaximumProvisioningTextUtf8Bytes;

    /// <summary>Maximum strict UTF-8 bytes across all verification commands.</summary>
    public const int MaximumAggregateVerificationTextUtf8Bytes = 256 * 1024;
}

/// <summary>
/// Host package-cache contents copied during provider provisioning.
/// <see cref="HostSourcePath"/> may identify either a file or a directory.
/// </summary>
public sealed record BaselinePackageCacheSeed
{
    /// <summary>Host path to a cache file or directory. Tilde expansion is provider-defined.</summary>
    public string HostSourcePath { get; init; } = string.Empty;

    /// <summary>
    /// Absolute guest destination directory. Directory contents are copied
    /// beneath this path; a file source is copied as
    /// <c>VmDestPath/&lt;source basename&gt;</c>.
    /// </summary>
    public string VmDestPath { get; init; } = string.Empty;

    /// <summary>
    /// Optional provider-enforced content-byte cap in MiB (1,048,576 bytes).
    /// The persisted configuration property retains its historical MB suffix.
    /// </summary>
    public double? MaxSizeMB { get; init; }
}

/// <summary>
/// A host-staged executable copied during provider provisioning to a known
/// absolute guest path with executable permissions.
/// </summary>
public sealed record BaselineExecutableProvision
{
    /// <summary>Host path to the executable file. Tilde expansion is provider-defined.</summary>
    public string HostSourcePath { get; init; } = string.Empty;

    /// <summary>
    /// Absolute guest path where the executable must be installed.
    /// </summary>
    public string VmDestPath { get; init; } = string.Empty;

    /// <summary>
    /// Optional absolute guest paths at which to create symlinks pointing to
    /// <see cref="VmDestPath"/>.
    /// </summary>
    public IReadOnlyList<string> VmSymlinks { get; init; } = [];

    /// <summary>
    /// Optional diagnostic label used in log lines and bake-failure messages.
    /// Providers may derive a label from <see cref="VmDestPath"/> when unset.
    /// </summary>
    public string? Label { get; init; }
}

/// <summary>
/// A one-shot guest command that must pass before a freshly baked baseline is
/// published for cloning. <see cref="Label"/> supplies human-readable
/// diagnostic context; providers may include the complete command contract in
/// their content-addressed baseline identity.
/// </summary>
public sealed record BaselineVerificationCommand(
    string Label,
    IReadOnlyList<string> Argv,
    string? FailureHint = null);
