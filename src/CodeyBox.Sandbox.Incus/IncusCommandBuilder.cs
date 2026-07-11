using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Sandbox.Incus;

internal enum IncusProvisioningPath
{
    FullLaunch,
    CowCopy,
}

internal static class IncusProvisioningDecision
{
    internal static IncusProvisioningPath Decide(
        IncusSandboxOptions options,
        SandboxSpec spec,
        bool baselineExists)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(spec);
        return options.UseBaselineImages
            && !string.IsNullOrWhiteSpace(spec.Network.ProfileName)
            && (!string.IsNullOrWhiteSpace(spec.BaselineImageRef)
                || string.IsNullOrWhiteSpace(spec.ImageReference)
                || string.Equals(spec.ImageReference, "ignored", StringComparison.Ordinal)
                || string.Equals(spec.ImageReference, options.DefaultImage, StringComparison.Ordinal))
            && baselineExists
                ? IncusProvisioningPath.CowCopy
                : IncusProvisioningPath.FullLaunch;
    }
}

internal static class IncusBaselineNaming
{
    internal const string BakeCandidatePrefix = "cb-bake-";
    private const int MaxInstanceNameLength = 63;

    internal static string DeriveBaselineName(
        IncusSandboxOptions options,
        string profileName,
        SandboxProfileFlavor flavor,
        Func<string, string?>? environmentVariableReader = null,
        CancellationToken ct = default,
        IReadOnlyList<string>? executableContentSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var hash = ComputeConfigHash(
            options,
            profileName,
            flavor,
            environmentVariableReader,
            ct,
            executableContentSha256);
        return DeriveBaselineNameFromHash(options, profileName, flavor, hash);
    }

    internal static string DeriveBaselineNameFromHash(
        IncusSandboxOptions options,
        string profileName,
        SandboxProfileFlavor flavor,
        string hash)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        if (hash.Length != 64 || !hash.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("An Incus baseline hash must be 64 lowercase hexadecimal characters.", nameof(hash));
        var profile = NormalizeNamePart(profileName);
        var flavorPart = flavor == SandboxProfileFlavor.Graphical ? "gui" : "headless";
        var prefix = NormalizeEffectivePrefix(options);
        var fixedSuffix = $"-{flavorPart}-{hash[..12]}";
        var maximumProfileLength = MaxInstanceNameLength - prefix.Length - fixedSuffix.Length;
        if (maximumProfileLength < 1)
            throw new InvalidOperationException("The Incus baseline prefix leaves no room for a profile component.");
        if (profile.Length > maximumProfileLength)
            profile = profile[..maximumProfileLength].TrimEnd('-');
        return prefix + profile + fixedSuffix;
    }

    internal static string NormalizeEffectivePrefix(IncusSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return NormalizeEffectivePrefix(options.BaselineNamePrefix);
    }

    internal static bool TryNormalizeEffectivePrefix(string? configuredPrefix, out string prefix)
    {
        prefix = string.Empty;
        if (configuredPrefix is null || configuredPrefix.Length is < 1 or > 32)
            return false;
        if (string.IsNullOrWhiteSpace(configuredPrefix)
            || configuredPrefix.Any(char.IsControl)
            || !char.IsAsciiLetterOrDigit(configuredPrefix[0])
            || configuredPrefix.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            return false;
        }

        prefix = NormalizeEffectivePrefix(configuredPrefix);
        return true;
    }

    private static string NormalizeEffectivePrefix(string configuredPrefix)
    {
        var prefix = NormalizePrefix(configuredPrefix);
        const int maximumPrefixLength = 20;
        return prefix.Length > maximumPrefixLength
            ? prefix[..maximumPrefixLength].TrimEnd('-') + "-"
            : prefix;
    }

    internal static bool OverlapsBakeCandidateNamespace(string effectivePrefix) =>
        effectivePrefix.StartsWith(BakeCandidatePrefix, StringComparison.Ordinal)
        || BakeCandidatePrefix.StartsWith(effectivePrefix, StringComparison.Ordinal);

