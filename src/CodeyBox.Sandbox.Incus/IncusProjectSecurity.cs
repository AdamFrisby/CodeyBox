using System.Text.Json;

namespace CodeyBox.Sandbox.Incus;

internal sealed record IncusProjectSecuritySnapshot(
    string Name,
    IReadOnlyDictionary<string, string> Config);

/// <summary>
/// Builds and verifies the dedicated Incus project's host-path confinement.
/// Incus applies <c>restricted.devices.disk.paths</c> at the daemon-side disk
/// sink and opens a matched source beneath its allowed parent with openat2.
/// The provider's own mount allowlist remains the narrower policy layer.
/// </summary>
internal static class IncusProjectSecurity
{
    internal const string FeaturesImagesKey = "features.images";
    internal const string FeaturesProfilesKey = "features.profiles";
    internal const string ManagedKey = "user.codeybox.managed";
    internal const string SchemaKey = "user.codeybox.project-schema";
    internal const string RestrictedKey = "restricted";
    internal const string RestrictedDiskKey = "restricted.devices.disk";
    internal const string RestrictedDiskPathsKey = "restricted.devices.disk.paths";
    internal const string RestrictedNicKey = "restricted.devices.nic";
    internal const string RestrictedSnapshotsKey = "restricted.snapshots";
    internal const string RestrictedVmLowLevelKey = "restricted.virtual-machines.lowlevel";

    private const int MaximumRoots = 65;
    private const int MaximumEncodedRootsBytes = 256 * 1024;

