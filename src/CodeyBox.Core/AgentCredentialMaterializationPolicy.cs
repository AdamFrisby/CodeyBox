using System.Collections.ObjectModel;
using System.Text;

namespace CodeyBox.Core;

/// <summary>
/// Defines the bounded, provider-neutral contract for credential material that
/// can cross into a sandbox. Credential bundles are snapshotted through this
/// policy so a provider cannot mutate them after validation and before a
/// subprocess or filesystem sink consumes them.
/// </summary>
public static class AgentCredentialMaterializationPolicy
{
    /// <summary>Maximum bytes available to one sandbox credential tmpfs.</summary>
    public const long MaterializationBudgetBytes = 4L * 1024 * 1024;

    /// <summary>Conservative tmpfs allocation granularity for file payloads.</summary>
    public const int MaterializationPageBytes = 4096;

    /// <summary>Maximum number of credential files in one bundle.</summary>
    public const int MaximumFiles = 64;

    /// <summary>Maximum number of credential environment variables in one bundle.</summary>
    public const int MaximumEnvironmentVariables = 64;

    /// <summary>Maximum number of credential adjunct mounts in one bundle.</summary>
    public const int MaximumMounts = 64;

    /// <summary>Maximum UTF-8 bytes in one sandbox or host path.</summary>
    public const int MaximumPathUtf8Bytes = 4096;

    /// <summary>Maximum UTF-8 bytes in one Unix path component.</summary>
    public const int MaximumPathSegmentUtf8Bytes = 255;

    /// <summary>Maximum components in one credential file path.</summary>
    public const int MaximumPathSegments = 256;

    /// <summary>Maximum characters in one POSIX environment-variable name.</summary>
    public const int MaximumEnvironmentVariableNameLength = SandboxEnvironmentVariableName.MaximumLength;

    private const long MaximumMountPathBytes = 256L * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Validates that <paramref name="value"/> is already in canonical relative
    /// Unix form and returns it unchanged. Canonical paths contain no empty,
    /// traversal, backslash, oversized, or control-character components.
    /// </summary>
    public static string ValidateRelativeFilePath(string value, string parameterName)
    {
        ValidatePathText(value, parameterName);
        if (value.StartsWith("/", StringComparison.Ordinal)
            || value.EndsWith("/", StringComparison.Ordinal)
            || value.Contains("//", StringComparison.Ordinal)
            || value.Contains('\\'))
        {
            throw new ArgumentException(
                "Credential file paths must be canonical relative Unix paths.",
                parameterName);
        }

        ValidateSegments(value.Split('/', StringSplitOptions.None), parameterName);
        return value;
    }

    /// <summary>
    /// Validates the optional destination override accepted by the in-sandbox
    /// writer. The result remains opaque until the writer resolves it beneath
    /// the selected allowlisted root.
    /// </summary>
    public static string ValidateDestinationOverride(string? value, string parameterName)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        ValidatePathText(value, parameterName);
        if (value.Contains('\\'))
            throw new ArgumentException("Credential destination overrides must use Unix separators.", parameterName);

        if (value.StartsWith("$HOME/", StringComparison.Ordinal))
        {
            _ = ValidateRelativeFilePath(value[6..], parameterName);
            return value;
        }
        if (value.StartsWith("~/", StringComparison.Ordinal))
        {
            _ = ValidateRelativeFilePath(value[2..], parameterName);
            return value;
        }
        if (!value.StartsWith("/", StringComparison.Ordinal))
            return ValidateRelativeFilePath(value, parameterName);

        if (value == "/"
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.EndsWith("/", StringComparison.Ordinal)
            || value.Contains("//", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Absolute credential destination overrides must be canonical file paths.",
                parameterName);
        }

