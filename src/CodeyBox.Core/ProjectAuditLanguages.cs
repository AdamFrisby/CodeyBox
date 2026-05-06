namespace CodeyBox.Core;

public static class ProjectAuditLanguages
{
    public static readonly IReadOnlyList<string> Default = ["csharp"];

    public static readonly IReadOnlySet<string> Supported =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "csharp",
            "python",
            "node",
            "go",
            "rust",
            "typescript",
            "javascript",
        };

    public static bool IsSupported(string language)
        => Supported.Contains(language);
}
