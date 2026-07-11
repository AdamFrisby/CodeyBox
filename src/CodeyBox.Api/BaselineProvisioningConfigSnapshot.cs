using System.Text;
using CodeyBox.Sandbox;

namespace CodeyBox.Api;

/// <summary>
/// Converts mutable configuration shapes into bounded provider-neutral
/// provisioning contracts without trusting collection <c>Count</c> values.
/// </summary>
internal static class BaselineProvisioningConfigSnapshot
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static IReadOnlyList<BaselinePackageCacheSeed> SnapshotPackageCacheSeeds(
        IEnumerable<PackageCacheSeedConfig>? seeds,
        string configurationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationKey);
        if (seeds is null)
            return [];

        var copy = new List<BaselinePackageCacheSeed>(
            Math.Min(BaselineProvisioningLimits.MaximumPackageCacheSeeds, 16));
        foreach (var seed in seeds)
        {
            if (copy.Count >= BaselineProvisioningLimits.MaximumPackageCacheSeeds)
            {
                throw new InvalidOperationException(
                    $"{configurationKey} cannot contain more than " +
                    $"{BaselineProvisioningLimits.MaximumPackageCacheSeeds} entries.");
            }
            if (seed is null)
                throw new InvalidOperationException($"{configurationKey} cannot contain null entries.");

            EnsureTextBound(seed.HostSourcePath, $"{configurationKey}:HostSourcePath", allowEmpty: false);
            EnsureTextBound(seed.VmDestPath, $"{configurationKey}:VmDestPath", allowEmpty: false);
            if (seed.MaxSizeMB is { } maxSizeMB
                && (!double.IsFinite(maxSizeMB) || maxSizeMB <= 0))
            {
                throw new InvalidOperationException(
                    $"{configurationKey}:MaxSizeMB must be finite and greater than zero when set.");
            }
            copy.Add(new BaselinePackageCacheSeed
            {
                HostSourcePath = seed.HostSourcePath,
                VmDestPath = seed.VmDestPath,
                MaxSizeMB = seed.MaxSizeMB,
            });
        }
        return Array.AsReadOnly(copy.ToArray());
    }

    internal static IReadOnlyList<BaselineExecutableProvision> SnapshotExecutableProvisions(
        IEnumerable<ExecutableProvisionConfig>? provisions,
        string configurationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationKey);
        if (provisions is null)
            return [];

        var copy = new List<BaselineExecutableProvision>(
            Math.Min(BaselineProvisioningLimits.MaximumExecutableProvisions, 16));
        foreach (var provision in provisions)
        {
            if (copy.Count >= BaselineProvisioningLimits.MaximumExecutableProvisions)
            {
                throw new InvalidOperationException(
                    $"{configurationKey} cannot contain more than " +
                    $"{BaselineProvisioningLimits.MaximumExecutableProvisions} entries.");
            }
            if (provision is null)
                throw new InvalidOperationException($"{configurationKey} cannot contain null entries.");

            EnsureTextBound(provision.HostSourcePath, $"{configurationKey}:HostSourcePath", allowEmpty: false);
            EnsureTextBound(provision.VmDestPath, $"{configurationKey}:VmDestPath", allowEmpty: false);
            if (provision.Label is not null)
                EnsureTextBound(provision.Label, $"{configurationKey}:Label", allowEmpty: true);

            var symlinks = new List<string>(
                Math.Min(BaselineProvisioningLimits.MaximumExecutableSymlinks, 8));
            foreach (var symlink in provision.VmSymlinks ?? [])
            {
                if (symlinks.Count >= BaselineProvisioningLimits.MaximumExecutableSymlinks)
                {
                    throw new InvalidOperationException(
                        $"{configurationKey}:VmSymlinks cannot contain more than " +
                        $"{BaselineProvisioningLimits.MaximumExecutableSymlinks} entries.");
                }
                EnsureTextBound(symlink, $"{configurationKey}:VmSymlinks", allowEmpty: false);
                symlinks.Add(symlink);
            }

            copy.Add(new BaselineExecutableProvision
            {
                HostSourcePath = provision.HostSourcePath,
                VmDestPath = provision.VmDestPath,
                VmSymlinks = Array.AsReadOnly(symlinks.ToArray()),
                Label = provision.Label,
            });
        }
        return Array.AsReadOnly(copy.ToArray());
    }

    private static void EnsureTextBound(string? value, string fieldName, bool allowEmpty)
    {
        ConfigurationInputBounds.EnsureCharacterBound(
            value,
            BaselineProvisioningLimits.MaximumProvisioningTextUtf8Bytes,
            fieldName);
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{fieldName} cannot be empty.");
        var text = value!;
        if (text.Any(char.IsControl))
            throw new InvalidOperationException($"{fieldName} cannot contain control characters.");

        try
        {
            if (StrictUtf8.GetByteCount(text)
                > BaselineProvisioningLimits.MaximumProvisioningTextUtf8Bytes)
            {
                throw new InvalidOperationException(
                    $"{fieldName} exceeds " +
                    $"{BaselineProvisioningLimits.MaximumProvisioningTextUtf8Bytes} UTF-8 bytes.");
            }
        }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidOperationException($"{fieldName} is not valid Unicode.", ex);
        }
    }
}