        ValidateSegments(value[1..].Split('/', StringSplitOptions.None), parameterName);
        return value;
    }

    /// <summary>Validates a bounded POSIX environment-variable identifier.</summary>
    public static void ValidateEnvironmentVariableName(string value, string parameterName) =>
        SandboxEnvironmentVariableName.Validate(value, parameterName);

    /// <summary>Validates one bounded UTF-8 credential payload.</summary>
    public static int ValidatePayload(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        long bytes = 0;
        AddWithinBudget(value, ref bytes, parameterName, "Credential payload");
        return checked((int)bytes);
    }

    /// <summary>
    /// Returns the conservative tmpfs data allocation for one validated
    /// payload, rounded to the next filesystem page.
    /// </summary>
    public static long GetPayloadAllocationBytes(string value, string parameterName)
    {
        var bytes = ValidatePayload(value, parameterName);
        if (bytes == 0)
            return 0;
        return ((long)bytes + MaterializationPageBytes - 1) / MaterializationPageBytes
            * MaterializationPageBytes;
    }

    internal static void SnapshotBundle(
        IReadOnlyDictionary<string, string> environmentVariables,
        IReadOnlyDictionary<string, string> files,
        out IReadOnlyDictionary<string, string> environmentSnapshot,
        out IReadOnlyDictionary<string, string> fileSnapshot)
    {
        ArgumentNullException.ThrowIfNull(environmentVariables);
        ArgumentNullException.ThrowIfNull(files);
        var environmentResult = new Dictionary<string, string>(StringComparer.Ordinal);
        var fileResult = new Dictionary<string, string>(StringComparer.Ordinal);
        var canonicalPaths = new HashSet<string>(StringComparer.Ordinal);
        long aggregateBytes = 0;
        var entriesSeen = 0;
        foreach (var pair in environmentVariables)
        {
            if (++entriesSeen > MaximumEnvironmentVariables)
            {
                throw new ArgumentException(
                    $"Credential environment cannot contain more than {MaximumEnvironmentVariables} entries.",
                    nameof(environmentVariables));
            }
            if (pair.Key is null || pair.Value is null)
                throw new ArgumentException("Credential environment cannot contain null keys or values.", nameof(environmentVariables));
            ValidateEnvironmentVariableName(pair.Key, nameof(environmentVariables));
            AddWithinBudget(pair.Key, ref aggregateBytes, nameof(environmentVariables), "Credential bundle");
            AddPayloadWithinBudget(pair.Value, ref aggregateBytes, nameof(environmentVariables));
            if (pair.Value.Contains('\0'))
                throw new ArgumentException("Credential environment values cannot contain NUL.", nameof(environmentVariables));
            if (!environmentResult.TryAdd(pair.Key, pair.Value))
                throw new ArgumentException("Credential environment contains duplicate names.", nameof(environmentVariables));
        }

        entriesSeen = 0;
        foreach (var pair in files)
        {
            if (++entriesSeen > MaximumFiles)
            {
                throw new ArgumentException(
                    $"Credential bundles cannot contain more than {MaximumFiles} files.",
                    nameof(files));
            }
            if (pair.Key is null || pair.Value is null)
                throw new ArgumentException("Credential files cannot contain null paths or payloads.", nameof(files));
            var canonicalPath = ValidateRelativeFilePath(pair.Key, nameof(files));
            if (!canonicalPaths.Add(canonicalPath) || !fileResult.TryAdd(canonicalPath, pair.Value))
                throw new ArgumentException("Credential files contain duplicate canonical paths.", nameof(files));
            AddWithinBudget(canonicalPath, ref aggregateBytes, nameof(files), "Credential bundle");
            AddPayloadWithinBudget(pair.Value, ref aggregateBytes, nameof(files));
        }

        if (environmentResult.Count > MaximumFiles - fileResult.Count)
        {
            throw new ArgumentException(
                $"A credential bundle cannot contain more than {MaximumFiles} environment and file entries in aggregate.",
                nameof(environmentVariables));
        }

        environmentSnapshot = new ReadOnlyDictionary<string, string>(environmentResult);
        fileSnapshot = new ReadOnlyDictionary<string, string>(fileResult);
    }

    internal static IReadOnlyList<SandboxMount> SnapshotMounts(
        IReadOnlyList<SandboxMount> source,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        var result = new List<SandboxMount>();
        var sandboxPaths = new HashSet<string>(StringComparer.Ordinal);
        long aggregatePathBytes = 0;
        var entriesSeen = 0;
        foreach (var mount in source)
        {
            if (++entriesSeen > MaximumMounts)
            {
                throw new ArgumentException(
                    $"Credential bundles cannot contain more than {MaximumMounts} mounts.",
                    parameterName);
            }
            if (mount is null)
                throw new ArgumentException("Credential mounts cannot contain null entries.", parameterName);
            if (mount.HostPath is not { Length: > 0 } hostPath)
            {
                throw new ArgumentException(
                    "Credential adjunct mounts must name a host source.",
                    parameterName);
            }
            if (!mount.ReadOnly || mount.Tmpfs || mount.SizeBytes is not null)
            {
                throw new ArgumentException(
                    "Credential adjunct mounts must be read-only host-backed mounts without tmpfs sizing.",
                    parameterName);
            }
            ValidateAbsoluteSandboxPath(mount.SandboxPath, parameterName);
            ValidateCanonicalHostPath(hostPath, parameterName);
            if (!sandboxPaths.Add(mount.SandboxPath))
                throw new ArgumentException("Credential mounts contain duplicate sandbox destinations.", parameterName);
            AddBoundedPath(mount.SandboxPath, ref aggregatePathBytes, parameterName);
            AddBoundedPath(hostPath, ref aggregatePathBytes, parameterName);
            result.Add(mount);
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static void ValidatePathText(string value, string parameterName)
    {
        if (value is null || value.Length == 0)
            throw new ArgumentException("Credential paths must be non-empty.", parameterName);
        if (value.Length > MaximumPathUtf8Bytes)
        {
            throw new ArgumentException(
                $"Credential paths must be valid UTF-8 without control characters and at most {MaximumPathUtf8Bytes} bytes.",
                parameterName);
        }
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(IsUnsafePathCharacter)
            || GetUtf8ByteCount(value, parameterName) > MaximumPathUtf8Bytes)
        {
            throw new ArgumentException(
                $"Credential paths must be valid UTF-8 without control characters and at most {MaximumPathUtf8Bytes} bytes.",
                parameterName);
        }
    }

    private static void ValidateSegments(IReadOnlyList<string> segments, string parameterName)
    {
        if (segments.Count is < 1 or > MaximumPathSegments)
        {
            throw new ArgumentException(
                $"Credential paths cannot contain more than {MaximumPathSegments} components.",
                parameterName);
        }
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
                throw new ArgumentException("Credential paths cannot contain empty or traversal components.", parameterName);
            if (GetUtf8ByteCount(segment, parameterName) > MaximumPathSegmentUtf8Bytes)
            {
                throw new ArgumentException(
                    $"Credential path components cannot exceed {MaximumPathSegmentUtf8Bytes} UTF-8 bytes.",
                    parameterName);
            }
        }
    }

    private static void AddBoundedPath(
        string value,
        ref long aggregateBytes,
        string parameterName)
    {
        if (value is null || value.Length == 0 || value.Length > MaximumPathUtf8Bytes)
        {
            throw new ArgumentException("Credential mount paths must be non-empty, bounded, and contain no control characters.", parameterName);
        }
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(IsUnsafePathCharacter))
        {
            throw new ArgumentException("Credential mount paths must be non-empty, bounded, and contain no control characters.", parameterName);
        }
        var bytes = GetUtf8ByteCount(value, parameterName);
        if (bytes > MaximumPathUtf8Bytes || bytes > MaximumMountPathBytes - aggregateBytes)
            throw new ArgumentException("Credential mount paths exceed their aggregate size bound.", parameterName);
        aggregateBytes += bytes;
    }

    private static void ValidateAbsoluteSandboxPath(string value, string parameterName)
    {
        ValidatePathText(value, parameterName);
        if (value == "/"
            || !value.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.EndsWith("/", StringComparison.Ordinal)
            || value.Contains("//", StringComparison.Ordinal)
            || value.Contains('\\'))
        {
            throw new ArgumentException(
                "Credential mount sandbox destinations must be canonical absolute Unix paths below root.",
                parameterName);
        }
        ValidateSegments(value[1..].Split('/', StringSplitOptions.None), parameterName);
    }

    private static void ValidateCanonicalHostPath(string value, string parameterName)
    {
        ValidatePathText(value, parameterName);
        string canonical;
        try
        {
            canonical = Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("Credential mount host sources must be valid canonical absolute paths.", parameterName, ex);
        }
        var root = Path.GetPathRoot(canonical);
        if (!Path.IsPathFullyQualified(value)
            || !string.Equals(canonical, value, StringComparison.Ordinal)
            || Path.EndsInDirectorySeparator(value)
            || root is null
            || string.Equals(
                Path.TrimEndingDirectorySeparator(canonical),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Credential mount host sources must be canonical absolute non-root paths.",
                parameterName);
        }
    }

    private static void AddWithinBudget(
        string value,
        ref long aggregateBytes,
        string parameterName,
        string description)
    {
        if (value.Length > MaterializationBudgetBytes)
            throw new ArgumentException($"{description} exceeds the sandbox credential budget.", parameterName);
        var bytes = GetUtf8ByteCount(value, parameterName);
        if (bytes > MaterializationBudgetBytes - aggregateBytes)
            throw new ArgumentException($"{description} exceeds the sandbox credential budget.", parameterName);
        aggregateBytes += bytes;
    }

    private static void AddPayloadWithinBudget(
        string value,
        ref long aggregateBytes,
        string parameterName)
    {
        var bytes = GetPayloadAllocationBytes(value, parameterName);
        if (bytes > MaterializationBudgetBytes - aggregateBytes)
            throw new ArgumentException("Credential bundle exceeds the sandbox credential budget.", parameterName);
        aggregateBytes += bytes;
    }

    private static int GetUtf8ByteCount(string value, string parameterName)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("Credential text contains invalid Unicode.", parameterName, ex);
        }
    }

    private static bool IsUnsafePathCharacter(char value) =>
        char.IsControl(value)
        || char.IsSurrogate(value)
        || value is '\u0085' or '\u2028' or '\u2029';

}
