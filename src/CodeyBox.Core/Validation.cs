using System.Text.RegularExpressions;

namespace CodeyBox.Core;

/// <summary>
/// Input validation for fields that get passed to git or to the shell.
///
/// Git is the main hazard: many subcommands treat arguments starting with
/// "-" as options even when the user "intended" them as positional values.
/// `git clone --upload-pack=evil /dest` is a classic remote-code-execution
/// trick that has bitten many tools. We defend by:
///   1. Rejecting leading-dash inputs at the validation layer.
///   2. Using `--` separators in subprocess argv where git supports them.
/// </summary>
public static partial class Validation
{
    /// <summary>
    /// Conservative branch-name regex. Stricter than git's own
    /// check-ref-format: only ASCII alnum + a small set of separators.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/\-]{0,254}$", RegexOptions.CultureInvariant)]
    private static partial Regex BranchNameRegex();

    /// <summary>
    /// Repository URL: accept https/http/git/ssh URIs, scp-like git URLs
    /// (user@host:path), or absolute filesystem paths. Reject anything with
    /// a leading dash so git can't interpret it as an option.
    /// </summary>
    [GeneratedRegex(
        @"^(https?://|git://|ssh://|git\+ssh://|file://|/|[a-zA-Z][a-zA-Z0-9+.\-]*://|[A-Za-z0-9_][A-Za-z0-9._\-]*@[A-Za-z0-9.\-]+:[A-Za-z0-9./_\-]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex RepoUrlRegex();

    public static void ValidateBranchName(string name, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException($"{fieldName} must not be empty", fieldName);
        if (name.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException($"{fieldName} must not contain '..'", fieldName);
        if (name.EndsWith(".lock", StringComparison.Ordinal))
            throw new ArgumentException($"{fieldName} must not end with '.lock'", fieldName);
        if (!BranchNameRegex().IsMatch(name))
            throw new ArgumentException($"{fieldName} '{name}' is not a valid branch name", fieldName);
    }

    public static void ValidateRepositoryUrl(string url, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException($"{fieldName} must not be empty", fieldName);
        if (url.StartsWith('-'))
            throw new ArgumentException($"{fieldName} must not start with '-'", fieldName);
        if (url.AsSpan().IndexOfAny(['\n', '\r', '\0']) >= 0)
            throw new ArgumentException($"{fieldName} must not contain control characters", fieldName);
        if (!RepoUrlRegex().IsMatch(url))
            throw new ArgumentException($"{fieldName} is not a recognised URL or path form", fieldName);
    }

    /// <summary>
    /// Validates a string used as a positional argument for a tool that may
    /// otherwise interpret leading '-' as an option, and forbids control
    /// characters that could affect log/audit fidelity.
    /// </summary>
    public static void ValidateNoOptionLikeOrControl(string value, string fieldName)
    {
        if (value is null) throw new ArgumentNullException(fieldName);
        if (value.StartsWith('-'))
            throw new ArgumentException($"{fieldName} must not start with '-'", fieldName);
        if (value.AsSpan().IndexOfAny(['\n', '\r', '\0']) >= 0)
            throw new ArgumentException($"{fieldName} must not contain control characters", fieldName);
    }
}