    internal static string ComputeConfigHash(
        IncusSandboxOptions options,
        string profileName,
        SandboxProfileFlavor flavor,
        Func<string, string?>? environmentVariableReader = null,
        CancellationToken ct = default,
        IReadOnlyList<string>? executableContentSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        executableContentSha256 ??= options.ExecutableProvisions.Count == 0
            ? []
            : IncusBaselineProvisioning.FingerprintExecutables(
                options,
                environmentVariableReader
                    ?? throw new InvalidOperationException(
                        "An environment reader is required to fingerprint configured Incus executable provisions."),
                ct);
        if (executableContentSha256.Count != options.ExecutableProvisions.Count)
            throw new ArgumentException("Executable fingerprint count does not match the Incus provisioning configuration.", nameof(executableContentSha256));
        for (var i = 0; i < executableContentSha256.Count; i++)
        {
            var digest = executableContentSha256[i];
            if (digest is null
                || digest.Length != 71
                || !digest.StartsWith("sha256:", StringComparison.Ordinal)
                || !IsLowerHex(digest.AsSpan(7)))
            {
                throw new ArgumentException(
                    $"Executable fingerprint {i} must be 'sha256:' followed by 64 lowercase hexadecimal characters.",
                    nameof(executableContentSha256));
            }
        }
        var canonical = new
        {
            version = 3,
            profile = profileName,
            flavor = flavor.ToString(),
            image = options.DefaultImage,
            pool = options.StoragePoolName,
            cpus = options.BaselineCpus,
            memory = options.BaselineMemoryBytes,
            disk = options.BaselineDiskBytes,
            bridge = options.NetworkProfiles.TryGetValue(profileName, out var bridge) ? bridge : null,
            runcmd = options.ExtraRuncmd.ToArray(),
            generatedCloudInit = IncusCloudInit.Build(options, flavor),
            captureMetrics = options.CaptureResourceMetrics,
            resourceSampleInterval = options.ResourceMetricsSampleInterval,
            guestUserId = options.GuestUserId,
            guestGroupId = options.GuestGroupId,
            guestHome = options.GuestHome,
            packageCacheSeeds = options.PackageCacheSeeds.Select(static seed => new
            {
                seed.HostSourcePath,
                seed.VmDestPath,
                seed.MaxSizeMB,
            }).ToArray(),
            executableProvisions = options.ExecutableProvisions.Select((provision, index) => new
            {
                provision.HostSourcePath,
                provision.VmDestPath,
                VmSymlinks = provision.VmSymlinks.ToArray(),
                provision.Label,
                ContentSha256 = executableContentSha256[index],
            }).ToArray(),
            baselineVerificationCommands = options.BaselineVerificationCommands.Select(static command => new
            {
                command.Label,
                Argv = command.Argv.ToArray(),
                command.FailureHint,
            }).ToArray(),
            options.MaxExecutableProvisionBytes,
            options.MaxAggregateExecutableProvisionBytes,
            options.MaxPackageCacheSeedBytes,
            options.MaxAggregatePackageCacheSeedBytes,
            options.MaxPackageCacheSeedEntries,
        };
        var json = JsonSerializer.Serialize(canonical);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    private static string NormalizePrefix(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = NormalizeNamePart(value);
        return normalized.EndsWith("-", StringComparison.Ordinal) ? normalized : normalized + "-";
    }

    private static string NormalizeNamePart(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var c in value.ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')
                result.Append(c);
            else if (result.Length == 0 || result[^1] != '-')
                result.Append('-');
        }
        var normalized = result.ToString().Trim('-');
        if (normalized.Length == 0)
            throw new ArgumentException("The value does not contain an Incus-compatible name character.", nameof(value));
        return normalized;
    }
}

internal static class IncusCommandBuilder
{
    internal static IReadOnlyList<string> BuildInit(
        IncusSandboxOptions options,
        string image,
        string name,
        SandboxResourceLimits limits)
    {
        IncusInputValidation.ValidateOptionsIdentity(options);
        IncusInputValidation.ValidateOpaqueArgument(image, nameof(image), maximumLength: 4096);
        IncusInputValidation.ValidateInstanceName(name, nameof(name));
        if (limits.CpuCount is <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "CPU count must be positive.");
        if (limits.MemoryBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "Memory bytes must be positive.");
        if (limits.DiskBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "Disk bytes must be positive.");
        var result = Prefix(options, "init");
        result.Add(image);
        result.Add(name);
        result.Add("--vm");
        result.Add("--storage");
        result.Add(options.StoragePoolName);
        result.Add("--no-profiles");
        if (limits.CpuCount is { } cpus)
        {
            result.Add("--config");
            result.Add($"limits.cpu={cpus.ToString(CultureInfo.InvariantCulture)}");
        }
        if (limits.MemoryBytes is { } memory)
        {
            result.Add("--config");
            result.Add($"limits.memory={memory.ToString(CultureInfo.InvariantCulture)}B");
        }
        if (limits.DiskBytes is { } disk)
        {
            result.Add("--device");
            result.Add($"root,size={disk.ToString(CultureInfo.InvariantCulture)}B");
        }
        return result;
    }

