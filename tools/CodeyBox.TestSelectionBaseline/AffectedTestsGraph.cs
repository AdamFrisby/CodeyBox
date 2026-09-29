namespace CodeyBox.TestSelectionProducer;

/// <summary>
/// Precomputes <c>project → affected tests</c>: a change owned by project P
/// affects every test whose owning test project is P or a transitive dependent
/// of P (walking ProjectReference edges backwards).
/// </summary>
public static class AffectedTestsGraph
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Compute(
        IReadOnlyDictionary<string, IReadOnlyList<string>> projectReferences,
        IReadOnlyDictionary<string, string> testOwningProject)
    {
        ArgumentNullException.ThrowIfNull(projectReferences);
        ArgumentNullException.ThrowIfNull(testOwningProject);

        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var projects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (from, tos) in projectReferences)
        {
            projects.Add(from);
            foreach (var to in tos)
            {
                projects.Add(to);
                if (!dependents.TryGetValue(to, out var list))
                    dependents[to] = list = [];
                list.Add(from);
            }
        }

        foreach (var project in testOwningProject.Values)
            projects.Add(project);

        var testsByProject = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (test, project) in testOwningProject)
        {
            if (!testsByProject.TryGetValue(project, out var list))
                testsByProject[project] = list = [];
            list.Add(test);
        }

        var affected = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var reachable = TransitiveDependents(project, dependents);
            reachable.Add(project);
            var tests = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var candidate in reachable)
            {
                if (testsByProject.TryGetValue(candidate, out var owned))
                {
                    foreach (var test in owned)
                        tests.Add(test);
                }
            }

            affected[project] = [.. tests];
        }

        return affected;
    }

    private static HashSet<string> TransitiveDependents(
        string project,
        IReadOnlyDictionary<string, List<string>> dependents)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(project);
        seen.Add(project);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!dependents.TryGetValue(current, out var next))
                continue;
            foreach (var dep in next)
            {
                if (seen.Add(dep))
                    queue.Enqueue(dep);
            }
        }

        seen.Remove(project);
        return seen;
    }
}
