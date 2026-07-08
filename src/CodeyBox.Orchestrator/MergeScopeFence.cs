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
    public string? FailureKind { get; }
    public AgentKind? Agent { get; }

    public MergeConflictResolutionFailedException(
        string message,
        Exception? innerException = null,
        string? failureKind = null,
        AgentKind? agent = null)
        : base(message, innerException)
    {
        var prior = innerException as MergeConflictResolutionFailedException;
        FailureKind = failureKind ?? prior?.FailureKind;
        Agent = agent ?? prior?.Agent;
    }
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
        if (bufferLines < 0)
            throw new ArgumentOutOfRangeException(nameof(bufferLines), "buffer must be non-negative");

        var hunksByPath = hunks
            .GroupBy(h => h.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var conflictFiles = hunksByPath.Keys.ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();

        // The resolver edits the canonical conflicted merge tree, not the
        // pre-merge main tree. Diffing against that baseline keeps line
        // coordinates in the same file shape as the captured conflict hunks
        // and excludes clean work-branch changes that git already merged.
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
            foreach (var lineNumber in ChangedOldLineCoordinates(diff))
            {
                if (!IsInsideAnyHunk(lineNumber, fileHunks, bufferLines))
                    violations.Add($"{path}:{lineNumber}");
            }
        }

        if (violations.Count > 0)
            throw new ScopeFenceViolation(violations);
    }

    public static void VerifyResolvedContents(
        IReadOnlyDictionary<string, string> conflictBaselines,
        IReadOnlyDictionary<string, string> resolvedContents,
        IReadOnlyList<ConflictHunk> hunks,
        int bufferLines)
    {
        if (bufferLines < 0)
            throw new ArgumentOutOfRangeException(nameof(bufferLines), "buffer must be non-negative");

        var hunksByPath = hunks
            .GroupBy(h => h.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var expected = hunksByPath.Keys.ToHashSet(StringComparer.Ordinal);
        var actual = resolvedContents.Keys.ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var missing in expected.Except(actual, StringComparer.Ordinal))
            violations.Add($"{missing}:1 conflicted file was not returned by the resolver");
        foreach (var extra in actual.Except(expected, StringComparer.Ordinal))
            violations.Add($"{extra}:1 resolver returned non-conflicted file");

        foreach (var path in expected.Intersect(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!conflictBaselines.TryGetValue(path, out var baseline))
            {
                violations.Add($"{path}:1 missing conflict baseline");
                continue;
            }

            foreach (var lineNumber in ChangedLineCoordinates(baseline, resolvedContents[path]))
            {
                if (!IsInsideAnyHunk(lineNumber, hunksByPath[path], bufferLines))
                    violations.Add($"{path}:{lineNumber}");
            }
        }

        if (violations.Count > 0)
            throw new ScopeFenceViolation(violations);
    }

    private static IEnumerable<int> ChangedLineCoordinates(string before, string after)
    {
        var oldLines = SplitLines(before);
        var newLines = SplitLines(after);
        var cells = (long)(oldLines.Length + 1) * (newLines.Length + 1);
        if (cells > 1_000_000)
            return ChangedLineCoordinatesByWindow(oldLines, newLines);

        var dp = new int[oldLines.Length + 1, newLines.Length + 1];
        for (var i = oldLines.Length - 1; i >= 0; i--)
        {
            for (var j = newLines.Length - 1; j >= 0; j--)
            {
                dp[i, j] = string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var changed = new SortedSet<int>();
        var oldIndex = 0;
        var newIndex = 0;
        while (oldIndex < oldLines.Length || newIndex < newLines.Length)
        {
            if (oldIndex < oldLines.Length
                && newIndex < newLines.Length
                && string.Equals(oldLines[oldIndex], newLines[newIndex], StringComparison.Ordinal))
            {
                oldIndex++;
                newIndex++;
            }
            else if (newIndex < newLines.Length
                     && (oldIndex == oldLines.Length || dp[oldIndex, newIndex + 1] >= dp[oldIndex + 1, newIndex]))
            {
                changed.Add(oldIndex + 1);
                newIndex++;
            }
            else
            {
                changed.Add(oldIndex + 1);
                oldIndex++;
            }
        }

        return changed;
    }

    private static IEnumerable<int> ChangedLineCoordinatesByWindow(string[] oldLines, string[] newLines)
    {
        var prefix = 0;
        while (prefix < oldLines.Length
               && prefix < newLines.Length
               && string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldLines.Length - prefix
               && suffix < newLines.Length - prefix
               && string.Equals(oldLines[oldLines.Length - 1 - suffix], newLines[newLines.Length - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        var end = Math.Max(prefix + 1, oldLines.Length - suffix);
        for (var line = prefix + 1; line <= end; line++)
            yield return line;
    }

    private static string[] SplitLines(string content)
        => content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static IEnumerable<int> ChangedOldLineCoordinates(string diff)
    {
        var oldLine = 0;
        var deletedLines = new List<int>();
        var addedLineCount = 0;
        var insertionAnchor = (int?)null;

        IEnumerable<int> FlushEdit()
        {
            if (deletedLines.Count == 0 && addedLineCount == 0)
                yield break;

            foreach (var line in deletedLines)
                yield return line;

            var addedAnchor = deletedLines.Count > 0
                ? deletedLines[0]
                : insertionAnchor.GetValueOrDefault(oldLine + 1);
            for (var offset = 0; offset < addedLineCount; offset++)
                yield return addedAnchor + offset;

            deletedLines.Clear();
            addedLineCount = 0;
            insertionAnchor = null;
        }

        void TouchAddedLine()
        {
            if (addedLineCount == 0 && deletedLines.Count == 0)
            {
                // In a zero-context diff, a pure insertion at "@@ -N,0 +M @@" is
                // inserted after old line N, so its first projected coordinate is N+1.
                insertionAnchor = oldLine + 1;
            }
            addedLineCount++;
        }

        foreach (var line in diff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = DiffHunkHeader().Match(line);
            if (match.Success)
            {
                foreach (var changedLine in FlushEdit())
                    yield return changedLine;
                oldLine = int.Parse(match.Groups["old"].Value);
                continue;
            }

            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
                continue;

            if (line.StartsWith('+'))
            {
                TouchAddedLine();
            }
            else if (line.StartsWith('-'))
            {
                deletedLines.Add(oldLine);
                oldLine++;
            }
            else if (line.StartsWith(' '))
            {
                foreach (var changedLine in FlushEdit())
                    yield return changedLine;
                oldLine++;
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