    internal static IReadOnlyList<string> BuildCopy(
        IncusSandboxOptions options,
        string source,
        string destination)
    {
        IncusInputValidation.ValidateOptionsIdentity(options);
        IncusInputValidation.ValidateSnapshotSource(source, nameof(source));
        IncusInputValidation.ValidateInstanceName(destination, nameof(destination));
        var result = Prefix(options, "copy");
        result.Add(source);
        result.Add(destination);
        result.Add("--storage");
        result.Add(options.StoragePoolName);
        result.Add("--no-profiles");
        return result;
    }

    internal static IReadOnlyList<string> BuildDeviceAdd(
        IncusSandboxOptions options,
        string instance,
        string deviceName,
        string hostSource,
        string guestPath,
        bool readOnly)
    {
        IncusInputValidation.ValidateOptionsIdentity(options);
        IncusInputValidation.ValidateInstanceName(instance, nameof(instance));
        IncusInputValidation.ValidateDeviceName(deviceName, nameof(deviceName));
        IncusInputValidation.ValidateAbsoluteHostPath(hostSource, nameof(hostSource));
        IncusInputValidation.ValidateAbsoluteGuestPath(guestPath, nameof(guestPath));
        var result = Prefix(options, "config", "device", "add", instance, deviceName, "disk");
        result.Add($"source={hostSource}");
        result.Add($"path={guestPath}");
        result.Add("io.bus=virtiofs");
        if (readOnly)
            result.Add("readonly=true");
        return result;
    }

    internal static IReadOnlyList<string> BuildNicAdd(
        IncusSandboxOptions options,
        string instance,
        string bridge)
    {
        IncusInputValidation.ValidateOptionsIdentity(options);
        IncusInputValidation.ValidateInstanceName(instance, nameof(instance));
        IncusInputValidation.ValidateBridgeName(bridge, nameof(bridge));
        var result = Prefix(options, "config", "device", "add", instance, "codeybox-net", "nic");
        result.Add("nictype=bridged");
        result.Add($"parent={bridge}");
        result.Add("name=eth0");
        return result;
    }

    internal static IReadOnlyList<string> BuildExec(
        IncusSandboxOptions options,
        string instance,
        IReadOnlyList<string> command,
        string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Count == 0)
            throw new ArgumentException("An Incus exec command must not be empty.", nameof(command));
        IncusInputValidation.ValidateOptionsIdentity(options);
        IncusInputValidation.ValidateInstanceName(instance, nameof(instance));
        if (workingDirectory is not null)
            IncusInputValidation.ValidateAbsoluteGuestPath(workingDirectory, nameof(workingDirectory));
        for (var i = 0; i < command.Count; i++)
        {
            if (command[i].Contains('\0'))
                throw new ArgumentException($"Command argument {i} contains NUL.", nameof(command));
        }
        if (string.IsNullOrEmpty(command[0]))
            throw new ArgumentException("The executable argument must not be empty.", nameof(command));
        var result = Prefix(options, "exec", instance);
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            result.Add("--cwd");
            result.Add(workingDirectory);
        }
        result.Add("--user");
        result.Add(options.GuestUserId.ToString(CultureInfo.InvariantCulture));
        result.Add("--group");
        result.Add(options.GuestGroupId.ToString(CultureInfo.InvariantCulture));
        result.Add("--");
        result.AddRange(command);
        return result;
    }

    internal static List<string> Prefix(IncusSandboxOptions options, params string[] command)
    {
        ArgumentNullException.ThrowIfNull(options);
        IncusInputValidation.ValidateOptionsIdentity(options);
        var result = new List<string>(command.Length + 4)
        {
            options.BinaryPath,
            "--project",
            options.ProjectName,
        };
        result.AddRange(command);
        return result;
    }
}

