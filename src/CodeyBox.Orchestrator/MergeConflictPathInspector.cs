using System.Text;
using System.Text.RegularExpressions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal static class MergeConflictPathInspector
{
    internal static IReadOnlyDictionary<string, string> GitLiteralPathspecEnvironment { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_LITERAL_PATHSPECS"] = "1",
        };

    private static readonly Regex LsFilesUnmergedRecord = new(
        @"\A[0-7]{6} (?:[0-9a-fA-F]{64}|[0-9a-fA-F]{40}) [1-3]\t(?<path>[^\0\p{Cc}]+)\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LsFilesUnmergedRecordStart = new(
        @"[0-7]{6} (?:[0-9a-fA-F]{64}|[0-9a-fA-F]{40}) [1-3]\t",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AnsiEscapeSequence = new(
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\)|[@-Z\\-_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static async Task<IReadOnlyList<string>> ListUnmergedPathsAsync(
        ISandbox sandbox, string workingDirectory, CancellationToken ct)
    {
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", workingDirectory, "ls-files", "-u", "-z"],
        }, ct);
        if (!result.Success)
            throw new MergeConflictResolutionFailedException(
                $"failed to inspect unmerged paths: {result.Stderr.Trim()}");

        return ParseUnmergedPathsFromLsFilesStdout(result.Stdout);
    }

    internal static IReadOnlyList<string> ParseUnmergedPathsFromLsFilesStdout(string stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return [];

        var paths = new List<string>();
        foreach (var rawSegment in stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsBenignLsFilesFramingSegment(rawSegment))
                continue;

            var segment = StripRecognizedTerminalStartupNoisePrefix(rawSegment);
            if (IsRecognizedTerminalStartupNoise(segment))
                continue;

            var match = LsFilesUnmergedRecord.Match(segment);
            if (!match.Success)
            {
                throw new MergeConflictResolutionFailedException(
                    "malformed git ls-files -u output segment '" +
                    Truncate(EscapeForSingleLine(rawSegment), 200) +
                    "'");
            }

            var path = match.Groups["path"].Value;
            ValidateRelativeWorkPath(path);
            paths.Add(path);
        }

        return paths
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Reject path patterns that escape the working directory (absolute paths,
    /// backslashes, traversal segments, Git pathspec-magic prefixes) or carry
    /// terminal/control bytes.
    /// Backticks are valid Git path characters; prompt builders must serialize
    /// paths as data instead of broadening filesystem safety checks.
    /// Git treats leading ':' as pathspec magic, including ':foo' meaning 'foo'.
    /// </summary>
    internal static void ValidateRelativeWorkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Contains('\\', StringComparison.Ordinal)
            || path[0] == ':'
            || path.Any(static ch => char.IsControl(ch))
            || path.Split('/', StringSplitOptions.None).Any(static part => part is "" or "." or ".."))
        {
            throw new MergeConflictResolutionFailedException(
                $"unsafe conflict file path '{EscapeForSingleLine(path)}'");
        }
    }

    private static bool IsBenignLsFilesFramingSegment(string segment) =>
        segment.All(static ch => ch is '\r' or '\n');

    private static string StripRecognizedTerminalStartupNoisePrefix(string segment)
    {
        var start = LsFilesUnmergedRecordStart.Match(segment);
        if (!start.Success || start.Index == 0)
            return segment;

        return IsRecognizedTerminalStartupNoise(segment[..start.Index])
            ? segment[start.Index..]
            : segment;
    }

    private static bool IsRecognizedTerminalStartupNoise(string value)
    {
        if (!ContainsTerminalControl(value))
            return false;

        var cleaned = AnsiEscapeSequence.Replace(value, "");
        var sb = new StringBuilder(cleaned.Length);
        foreach (var ch in cleaned)
        {
            if (!char.IsControl(ch))
                sb.Append(ch);
        }

        var text = sb.ToString().Trim();
        if (!text.StartsWith("Starting ", StringComparison.Ordinal))
            return false;

        var rest = text["Starting ".Length..].TrimStart();
        return rest.Length > 0;
    }

    private static bool ContainsTerminalControl(string value)
    {
        if (AnsiEscapeSequence.IsMatch(value))
            return true;

        foreach (var ch in value)
        {
            if (char.IsControl(ch))
                return true;
        }

        return false;
    }

    private static string EscapeForSingleLine(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    sb.Append(@"\\");
                    break;
                case '\'':
                    sb.Append(@"\'");
                    break;
                default:
                    if (char.IsControl(ch))
                        sb.Append(@"\u").Append(((int)ch).ToString("X4"));
                    else
                        sb.Append(ch);
                    break;
            }
        }

        return sb.ToString();
    }

    private static string Truncate(string value, int maxChars)
    {
        if (value.Length <= maxChars) return value;
        return value[..maxChars] + "...";
    }
}