    internal static IReadOnlyList<string> ResolveRequiredRoots(
        IncusSandboxOptions options,
        string stagingRoot)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);

        var roots = new List<string>(options.AllowedHostMountRoots.Count + 1);
        foreach (var configured in options.AllowedHostMountRoots.Append(stagingRoot))
        {
            ValidateRootText(configured);
            roots.Add(ResolvePotentialCanonicalPath(configured));
        }

        return NormalizeCanonicalRoots(roots);
    }

    internal static IReadOnlyList<string> NormalizeCanonicalRoots(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var normalized = new SortedSet<string>(StringComparer.Ordinal);
        var encodedBytes = 0L;
        foreach (var root in roots)
        {
            ValidateRootText(root);
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (!string.Equals(root, full, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Incus restricted-project disk roots must be exact canonical paths without trailing separators.");
            }

            var filesystemRoot = Path.GetPathRoot(full);
            if (filesystemRoot is not null
                && string.Equals(
                    full,
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(filesystemRoot)),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The host filesystem root cannot be an Incus restricted-project disk path.");
            }

            if (!normalized.Add(full))
                continue;
            encodedBytes += IncusInputValidation.GetBoundedUtf8ByteCount(
                full,
                4096,
                nameof(roots),
                "Incus restricted-project disk root");
            if (normalized.Count > MaximumRoots || encodedBytes > MaximumEncodedRootsBytes)
            {
                throw new InvalidOperationException(
                    "Incus restricted-project disk roots exceed their configured count or aggregate size bound.");
            }
        }

        if (normalized.Count == 0)
        {
            throw new InvalidOperationException(
                "Incus restricted-project disk paths must never be empty because Incus treats an empty value as unrestricted.");
        }

        return Array.AsReadOnly(normalized.ToArray());
    }

    internal static IReadOnlyList<string> BuildCreateArguments(
        IncusSandboxOptions options,
        IReadOnlyList<string> requiredRoots)
    {
        IncusInputValidation.ValidateOptionsIdentity(options);
        var encodedRoots = EncodeRoots(requiredRoots);
        return
        [
            options.BinaryPath,
            "project", "create", options.ProjectName,
            "--config", $"{FeaturesImagesKey}=false",
            // Incus requires restricted projects to own their profiles. Every
            // CodeyBox VM still uses --no-profiles and an exact topology check.
            "--config", $"{FeaturesProfilesKey}=true",
            "--config", $"{ManagedKey}=true",
            "--config", $"{SchemaKey}=1",
            "--config", $"{RestrictedKey}=true",
            "--config", $"{RestrictedDiskKey}=allow",
            "--config", $"{RestrictedDiskPathsKey}={encodedRoots}",
            "--config", $"{RestrictedNicKey}=allow",
            "--config", $"{RestrictedSnapshotsKey}=allow",
            "--config", $"{RestrictedVmLowLevelKey}=block",
        ];
    }

    internal static IReadOnlyList<string> BuildSetArguments(
        IncusSandboxOptions options,
        IReadOnlyList<string> requiredRoots)
    {
        IncusInputValidation.ValidateOptionsIdentity(options);
        var encodedRoots = EncodeRoots(requiredRoots);
        return
        [
            options.BinaryPath,
            "project", "set", options.ProjectName,
            $"{RestrictedKey}=true",
            $"{RestrictedDiskKey}=allow",
            $"{RestrictedDiskPathsKey}={encodedRoots}",
            $"{RestrictedNicKey}=allow",
            $"{RestrictedSnapshotsKey}=allow",
            $"{RestrictedVmLowLevelKey}=block",
            $"{ManagedKey}=true",
            $"{SchemaKey}=1",
        ];
    }

    internal static IncusProjectSecuritySnapshot ParseProjectQuery(
        string json,
        string expectedProjectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProjectName);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        var project = root.TryGetProperty("metadata", out var metadata) ? metadata : root;
        if (project.ValueKind != JsonValueKind.Object
            || !project.TryGetProperty("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String
            || !string.Equals(nameElement.GetString(), expectedProjectName, StringComparison.Ordinal)
            || !project.TryGetProperty("config", out var configElement)
            || configElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Incus returned a malformed or mismatched dedicated-project response.");
        }

        var config = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in configElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String
                || !config.TryAdd(property.Name, property.Value.GetString() ?? string.Empty))
            {
                throw new InvalidOperationException(
                    "Incus returned ambiguous non-string dedicated-project configuration.");
            }
        }

        return new IncusProjectSecuritySnapshot(expectedProjectName, config);
    }

    internal static void EnsureDedicatedShape(IncusProjectSecuritySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!HasExactValue(snapshot.Config, ManagedKey, "true")
            || !HasExactValue(snapshot.Config, SchemaKey, "1")
            || !HasExactValue(snapshot.Config, FeaturesImagesKey, "false")
            || !HasExactValue(snapshot.Config, FeaturesProfilesKey, "true"))
        {
            throw new InvalidOperationException(
                "Refusing to mutate an Incus project without the exact CodeyBox ownership/schema marker and dedicated feature flags.");
        }
    }

    internal static bool IsCompliant(
        IncusProjectSecuritySnapshot snapshot,
        IReadOnlyList<string> requiredRoots)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(requiredRoots);
        if (!HasExactValue(snapshot.Config, ManagedKey, "true")
            || !HasExactValue(snapshot.Config, SchemaKey, "1")
            || !HasExactValue(snapshot.Config, FeaturesImagesKey, "false")
            || !HasExactValue(snapshot.Config, FeaturesProfilesKey, "true")
            || !HasExactValue(snapshot.Config, RestrictedKey, "true")
            || !HasExactValue(snapshot.Config, RestrictedDiskKey, "allow")
            || !HasExactValue(snapshot.Config, RestrictedNicKey, "allow")
            || !HasExactValue(snapshot.Config, RestrictedSnapshotsKey, "allow")
            || !HasExactValue(snapshot.Config, RestrictedVmLowLevelKey, "block")
            || !snapshot.Config.TryGetValue(RestrictedDiskPathsKey, out var configuredPaths)
            || string.IsNullOrEmpty(configuredPaths))
        {
            return false;
        }

        try
        {
            var configuredRoots = NormalizeCanonicalRoots(
                configuredPaths.Split(',', StringSplitOptions.None));
            var normalizedRequired = NormalizeCanonicalRoots(requiredRoots);
            return configuredRoots.SequenceEqual(normalizedRequired, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    internal static void EnsureCompliant(
        IncusProjectSecuritySnapshot snapshot,
        IReadOnlyList<string> requiredRoots)
    {
        if (!IsCompliant(snapshot, requiredRoots))
        {
            throw new InvalidOperationException(
                "Incus dedicated-project confinement did not match the required restricted disk paths after configuration read-back.");
        }
    }

    internal static void EnsureServerCapabilities(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        var server = root.TryGetProperty("metadata", out var metadata) ? metadata : root;
        if (server.ValueKind != JsonValueKind.Object
            || !server.TryGetProperty("api_extensions", out var extensions)
            || extensions.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Incus server capability response is malformed.");
        }

        var extensionNames = extensions
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .ToHashSet(StringComparer.Ordinal);
        if (!extensionNames.Contains("disk_io_bus_cache_filesystem"))
            throw new InvalidOperationException("Incus lacks the filesystem io.bus API extension required to force virtiofs.");
        if (!extensionNames.Contains("projects_restrictions"))
            throw new InvalidOperationException("Incus lacks restricted-project support required for daemon-side disk path confinement.");

        if (!server.TryGetProperty("environment", out var environment)
            || environment.ValueKind != JsonValueKind.Object
            || !environment.TryGetProperty("kernel_version", out var kernelVersion)
            || kernelVersion.ValueKind != JsonValueKind.String
            || !KernelSupportsOpenAt2(kernelVersion.GetString()))
        {
            throw new InvalidOperationException(
                "The Incus daemon host must report Linux kernel 5.6 or newer for openat2-backed restricted disk paths.");
        }
    }

    internal static bool KernelSupportsOpenAt2(string? kernelVersion)
    {
        if (string.IsNullOrWhiteSpace(kernelVersion))
            return false;
        var span = kernelVersion.AsSpan();
        var separator = span.IndexOf('.');
        if (separator <= 0
            || !int.TryParse(span[..separator], out var major))
        {
            return false;
        }

        span = span[(separator + 1)..];
        separator = span.IndexOfAny('.', '-');
        var minorSpan = separator < 0 ? span : span[..separator];
        if (minorSpan.Length == 0 || !int.TryParse(minorSpan, out var minor))
            return false;
        return major > 5 || (major == 5 && minor >= 6);
    }

    private static string EncodeRoots(IReadOnlyList<string> roots) =>
        string.Join(',', NormalizeCanonicalRoots(roots));

    private static string ResolvePotentialCanonicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full) || File.Exists(full))
            return IncusMountStaging.ResolveExistingRealPath(full);

        var missing = new Stack<string>();
        var current = full;
        while (!Directory.Exists(current) && !File.Exists(current))
        {
            var name = Path.GetFileName(current);
            if (string.IsNullOrEmpty(name))
                throw new FileNotFoundException("No existing ancestor can anchor an Incus restricted-project path.", full);
            missing.Push(name);
            current = Path.GetDirectoryName(current)
                ?? throw new FileNotFoundException("No existing ancestor can anchor an Incus restricted-project path.", full);
        }

        current = IncusMountStaging.ResolveExistingRealPath(current);
        while (missing.TryPop(out var segment))
            current = Path.Combine(current, segment);
        return Path.GetFullPath(current);
    }

    private static void ValidateRootText(string root)
    {
        if (root is null || root.Length is < 1 or > 4096)
            throw new ArgumentException(
                "Incus restricted-project disk roots must be bounded absolute paths without commas or control characters.",
                nameof(root));
        _ = IncusInputValidation.GetBoundedUtf8ByteCount(
            root,
            4096,
            nameof(root),
            "Incus restricted-project disk root");
        if (string.IsNullOrWhiteSpace(root)
            || root.Contains(',')
            || root.Any(char.IsControl)
            || !Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException(
                "Incus restricted-project disk roots must be bounded absolute paths without commas or control characters.",
                nameof(root));
        }
    }

    private static bool HasExactValue(
        IReadOnlyDictionary<string, string> config,
        string key,
        string expected) =>
        config.TryGetValue(key, out var actual)
        && string.Equals(actual, expected, StringComparison.Ordinal);
}
