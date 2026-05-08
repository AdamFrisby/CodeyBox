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
                continue;
            }

            if (line.StartsWith(">>>>>>>", StringComparison.Ordinal))
            {
                if (startLine is null)
                    throw new InvalidOperationException($"unmatched conflict end marker in {path}:{lineNumber}");
                hunks.Add(new ConflictHunk(path, startLine.Value, lineNumber));
                startLine = null;
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
        var changes = await gitHost.GetChangedPathsAsync(repositoryId, mainTreeish, resolvedTreeish, ct);
        var changedFiles = new HashSet<string>(StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var change in changes)
        {
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

            changedFiles.Add(change.Path);
            if (!hunksByPath.TryGetValue(change.Path, out var fileHunks))
            {
                violations.Add($"{change.Path}:1 changed non-conflicted file");
                continue;
            }

            var diff = await gitHost.GetUnifiedDiffAsync(repositoryId, mainTreeish, resolvedTreeish, change.Path, ct);
            foreach (var lineNumber in ChangedLineNumbers(diff))
            {
                if (!IsInsideAnyHunk(lineNumber, fileHunks, bufferLines))
                    violations.Add($"{change.Path}:{lineNumber}");
            }
        }

        foreach (var file in conflictFiles)
        {
            if (!changedFiles.Contains(file))
                violations.Add($"{file}:1 conflicted file was not part of the resolved diff");
        }

        if (violations.Count > 0)
            throw new ScopeFenceViolation(violations);
    }

    public static IReadOnlyList<AuditFinding> ReviewResolvedDiffForSuspiciousPatterns(string diff)
    {
        var findings = new List<AuditFinding>();
        AddIfContains(findings, diff, "eval(", "eval call in merge resolution");
        AddIfContains(findings, diff, "exec(", "exec call in merge resolution");
        AddIfContains(findings, diff, "Convert.FromBase64String", "base64 decoder in merge resolution");
        AddIfContains(findings, diff, "HttpClient", "network client in merge resolution");
        return findings;
    }

    private static IEnumerable<int> ChangedLineNumbers(string diff)
    {
        var oldLine = 0;
        var newLine = 0;
        foreach (var line in diff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = DiffHunkHeader().Match(line);
            if (match.Success)
            {
                oldLine = int.Parse(match.Groups["old"].Value);
                newLine = int.Parse(match.Groups["new"].Value);
                continue;
            }

            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
                continue;

            if (line.StartsWith('+'))
            {
                yield return newLine;
                newLine++;
            }
            else if (line.StartsWith('-'))
            {
                yield return oldLine;
                oldLine++;
            }
            else if (line.StartsWith(' '))
            {
                oldLine++;
                newLine++;
            }
        }
    }

    private static bool IsInsideAnyHunk(int lineNumber, IReadOnlyList<ConflictHunk> hunks, int bufferLines)
        => hunks.Any(h => lineNumber >= h.StartLine - bufferLines && lineNumber <= h.EndLine + bufferLines);

    private static string DescribeStatus(GitChangedPath change)
        => change.Status.StartsWith('R')
            ? $"rename from {change.OldPath}"
            : change.Status.StartsWith('A')
                ? "new file"
                : "deleted file";

    private static void AddIfContains(List<AuditFinding> findings, string diff, string needle, string title)
    {
        if (!diff.Contains(needle, StringComparison.OrdinalIgnoreCase))
            return;
        findings.Add(new AuditFinding(
            "merge-security-review",
            AuditSeverity.Info,
            title,
            "Advisory-only merge security review finding; deterministic scope fence remains the merge gate."));
    }

    [GeneratedRegex(@"^@@ -(?<old>\d+)(?:,\d+)? \+(?<new>\d+)(?:,\d+)? @@", RegexOptions.CultureInvariant)]
    private static partial Regex DiffHunkHeader();
}
