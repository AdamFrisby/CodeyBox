namespace CodeyBox.Sandbox.Incus;

/// <summary>Canonical relationship checks for already-validated absolute guest paths.</summary>
internal static class IncusGuestPaths
{
    private static readonly string[] VolatileOrPseudoFilesystemRoots =
        ["/dev", "/proc", "/run", "/sys"];

    internal static bool IsDescendant(string candidate, string parent) =>
        candidate.Length > parent.Length
        && candidate.StartsWith(parent, StringComparison.Ordinal)
        && (parent == "/" || candidate[parent.Length] == '/');

    internal static bool Overlap(string first, string second) =>
        string.Equals(first, second, StringComparison.Ordinal)
        || IsDescendant(first, second)
        || IsDescendant(second, first);

    internal static bool IsVolatileOrPseudoFilesystemPath(string path) =>
        VolatileOrPseudoFilesystemRoots.Any(root =>
            string.Equals(path, root, StringComparison.Ordinal)
            || IsDescendant(path, root));
}
