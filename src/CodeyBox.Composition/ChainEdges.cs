using System.Text.RegularExpressions;

namespace CodeyBox.Composition;

/// <summary>
/// The edge algebra of a chain under composition: items are 1-based
/// positions, an edge is "item i waits for item j". Everything here is
/// pure over lists of ints so the outline can offer whole-chain shapes
/// (series, parallel, fan-out) as single acts, read a typed "1, 3" back
/// into edges, detect cycles before anything is filed, and renumber when
/// an item is removed. Deliberately no knowledge of titles or bodies.
/// </summary>
public static partial class ChainEdges
{
    /// <summary>Each item waits for the one before it. Item 1 is the root.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> Series(int count) =>
        Enumerable.Range(1, Math.Max(0, count))
            .Select(n => (IReadOnlyList<int>)(n == 1 ? [] : [n - 1]))
            .ToList();

    /// <summary>No edges: every item is a root and may run at once.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> Parallel(int count) =>
        Enumerable.Range(1, Math.Max(0, count))
            .Select(_ => (IReadOnlyList<int>)[])
            .ToList();

    /// <summary>
    /// Items 1..<paramref name="afterItem"/> run in series (the foundation);
    /// every later item waits only for item <paramref name="afterItem"/>
    /// and may run alongside its siblings. This is the "three serial
    /// foundations, then sixty-nine in parallel" shape as one act.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> FanOutAfter(int count, int afterItem)
    {
        count = Math.Max(0, count);
        if (afterItem < 1 || afterItem > count)
        {
            throw new ArgumentOutOfRangeException(nameof(afterItem), "The fan-out root must be an item in the chain.");
        }

        return Enumerable.Range(1, count)
            .Select(n => (IReadOnlyList<int>)(
                n == 1 ? [] : n <= afterItem ? [n - 1] : [afterItem]))
            .ToList();
    }

