namespace CodeyBox.Audit.Shell;

/// <summary>
/// Structural guard for <c>dotnet test</c> invocations.
///
/// <c>dotnet test</c> accepts a project, solution, directory, or test-source
/// assembly as its optional positional target — but a bare <c>.dll</c> flips
/// the command into VSTest passthrough (the <c>--no-build</c> flag is ignored)
/// and VSTest rejects the source with <c>The argument ... is invalid</c> /
/// <c>The test source file ... was not found</c>, exiting 1 with zero tests
/// executed. That refusal is indistinguishable from a red suite at the exit-code
/// level, so the gate must never construct the assembly form: it runs the
/// project/solution form (<c>dotnet test [--no-build] [--filter ...]</c> with
/// no positional target) and relies on MSBuild discovery from the working
/// directory.
/// </summary>
public static class DotnetTestArgv
{
    /// <summary>
    /// The only <c>dotnet test</c> option whose value this guard must look
    /// through: a VSTest filter expression may legitimately end in
    /// <c>.dll</c> (e.g. a test in a namespace segment named <c>dll</c>), so
    /// the token following an exact <c>--filter</c> is a filter value, never
    /// a positional test source. Every other option the gate emits takes a
    /// value that can never be an assembly path. Exact match only — never a
    /// prefix match.
    /// </summary>
    private const string FilterOption = "--filter";

    /// <summary>
    /// Returns the first bare test-assembly positional in <paramref name="argv"/>,
    /// or null when the invocation is not in the rejected assembly form.
    /// Non-<c>dotnet test</c> commands are out of scope and always yield null.
    /// A token counts as positional when it does not start with <c>-</c> and is
    /// not the value of <c>--filter</c>; option tokens (including
    /// <c>--filter=...</c>) can never be positionals.
    /// </summary>
    public static string? FindBareAssemblyPositional(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count < 2
            || !string.Equals(argv[0], "dotnet", StringComparison.Ordinal)
            || !string.Equals(argv[1], "test", StringComparison.Ordinal))
        {
            return null;
        }

        for (var i = 2; i < argv.Count; i++)
        {
            var token = argv[i];
            if (string.IsNullOrEmpty(token) || token[0] == '-')
                continue;
            if (i > 2 && string.Equals(argv[i - 1], FilterOption, StringComparison.Ordinal))
                continue;
            if (token.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return token;
        }

        return null;
    }
}
