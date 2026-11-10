namespace CodeyBox.Core;

/// <summary>
/// Parses <c>git diff --name-status -M</c> stdout into
/// <see cref="GitChangedPath"/> records. Shared by the host git
/// implementation and sandbox-side verifiers so both interpret status
/// letters and rename columns identically: two-column lines are
/// <c>STATUS\tpath</c>, three-column lines are
/// <c>STATUS\toldPath\tnewPath</c> (rename/copy). Lines in any other shape
/// are skipped, matching the historical host parser.
/// </summary>
public static class GitNameStatusParser
{
    public static IReadOnlyList<GitChangedPath> Parse(string stdout)
    {
        var changes = new List<GitChangedPath>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', StringSplitOptions.None);
            if (parts.Length == 2)
                changes.Add(new GitChangedPath(parts[0], parts[1]));
            else if (parts.Length == 3)
                changes.Add(new GitChangedPath(parts[0], parts[2], parts[1]));
        }
        return changes;
    }
}
