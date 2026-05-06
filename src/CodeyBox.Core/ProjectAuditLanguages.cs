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
            // Backward-compatible language IDs supported before the
            // language-agnostic preset refactor. Keep them accepted so
            // existing operator configs do not get filtered at startup.
            "typescript",
            "javascript",
            "ruby",
            "shell",
        };

    public static bool IsSupported(string language)
        => Supported.Contains(language);
}
