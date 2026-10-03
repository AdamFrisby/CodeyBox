using CodeyBox.Sandbox;

namespace CodeyBox.OpenStackSandboxPlugin;

/// <summary>One baked baseline image as seen by retention selection.</summary>
public sealed record OpenStackRetainedImage(
    string Name,
    string? ToolchainHash,
    string? Project,
    DateTimeOffset? CreatedAt);

/// <summary>
/// Pure count-based retention selection for baked OpenStack baseline images:
/// keep the newest <paramref name="keepNewestPerGroup"/> images per project
/// group, plus every image pinned by a non-terminal item. An image counts as
/// pinned by exact name match OR by toolchain-hash match against any scoped
/// pin (any provider scope) — so an image serving a cross-provider pin is
/// never selected. Pure (inputs in, names out) so the policy is unit-testable
/// without a cloud.
/// </summary>
public static class OpenStackBaselineRetention
{
    /// <summary>
    /// Selects image names to delete. Never returns a pinned image, even when
    /// the history depth is exceeded; never throws on unparsable pins (they
    /// simply match nothing by hash).
    /// </summary>
    public static IReadOnlyList<string> SelectForDeletion(
        IReadOnlyList<OpenStackRetainedImage> images,
        IReadOnlySet<string> livePins,
        int keepNewestPerGroup)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(livePins);
        if (keepNewestPerGroup < 1)
            throw new ArgumentOutOfRangeException(nameof(keepNewestPerGroup), "History depth must be positive.");

        var liveHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pin in livePins)
        {
            var hash = BaselinePin.TryExtractToolchainHash(pin);
            if (hash is not null)
                liveHashes.Add(hash);
        }

        var doomed = new List<string>();
        foreach (var group in images.GroupBy(static image => image.Project ?? string.Empty))
        {
            var ordered = group
                .OrderByDescending(static image => image.CreatedAt.HasValue)
                .ThenByDescending(static image => image.CreatedAt)
                .ThenBy(static image => image.Name, StringComparer.Ordinal)
                .ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                if (i < keepNewestPerGroup)
                    continue;
                var candidate = ordered[i];
                if (livePins.Contains(candidate.Name))
                    continue;
                if (candidate.ToolchainHash is not null && liveHashes.Contains(candidate.ToolchainHash))
                    continue;
                doomed.Add(candidate.Name);
            }
        }
        return doomed;
    }
}