internal static class IncusInputValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Counts strict UTF-8 only after a constant-time length rejection. Because
    /// every valid UTF-16 code unit contributes at least one UTF-8 byte, a
    /// character count above the byte budget can be rejected without scanning
    /// caller-owned text.
    /// </summary>
    internal static int GetBoundedUtf8ByteCount(
        string value,
        int maximumUtf8Bytes,
        string parameterName,
        string description)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (maximumUtf8Bytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumUtf8Bytes));
        if (value.Length > maximumUtf8Bytes)
        {
            throw new ArgumentException(
                $"{description} exceeds the {maximumUtf8Bytes}-byte UTF-8 safety bound.",
                parameterName);
        }
        try
        {
            var bytes = StrictUtf8.GetByteCount(value);
            if (bytes > maximumUtf8Bytes)
            {
                throw new ArgumentException(
                    $"{description} exceeds the {maximumUtf8Bytes}-byte UTF-8 safety bound.",
                    parameterName);
            }
            return bytes;
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException($"{description} is not valid Unicode.", parameterName, ex);
        }
    }

    internal static void ValidateOptionsIdentity(IncusSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOpaqueArgument(options.BinaryPath, nameof(options.BinaryPath));
        ValidateIdentifier(options.ProjectName, nameof(options.ProjectName), 63, allowDotAndUnderscore: true);
        ValidateIdentifier(options.StoragePoolName, nameof(options.StoragePoolName), 63, allowDotAndUnderscore: true);
    }

    internal static void ValidateInstanceName(string value, string parameterName) =>
        ValidateIdentifier(value, parameterName, 63, allowDotAndUnderscore: false);

    internal static void ValidateSnapshotSource(string value, string parameterName)
    {
        const string snapshotSuffix = "/ready";
        if (!value.EndsWith(snapshotSuffix, StringComparison.Ordinal))
            throw new ArgumentException("The COW source must be the provider-owned 'ready' baseline snapshot.", parameterName);
        ValidateInstanceName(value[..^snapshotSuffix.Length], parameterName);
    }

    internal static void ValidateDeviceName(string value, string parameterName) =>
        ValidateIdentifier(value, parameterName, 63, allowDotAndUnderscore: true);

    internal static void ValidateBridgeName(string value, string parameterName)
    {
        if (value is null || value.Length is < 1 or > 15)
            throw new ArgumentException("The bridge must be a valid Linux interface name of at most 15 characters.", parameterName);
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("The bridge must be a valid Linux interface name of at most 15 characters.", parameterName);
    }

    internal static void ValidateAbsoluteHostPath(string value, string parameterName)
    {
        if (value is null || value.Length is < 1 or > 4096)
            throw new ArgumentException("The host source must be a fully-qualified path without NUL.", parameterName);
        _ = GetBoundedUtf8ByteCount(value, 4096, parameterName, "Host source path");
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl)
            || !Path.IsPathFullyQualified(value))
            throw new ArgumentException("The host source must be a fully-qualified path without NUL.", parameterName);
    }

    internal static void ValidateAbsoluteGuestPath(string value, string parameterName)
    {
        if (!IncusSandboxOptions.IsAbsoluteGuestPath(value))
            throw new ArgumentException("The guest path must be a normalized absolute Unix path.", parameterName);
    }

    internal static void ValidateOpaqueArgument(
        string value,
        string parameterName,
        int maximumLength = 4096)
    {
        if (value is null || value.Length is < 1 || value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"The argument must be non-empty, at most {maximumLength} characters, must not start with '-', and must contain no control characters.",
                parameterName);
        }
        _ = GetBoundedUtf8ByteCount(value, maximumLength, parameterName, "Opaque argument");
        if (string.IsNullOrWhiteSpace(value)
            || value.StartsWith("-", StringComparison.Ordinal)
            || value.Contains('\0')
            || value.Any(c => char.IsControl(c)))
        {
            throw new ArgumentException(
                $"The argument must be non-empty, at most {maximumLength} characters, must not start with '-', and must contain no control characters.",
                parameterName);
        }
    }

    private static void ValidateIdentifier(
        string value,
        string parameterName,
        int maxLength,
        bool allowDotAndUnderscore)
    {
        if (value is null || value.Length is < 1 || value.Length > maxLength)
            throw new ArgumentException("The identifier contains unsupported characters or has an invalid length.", parameterName);
        if (string.IsNullOrWhiteSpace(value)
            || !char.IsAsciiLetterOrDigit(value[0])
            || !char.IsAsciiLetterOrDigit(value[^1])
            || value.Any(c => !(char.IsAsciiLetterOrDigit(c)
                || c == '-'
                || (allowDotAndUnderscore && c is '.' or '_'))))
            throw new ArgumentException("The identifier contains unsupported characters or has an invalid length.", parameterName);
    }
}
