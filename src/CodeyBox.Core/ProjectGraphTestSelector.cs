using System.Globalization;

namespace CodeyBox.Core;

/// <summary>
/// Outcome of project-graph resolution: either the full suite (with the reason
/// why narrowing was unsound) or the exact set of affected test names.
/// </summary>
public sealed record ProjectGraphSelection(
    bool IsFullSuite,
    IReadOnlySet<string> Tests,
    string Reason);

/// <summary>
/// Pure core of project-graph selection. Maps each changed file to its owning
/// MSBuild project (via the baseline's <c>file → project</c> map, which the
/// producer builds by walking the <c>dotnet sln</c> / project-reference graph
/// and precomputing the transitive affected tests per project) and selects the
/// affected tests for those projects, plus the tests defined in the changed
/// files themselves.
///
/// <para>Fail-safe: ANY uncertainty — no baseline, invalid selection options,
/// unknown changeset, a global target, a change owned by an ALWAYS-FULL
/// project (shared/root contracts, source generators, test infrastructure),
/// a whole-file change (no line granularity), a changed file the baseline
/// never references, or an empty affected set — resolves to the full suite.
/// Running more tests is always safe; running fewer than the change requires
/// is not.</para>
/// </summary>
public static class ProjectGraphSelectorCore
{
    public static ProjectGraphSelection SelectTests(
        IReadOnlyList<TestSelectionChangedFile> changedFiles,
        TestSelectionBaseline? baseline,
        CoverageTestSelectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(changedFiles);
        ArgumentNullException.ThrowIfNull(options);
        if (baseline is null)
            return Full("no project-graph baseline is available");
        if (!CoverageTestSelectionOptions.IsValid(options))
            return Full("selection options are invalid");
        if (changedFiles.Count == 0)
            return Full("the changeset could not be determined");

        var graph = baseline.ProjectGraph;
        var byDefiningFile = IndexByDefiningFile(baseline.Tests);
        var selected = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in changedFiles)
        {
            var path = TestSelectionPaths.Normalize(file.Path);
            if (options.IsGlobalTarget(path))
                return Full($"change touches global target '{path}'");
            if (file.ChangedRanges.Count == 0)
                return Full($"whole-file change to '{path}' (no line granularity)");

            var known = false;
            if (graph.FileProject.TryGetValue(path, out var project)
                && !string.IsNullOrWhiteSpace(project))
            {
                known = true;
                var owningProject = TestSelectionPaths.Normalize(project);
                if (options.IsAlwaysFullProject(owningProject))
                    return Full($"change touches always-full project '{owningProject}'");
                if (graph.AffectedTestsByProject.TryGetValue(project, out var affected))
                {
                    foreach (var test in affected)
                        selected.Add(test);
                }
            }
            if (byDefiningFile.TryGetValue(path, out var defined))
            {
                known = true;
                foreach (var test in defined)
                    selected.Add(test);
            }
            if (!known)
                return Full($"no project-graph record references '{path}'");
        }

        if (selected.Count == 0)
            return Full("no affected tests are recorded for the changed projects");

        return new ProjectGraphSelection(false, selected, string.Create(
            CultureInfo.InvariantCulture,
            $"{selected.Count} test(s) affected via the project graph"));
    }

    private static ProjectGraphSelection Full(string reason)
        => new(true, new HashSet<string>(StringComparer.Ordinal), reason);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> IndexByDefiningFile(
        IReadOnlyDictionary<string, BaselineTestEntry> tests)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, entry) in tests)
        {
            var file = TestSelectionPaths.Normalize(entry.DefiningFile);
            if (string.IsNullOrWhiteSpace(file))
                continue;
            if (!index.TryGetValue(file, out var list))
                index[file] = list = new List<string>();
            list.Add(name);
        }
        return index.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)kvp.Value,
            StringComparer.Ordinal);
    }
}

/// <summary>
/// Project-graph regression-test selector: narrows the suite to the tests the
/// baseline records as affected by the changed files' owning projects (plus
/// the tests defined in those files). The ALWAYS-FULL trigger set (shared
/// projects, global build targets) is read live from the injected options so
/// operator edits hot-reload without a restart. Falls back to
/// <see cref="TestSelection.All"/> on any uncertainty — see
/// <see cref="ProjectGraphSelectorCore"/>.
/// </summary>
public sealed class ProjectGraphTestSelector : ITestSelector
{
    /// <summary>Selector name used in justifications and shadow records.</summary>
    public const string SelectorName = "project-graph";

    private readonly Func<CoverageTestSelectionOptions> _optionsProvider;

    /// <param name="optionsProvider">Live hot-reloadable selection options.
    /// Null defaults to fresh defaults (used by unit tests); the composition
    /// root passes an <c>IOptionsMonitor</c>-backed accessor.</param>
    public ProjectGraphTestSelector(Func<CoverageTestSelectionOptions>? optionsProvider = null)
    {
        _optionsProvider = optionsProvider ?? (static () => new CoverageTestSelectionOptions());
    }

    public TestSelectionDecision Select(TestSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CoverageTestSelectionOptions options;
        try
        {
            options = _optionsProvider();
        }
        catch (Exception ex)
        {
            // Fail-safe: unreadable options fall back to the full run.
            return new TestSelectionDecision(
                TestSelection.All,
                $"{SelectorName}: full suite (selection options unavailable ({ex.GetType().Name}))");
        }
        var resolved = ProjectGraphSelectorCore.SelectTests(request.ChangedFiles, request.Baseline, options);
        if (resolved.IsFullSuite)
        {
            return new TestSelectionDecision(
                TestSelection.All,
                $"{SelectorName}: full suite ({resolved.Reason})");
        }
        return new TestSelectionDecision(
            new TestSelection([.. resolved.Tests]),
            $"{SelectorName}: {resolved.Reason}");
    }
}
