namespace CodeyBox.Core;

public static class LanguageProjectDiscovery
{
    private const string PruneExpression =
        "-type d \\( -name '.git' -o -name 'node_modules' -o -name 'bin' -o -name 'obj' \\) -prune -o";

    public const string CSharpDiscoveryScript =
        "solutionDirs=$(find . " + PruneExpression + " \\( -name '*.sln' -o -name '*.slnx' \\) -exec dirname {} \\; | sort -u); " +
        "if [ -n \"$solutionDirs\" ]; then printf '%s\\n' \"$solutionDirs\"; " +
        "else find . " + PruneExpression + " -name '*.csproj' -exec dirname {} \\; | sort -u; fi";

    public const string PythonDiscoveryScript =
        "find . " + PruneExpression + " \\( -name 'pyproject.toml' -o -name 'setup.py' -o -name 'setup.cfg' -o -name 'requirements.txt' \\) -exec dirname {} \\; | sort -u";

    public const string NodeDiscoveryScript =
        "find . " + PruneExpression + " -name 'package.json' -exec dirname {} \\; | sort -u";

    public const string GoDiscoveryScript =
        "find . " + PruneExpression + " -name 'go.mod' -exec dirname {} \\; | sort -u";

    public const string RustDiscoveryScript =
        "find . " + PruneExpression + " -name 'Cargo.toml' -exec dirname {} \\; | sort -u";
}
