using CodeyBox.Sandbox;

namespace CodeyBox.OpenStackSandboxPlugin;

/// <summary>
/// Names and tags for OpenStack baseline Glance images. Every baked image is
/// addressed by the shared toolchain hash (<see cref="BaselineContentHash"/>):
/// the image name embeds the short hash and the image carries a matching
/// Glance tag, so a pin from any provider resolves to its equivalent local
/// image by hash — never by a provider-specific name.
/// </summary>
public static class OpenStackBaselineNaming
{
    /// <summary>Glance tag prefix recording an image's toolchain hash.</summary>
    public const string ToolchainTagPrefix = "codeybox-tc-";

    /// <summary>Marker embedded in baked image names before the short hash.</summary>
    public const string ImageNameHashMarker = "tc-";

    /// <summary>Derives the baked image name for a toolchain hash (full or short).</summary>
    public static string DeriveImageName(string prefix, string toolchainHash)
    {
        if (string.IsNullOrWhiteSpace(prefix) || !prefix.StartsWith("codeybox-", StringComparison.Ordinal))
            throw new ArgumentException("A baseline image prefix must start with 'codeybox-'.", nameof(prefix));
        return prefix + ImageNameHashMarker + ShortHash(toolchainHash);
    }

    /// <summary>Derives the Glance tag recording an image's toolchain hash.</summary>
    public static string ToolchainTag(string toolchainHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolchainHash);
        return ToolchainTagPrefix + ShortHash(toolchainHash);
    }

    /// <summary>
    /// Extracts the short toolchain hash from a baked image name, or null when
    /// the name was not produced by <see cref="DeriveImageName"/>.
    /// </summary>
    public static string? TryExtractHashFromName(string? imageName, string prefix)
    {
        if (string.IsNullOrWhiteSpace(imageName) || string.IsNullOrWhiteSpace(prefix))
            return null;
        var name = imageName.Trim();
        var marker = prefix + ImageNameHashMarker;
        if (!name.StartsWith(marker, StringComparison.Ordinal))
            return null;
        var hash = name[marker.Length..];
        return hash.Length == BaselineContentHash.ShortHashChars && BaselineContentHash.IsLowerHex(hash)
            ? hash
            : null;
    }

    /// <summary>
    /// Extracts the short toolchain hash from a Glance tag list, or null when
    /// no toolchain tag is present.
    /// </summary>
    public static string? TryExtractHashFromTags(IEnumerable<string>? tags)
    {
        if (tags is null)
            return null;
        foreach (var tag in tags)
        {
            if (tag is not null
                && tag.StartsWith(ToolchainTagPrefix, StringComparison.Ordinal)
                && tag.Length == ToolchainTagPrefix.Length + BaselineContentHash.ShortHashChars
                && BaselineContentHash.IsLowerHex(tag[ToolchainTagPrefix.Length..]))
            {
                return tag[ToolchainTagPrefix.Length..];
            }
        }
        return null;
    }

    /// <summary>Formats the persisted provider-scoped pin for a baked image.</summary>
    public static string FormatScopedPin(string toolchainHash, string imageName) =>
        BaselinePin.FormatScopedPin(OpenStackSandboxOptions.ProviderKind, toolchainHash, imageName);

    /// <summary>True when the image name belongs to this provider's baseline namespace.</summary>
    public static bool IsOwnedImageName(string? imageName, string prefix)
    {
        if (string.IsNullOrWhiteSpace(imageName) || string.IsNullOrWhiteSpace(prefix))
            return false;
        return imageName.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string ShortHash(string toolchainHash)
    {
        if (toolchainHash.Length == BaselineContentHash.HashHexChars
            && BaselineContentHash.IsLowerHex(toolchainHash))
        {
            return toolchainHash[..BaselineContentHash.ShortHashChars];
        }
        if (toolchainHash.Length == BaselineContentHash.ShortHashChars
            && BaselineContentHash.IsLowerHex(toolchainHash))
        {
            return toolchainHash;
        }

        throw new ArgumentException(
            "A baseline toolchain hash must be 12 or 64 lowercase hexadecimal characters.",
            nameof(toolchainHash));
    }
}
