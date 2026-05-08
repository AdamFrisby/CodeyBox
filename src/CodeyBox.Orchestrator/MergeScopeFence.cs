using System.Text.RegularExpressions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public sealed class MergePhaseInconsistentResultException : Exception
{
    public MergePhaseInconsistentResultException(string message) : base(message) { }
}

public sealed class ScopeFenceViolation : Exception
{
    public IReadOnlyList<string> Violations { get; }

    public ScopeFenceViolation(IReadOnlyList<string> violations)
        : base("merge conflict resolver changed lines outside the permitted conflict hunks: " + string.Join(", ", violations))
    {
        Violations = violations;
    }
}

public sealed class MergeConflictResolutionFailedException : Exception
{
    public MergeConflictResolutionFailedException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

internal sealed record ConflictHunk(string Path, int StartLine, int EndLine);

internal static partial class MergeScopeFence
{
    public static IReadOnlyList<ConflictHunk> ExtractConflictHunks(string path, string conflictedContent)
    {
        var hunks = new List<ConflictHunk>();
        int? startLine = null;
        var state = ConflictParseState.Outside;
        var lines = conflictedContent.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var line = lines[i];
            if (line.StartsWith("<<<<<<<", StringComparison.Ordinal))
            {
                if (startLine is not null)
                    throw new InvalidOperationException($"nested conflict marker in {path}:{lineNumber}");
                startLine = lineNumber;
                state = ConflictParseState.MainSide;
                continue;
            }

            if (line.StartsWith("|||||||", StringComparison.Ordinal) && state == ConflictParseState.MainSide)
            {
                state = ConflictParseState.BaseSide;
                continue;
            }

            if (line.StartsWith("=======", StringComparison.Ordinal) &&
                state is ConflictParseState.MainSide or ConflictParseState.BaseSide)
            {
                state = ConflictParseState.WorkSide;
                continue;
            }

            if (line.StartsWith(">>>>>>>", StringComparison.Ordinal))
            {
                if (startLine is null)
                    throw new InvalidOperationException($"unmatched conflict end marker in {path}:{lineNumber}");
                hunks.Add(new ConflictHunk(path, startLine.Value, lineNumber));
                startLine = null;
                state = ConflictParseState.Outside;
                continue;
            }
        }

        if (startLine is not null)
            throw new InvalidOperationException($"unclosed conflict marker in {path}:{startLine.Value}");
        return hunks;
    }

    public static async Task VerifyAsync(
        IGitHost gitHost,
        string repositoryId,
        string mainTreeish,
        string conflictBaselineTreeish,
        string resolvedTreeish,
        IReadOnlyList<ConflictHunk> hunks,
        int bufferLines,
        CancellationToken ct)
    {
        _ = mainTreeish;
        if (bufferLines < 0)
            throw new ArgumentOutOfRangeException(nameof(bufferLines), "buffer must be non-negative");

        var hunksByPath = hunks
            .GroupBy(h => h.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var conflictFiles = hunksByPath.Keys.ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();

        var resolvedChanges = await gitHost.GetChangedPathsAsync(repositoryId, conflictBaselineTreeish, resolvedTreeish, ct);
        var changedFiles = resolvedChanges.Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var missing in conflictFiles.Except(changedFiles, StringComparer.Ordinal))
            violations.Add($"{missing}:1 conflicted file was not part of the resolved diff");

        foreach (var change in resolvedChanges)
        {
            if (!conflictFiles.Contains(change.Path))
            {
                violations.Add(change.Status.StartsWith('R') || change.Status.StartsWith('A') || change.Status.StartsWith('D')
                    ? $"{change.Path}:1 {DescribeStatus(change)}"
                    : $"{change.Path}:1 changed non-conflicted file");
                continue;
            }

            if (change.Status.StartsWith('R') || change.Status.StartsWith('A') || change.Status.StartsWith('D'))
            {
                violations.Add($"{change.Path}:1 {DescribeStatus(change)}");
                continue;
            }

            if (!change.Status.StartsWith('M'))
            {
                violations.Add($"{change.Path}:1 unsupported change status {change.Status}");
                continue;
            }
        }

        foreach (var (path, fileHunks) in hunksByPath)
        {
            var diff = await gitHost.GetUnifiedDiffAsync(repositoryId, conflictBaselineTreeish, resolvedTreeish, path, ct);
            foreach (var lineNumber in ChangedOldLineNumbers(diff))
            {
                if (!IsInsideAnyHunk(lineNumber, fileHunks, bufferLines))
                    violations.Add($"{path}:{lineNumber}");
            }
        }

        if (violations.Count > 0)
            throw new ScopeFenceViolation(violations);
    }

    private static IEnumerable<int> ChangedOldLineNumbers(string diff)
    {
        var oldLine = 0;
        var newLine = 0;
        var editStart = (int?)null;
        var editEnd = 0;
        var editHasDeletion = false;

        IEnumerable<int> FlushEdit()
        {
            if (editStart is null)
                yield break;
            for (var line = editStart.Value; line <= editEnd; line++)
                yield return line;
            editStart = null;
            editEnd = 0;
            editHasDeletion = false;
        }

        void TouchOldLine(int lineNumber)
        {
            if (editStart is null)
            {
                editStart = lineNumber;
                editEnd = lineNumber;
                return;
            }

            if (lineNumber < editStart.Value)
                editStart = lineNumber;
            if (lineNumber > editEnd)
                editEnd = lineNumber;
        }

        foreach (var line in diff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = DiffHunkHeader().Match(line);
            if (match.Success)
            {
                foreach (var changedLine in FlushEdit())
                    yield return changedLine;
                oldLine = int.Parse(match.Groups["old"].Value);
                newLine = int.Parse(match.Groups["new"].Value);
                continue;
            }

            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
                continue;

            if (line.StartsWith('+'))
            {
                if (!editHasDeletion)
                    TouchOldLine(oldLine);
                newLine++;
            }
            else if (line.StartsWith('-'))
            {
                TouchOldLine(oldLine);
                editHasDeletion = true;
                oldLine++;
            }
            else if (line.StartsWith(' '))
            {
                foreach (var changedLine in FlushEdit())
                    yield return changedLine;
                oldLine++;
                newLine++;
            }
        }

        foreach (var changedLine in FlushEdit())
            yield return changedLine;
    }

    private static bool IsInsideAnyHunk(int lineNumber, IReadOnlyList<ConflictHunk> hunks, int bufferLines)
        => hunks.Any(h => lineNumber >= h.StartLine - bufferLines && lineNumber <= h.EndLine + bufferLines);

    private static string DescribeStatus(GitChangedPath change)
        => change.Status.StartsWith('R')
            ? $"rename from {change.OldPath}"
            : change.Status.StartsWith('A')
                ? "new file"
                : "deleted file";

    [GeneratedRegex(@"^@@ -(?<old>\d+)(?:,\d+)? \+(?<new>\d+)(?:,\d+)? @@", RegexOptions.CultureInvariant)]
    private static partial Regex DiffHunkHeader();

    private enum ConflictParseState
    {
        Outside,
        MainSide,
        BaseSide,
        WorkSide,
    }
}
