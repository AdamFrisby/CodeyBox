namespace CodeyBox.Core;

/// <summary>
/// Optional capability mixin for auditors that know the language preset
/// they belong to and the shell script that discovers project marker files
/// for that language. Mechanical fixers and other downstream consumers use
/// this to scope work to the same set of projects the auditor scoped to —
/// without depending on internal preset types.
///
/// Custom auditors (including hand-rolled <c>csharp:format-check</c> shell
/// auditors) MAY implement this mixin so they participate in mechanical-edit
/// flows that look up their marker script.
/// </summary>
public interface IAuditorLanguageContext
{
    /// <summary>
    /// Lower-case language identifier (e.g. <c>"csharp"</c>, <c>"python"</c>).
    /// </summary>
    string Language { get; }

    /// <summary>
    /// Shell script (executable via <c>sh -c</c>) that prints one project
    /// directory per line on stdout. Empty / blank is treated as "no script
    /// provided" by consumers, which then either fall back to a default or
    /// skip.
    /// </summary>
    string MarkerScript { get; }
}
