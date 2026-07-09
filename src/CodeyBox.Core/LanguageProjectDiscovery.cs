namespace CodeyBox.Core;

public static class LanguageProjectDiscovery
{
    public const int MaxProjectDirectoriesToRun = 32;

    private const string PruneExpression =
        "-type d \\( -name '.git' -o -name 'node_modules' -o -name 'bin' -o -name 'obj' \\) -prune -o";

    public const string CSharpDiscoveryScript = """
if find . -maxdepth 1 \( -name '*.csproj' -o -name '*.sln' -o -name '*.slnx' \) -print -quit | grep -q .; then
  printf '.\n'
else
  find . -type d \( -name '.git' -o -name 'node_modules' -o -name 'bin' -o -name 'obj' \) -prune -o \( -name '*.csproj' -o -name '*.sln' -o -name '*.slnx' \) -exec dirname {} \; | sort -u
fi
""";

    public const string PythonDiscoveryScript =
        "find . " + PruneExpression + " \\( -name 'pyproject.toml' -o -name 'setup.py' -o -name 'setup.cfg' -o -name 'requirements.txt' \\) -exec dirname {} \\; | sort -u";

    public const string NodeDiscoveryScript =
        "find . " + PruneExpression + " -name 'package.json' -exec dirname {} \\; | sort -u";

    public const string GoDiscoveryScript =
        "find . " + PruneExpression + " -name 'go.mod' -exec dirname {} \\; | sort -u";

    public const string RustDiscoveryScript =
        "find . " + PruneExpression + " -name 'Cargo.toml' -exec dirname {} \\; | sort -u";

    public static IReadOnlyList<string> SelectProjectDirectoriesToRun(
        string language,
        IReadOnlyList<string> projectDirectories)
    {
        if (string.Equals(language, "csharp", StringComparison.OrdinalIgnoreCase) &&
            projectDirectories.Contains(".", StringComparer.Ordinal))
            return ["."];

        return projectDirectories;
    }

    public static IReadOnlyList<string> SelectProjectDirectoriesToRun(
        string language,
        IReadOnlyList<string> projectDirectories,
        out int skippedDueToLimit)
    {
        skippedDueToLimit = 0;
        var selected = SelectProjectDirectoriesToRun(language, projectDirectories);
        if (selected.Count <= MaxProjectDirectoriesToRun)
            return selected;

        skippedDueToLimit = selected.Count - MaxProjectDirectoriesToRun;
        return selected.Take(MaxProjectDirectoriesToRun).ToList();
    }
}
