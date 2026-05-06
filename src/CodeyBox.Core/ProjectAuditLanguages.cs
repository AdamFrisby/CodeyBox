namespace CodeyBox.Core;

public static class ProjectAuditLanguages
{
    public static readonly IReadOnlySet<string> Supported =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "csharp",
            "python",
            "node",
            "go",
            "rust",
        };

    public static bool IsSupported(string language)
        => Supported.Contains(language);
}
