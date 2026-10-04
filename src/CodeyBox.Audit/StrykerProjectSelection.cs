namespace CodeyBox.Audit;

/// <summary>
/// One project under test with the test projects covering it and the
/// validated mutate patterns (relative to <see cref="ProjectDirectory"/>)
/// derived from the changed files it owns.
/// </summary>
public sealed record StrykerProjectGroup(
    string ProjectCsproj,
    string ProjectDirectory,
    string ProjectFileName,
    IReadOnlyList<string> TestProjects,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> MutatePatterns);

/// <summary>Deterministic result of mapping changed files to projects.</summary>
public sealed record StrykerSelection(
    IReadOnlyList<StrykerProjectGroup> Groups,
    IReadOnlyList<string> UnmappedChangedFiles,
    IReadOnlyList<string> TestOnlyChangedFiles);

/// <summary>
/// Maps validated repository-relative changed files to .NET production
/// projects and their covering test projects. Pure function of its inputs:
/// no I/O, deterministic (ordinal sorts, longest-prefix ownership, first-
/// sorted wins on ties). Test-project directories win over production
/// directories so changed test code never generates mutate patterns.
/// </summary>
public static class StrykerProjectSelector
{
    /// <summary>
    /// Selects one group per production project that owns at least one
    /// changed file. Test projects owning changed files are reported via
    /// <see cref="StrykerSelection.TestOnlyChangedFiles"/>; files under no
    /// known project via <see cref="StrykerSelection.UnmappedChangedFiles"/>.
    /// </summary>
    /// <param name="changedFiles">Validated repo-relative changed paths.</param>
    /// <param name="productionProjects">Validated repo-relative production csproj paths.</param>
    /// <param name="testProjects">Validated repo-relative test csproj paths.</param>
    /// <param name="projectReferences">
    /// Map from csproj path to the file names it references
    /// (e.g. <c>"SampleCalc.Tests.csproj" → ["SampleCalc.csproj"]</c>).
    /// </param>
    /// <param name="maxTestProjectsPerProject">Cap applied after ordinal sort.</param>
    public static StrykerSelection Select(
        IReadOnlyList<string> changedFiles,
        IReadOnlyList<string> productionProjects,
        IReadOnlyList<string> testProjects,
        IReadOnlyDictionary<string, IReadOnlyList<string>> projectReferences,
        int maxTestProjectsPerProject)
    {
        var testDirs = testProjects
            .Select(StrykerPaths.DirectoryOf)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(d => d.Length)
            .ToList();
        var prodByDir = productionProjects
            .GroupBy(StrykerPaths.DirectoryOf, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(p => p, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        var owned = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var unmapped = new List<string>();
        var testOnly = new List<string>();

        foreach (var file in changedFiles.OrderBy(f => f, StringComparer.Ordinal))
        {
            if (IsUnderAnyDirectory(file, testDirs))
            {
                testOnly.Add(file);
                continue;
            }
            var owner = FindOwner(file, prodByDir);
            if (owner is null)
            {
                unmapped.Add(file);
                continue;
            }
            if (!owned.TryGetValue(owner, out var list))
            {
                list = [];
                owned[owner] = list;
            }
            list.Add(file);
        }

        var groups = new List<StrykerProjectGroup>();
        foreach (var csproj in owned.Keys.OrderBy(p => p, StringComparer.Ordinal))
        {
            var fileName = FileNameOf(csproj);
            var covering = testProjects
                .Where(t => References(t, projectReferences, fileName))
                .OrderBy(t => t, StringComparer.Ordinal)
                .Take(Math.Max(1, maxTestProjectsPerProject))
                .ToList();
            var dir = StrykerPaths.DirectoryOf(csproj);
            var patterns = owned[csproj]
                .Select(f => dir.Length == 0 ? f : f[(dir.Length + 1)..])
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            groups.Add(new StrykerProjectGroup(
                csproj, dir, fileName, covering, owned[csproj], patterns));
        }

        return new StrykerSelection(groups, unmapped, testOnly);
    }

    private static bool IsUnderAnyDirectory(string file, IReadOnlyList<string> dirs)
    {
        foreach (var dir in dirs)
        {
            if (dir.Length == 0)
                return true;
            if (file.Length > dir.Length
                && file.StartsWith(dir, StringComparison.Ordinal)
                && file[dir.Length] == '/')
                return true;
        }
        return false;
    }

    private static string? FindOwner(
        string file, IReadOnlyDictionary<string, List<string>> prodByDir)
    {
        // Longest owning directory wins; the per-directory list is already
        // ordinal-first, so same-directory ties stay deterministic.
        string? best = null;
        var bestLength = -1;
        foreach (var dir in prodByDir.Keys)
        {
            if (dir.Length == 0)
            {
                if (0 > bestLength)
                {
                    best = prodByDir[dir][0];
                    bestLength = 0;
                }
                continue;
            }
            if (dir.Length > bestLength
                && file.Length > dir.Length
                && file.StartsWith(dir, StringComparison.Ordinal)
                && file[dir.Length] == '/')
            {
                best = prodByDir[dir][0];
                bestLength = dir.Length;
            }
        }
        return best;
    }

    private static bool References(
        string testCsproj,
        IReadOnlyDictionary<string, IReadOnlyList<string>> projectReferences,
        string productionFileName) =>
        projectReferences.TryGetValue(testCsproj, out var names)
        && names.Any(n => n.Equals(productionFileName, StringComparison.OrdinalIgnoreCase));

    private static string FileNameOf(string csproj)
    {
        var index = csproj.LastIndexOf('/');
        return index < 0 ? csproj : csproj[(index + 1)..];
    }
}
