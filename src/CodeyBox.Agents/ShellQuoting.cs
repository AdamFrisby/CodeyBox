namespace CodeyBox.Agents;

/// <summary>
/// POSIX single-quote escaping for values interpolated into generated
/// <c>bash</c>/<c>sh</c> scripts. The one quoting implementation shared by
/// every generated-script helper in this assembly (and by sibling agent
/// assemblies via InternalsVisibleTo) so producers can never drift on
/// escaping rules.
/// </summary>
internal static class ShellQuoting
{
    /// <summary>
    /// Single-quotes <paramref name="value"/> for safe interpolation into a
    /// <c>bash</c>/<c>sh</c> script, escaping embedded single quotes.
    /// </summary>
    internal static string Quote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
