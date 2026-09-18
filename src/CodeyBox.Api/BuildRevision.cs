using System.Reflection;

namespace CodeyBox.Api;

/// <summary>
/// Reads the commit revision stamped into the API assembly at build time by
/// the <c>CodeyBoxEmbedBuildRevision</c> MSBuild target (see
/// <c>Directory.Build.targets</c>). Never throws: any failure yields
/// <see cref="DeployConsistency.UnknownRevision"/> so the consistency check
/// reports unknown instead of blocking startup.
/// </summary>
internal static class BuildRevision
{
    internal const string MetadataKey = "CodeyBoxBuildRevision";

    public static string GetBuiltRevision(Assembly? assembly = null)
    {
        try
        {
            var target = assembly ?? typeof(BuildRevision).Assembly;
            var value = target
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => string.Equals(a.Key, MetadataKey, StringComparison.Ordinal))
                ?.Value;
            return DeployConsistency.NormalizeRevision(value);
        }
        catch (Exception)
        {
            // Assembly metadata must never prevent startup; unknown is a
            // first-class outcome the caller already handles.
            return DeployConsistency.UnknownRevision;
        }
    }
}