    /// <summary>
    /// Parses a typed edge list ("1, 3" / "after 2" / "#4 5" / "") for
    /// item <paramref name="self"/> in a chain of <paramref name="count"/>.
    /// Returns the sorted, de-duplicated edges and a problem when a token
    /// is not a number, names a missing item, or names the item itself.
    /// Blank input means no edges — a root.
    /// </summary>
    public static (IReadOnlyList<int> Edges, string? Problem) ParseWaitsFor(string? text, int count, int self)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ([], null);
        }

        var cleaned = LeadWordRegex().Replace(text.Trim(), string.Empty);
        if (cleaned.Length == 0)
        {
            return ([], null);
        }

        var edges = new SortedSet<int>();
        foreach (var token in cleaned.Split([',', ' ', ';', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim().TrimStart('#');
            if (t.Length == 0)
            {
                continue;
            }

            if (!int.TryParse(t, out var n))
            {
                return ([], $"“{token}” is not an item number.");
            }

            if (n < 1 || n > count)
            {
                return ([], $"There is no item {n} in this chain.");
            }

            if (n == self)
            {
                return ([], $"Item {self} cannot wait for itself.");
            }

            edges.Add(n);
        }

        return (edges.ToList(), null);
    }

    /// <summary>Formats edges for display and for the input box: "1, 3".</summary>
    public static string Format(IReadOnlyList<int> edges) =>
        string.Join(", ", edges.Distinct().OrderBy(n => n));

    /// <summary>The items with no in-chain edges: where existing-item dependencies attach.</summary>
    public static IReadOnlyList<int> Roots(IReadOnlyList<IReadOnlyList<int>> edgesByItem) =>
        Enumerable.Range(1, edgesByItem.Count)
            .Where(n => edgesByItem[n - 1].Count == 0)
            .ToList();

    /// <summary>
    /// Renumbers edges after item <paramref name="removed"/> (1-based) is
    /// deleted. Items that waited for it inherit what it waited for — a
    /// series stays a series with one link spliced out — and edges past it
    /// shift down by one.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> Remove(IReadOnlyList<IReadOnlyList<int>> edgesByItem, int removed)
    {
        if (removed < 1 || removed > edgesByItem.Count)
        {
            return edgesByItem;
        }

        var inherited = edgesByItem[removed - 1].Where(d => d != removed).ToList();
        var result = new List<IReadOnlyList<int>>(edgesByItem.Count - 1);
        for (var i = 0; i < edgesByItem.Count; i++)
        {
            if (i == removed - 1)
            {
                continue;
            }

            var own = edgesByItem[i];
            var spliced = own.Contains(removed) ? own.Concat(inherited) : own;
            result.Add(spliced
                .Where(d => d != removed && d != i + 1)
                .Select(d => d > removed ? d - 1 : d)
                .Distinct()
                .OrderBy(d => d)
                .ToList());
        }

        return result;
    }

    /// <summary>
    /// The items on a dependency cycle, empty when the graph is acyclic.
    /// Cycles are the one shape the orchestrator would accept edge by edge
    /// and then never dispatch, so they are caught here, before filing.
    /// </summary>
    public static IReadOnlyList<int> CycleMembers(IReadOnlyList<IReadOnlyList<int>> edgesByItem)
    {
        var count = edgesByItem.Count;
        var state = new int[count + 1]; // 0 = unvisited, 1 = on stack, 2 = done
        var onCycle = new SortedSet<int>();

        for (var start = 1; start <= count; start++)
        {
            if (state[start] != 0)
            {
                continue;
            }

            var path = new List<int>();
            Visit(start);

            void Visit(int n)
            {
                state[n] = 1;
                path.Add(n);
                foreach (var dep in edgesByItem[n - 1])
                {
                    if (dep < 1 || dep > count)
                    {
                        continue;
                    }

                    if (state[dep] == 1)
                    {
                        var from = path.IndexOf(dep);
                        for (var i = from; i < path.Count; i++)
                        {
                            onCycle.Add(path[i]);
                        }
                    }
                    else if (state[dep] == 0)
                    {
                        Visit(dep);
                    }
                }

                path.RemoveAt(path.Count - 1);
                state[n] = 2;
            }
        }

        return onCycle.ToList();
    }

    /// <summary>
    /// A one-line reading of the whole shape: "1 → 2 → 3", "all parallel",
    /// "1 → 2 → 3, then 4–9 after 3", or "custom" when nothing simpler
    /// fits. Used in the review sentence so the operator reads the chain
    /// as words before filing it.
    /// </summary>
    public static string Describe(IReadOnlyList<IReadOnlyList<int>> edgesByItem)
    {
        var count = edgesByItem.Count;
        if (count <= 1)
        {
            return count == 0 ? "nothing" : "one item";
        }

        if (edgesByItem.All(e => e.Count == 0))
        {
            return $"all {count} in parallel";
        }

        if (IsSeries(edgesByItem, count))
        {
            return count <= 6
                ? string.Join(" → ", Enumerable.Range(1, count))
                : $"1 → 2 → … → {count} in series";
        }

        for (var k = 1; k < count; k++)
        {
            if (MatchesFanOut(edgesByItem, k))
            {
                var head = k == 1 ? "1" : string.Join(" → ", Enumerable.Range(1, k));
                var tail = k + 1 == count ? $"{count}" : $"{k + 1}–{count}";
                return $"{head}, then {tail} in parallel after {k}";
            }
        }

        return $"{count} items, custom edges";
    }

    private static bool IsSeries(IReadOnlyList<IReadOnlyList<int>> edges, int count)
    {
        for (var n = 1; n <= count; n++)
        {
            var e = edges[n - 1];
            if (n == 1 ? e.Count != 0 : e.Count != 1 || e[0] != n - 1)
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesFanOut(IReadOnlyList<IReadOnlyList<int>> edges, int k)
    {
        var expected = FanOutAfter(edges.Count, k);
        for (var i = 0; i < edges.Count; i++)
        {
            if (!edges[i].OrderBy(d => d).SequenceEqual(expected[i]))
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex(@"^(?:waits?\s+for|after|depends?\s+on)\s*:?\s*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LeadWordRegex();
}
