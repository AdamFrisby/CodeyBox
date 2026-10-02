using CodeyBox.Audit;

namespace CodeyBox.TestSelectionProducer;

/// <summary>
/// Maps a parsed Cobertura report onto the per-test <c>covers</c> shape: only
/// executable lines with a positive hit count, keyed by
/// <see cref="CoberturaParser.ToRepositoryRelative"/> so the producer and the
/// <c>tests:coverage</c> gate share one path-normalisation function.
/// </summary>
public static class TestSelectionCoverageMap
{
    public static IReadOnlyDictionary<string, IReadOnlyList<int>> FromReports(
        IEnumerable<CoberturaReport> reports,
        string repoRoot)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        var extraRoots = EquivalentRepoRoots(repoRoot);
        var canonicalRepo = extraRoots[0];
        var rewritten = new List<CoberturaReport>();
        foreach (var report in reports)
        {
            // Coerce coverlet's missing-slash Unix paths to absolute so
            // CoberturaParser.ToRepositoryRelative (called inside BuildLineMap)
            // can strip the repo root. Do not strip here — that function is
            // the single source of truth for path normalisation.
            var sources = extraRoots
                .Concat(report.Sources)
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var files = new List<CoberturaFile>(report.Files.Count);
            foreach (var file in report.Files)
            {
                var coerced = CoerceAbsolute(file.FileName, sources);
                files.Add(new CoberturaFile(coerced, file.LineHits));
            }

            rewritten.Add(new CoberturaReport(sources, files));
        }

        var merged = CoberturaParser.BuildLineMap(rewritten, canonicalRepo);
        var covers = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        foreach (var (file, lineHits) in merged)
        {
            var lines = new SortedSet<int>();
            foreach (var (line, hits) in lineHits)
            {
                if (line >= 1 && hits > 0)
                    lines.Add(line);
            }

            if (lines.Count > 0)
                covers[file] = [.. lines];
        }

        return covers;
    }

    /// <summary>
    /// Normalises a PDB / filesystem path the same way the coverage gate keys
    /// Cobertura filenames: <see cref="CoberturaParser.ToRepositoryRelative"/>.
    /// </summary>
    public static string ToRepositoryRelative(string path, string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        var extraRoots = EquivalentRepoRoots(repoRoot);
        var coerced = CoerceAbsolute(path, extraRoots);
        return CoberturaParser.ToRepositoryRelative(coerced, extraRoots, extraRoots[0]);
    }

    /// <summary>
    /// Coverlet sometimes emits an absolute Unix path with the leading slash
    /// stripped (<c>tmp/foo/src/Bar.cs</c> instead of <c>/tmp/foo/src/Bar.cs</c>).
    /// Restore the slash so <see cref="CoberturaParser.ToRepositoryRelative"/>
    /// can strip the repo root; relative paths that are already repo-relative
    /// are left alone.
    /// </summary>
    internal static string CoerceAbsolute(string filename, IReadOnlyList<string> roots)
    {
        ArgumentNullException.ThrowIfNull(filename);
        ArgumentNullException.ThrowIfNull(roots);
        var normalized = filename.Replace('\\', '/').Trim();
        if (normalized.StartsWith('/')
            || (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':'))
        {
            return normalized;
        }

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            var trimmed = root.Replace('\\', '/').Trim().TrimStart('/');
            if (trimmed.Length > 0
                && normalized.StartsWith(trimmed + "/", StringComparison.Ordinal))
            {
                return "/" + normalized;
            }
        }

        return normalized;
    }

    internal static IReadOnlyList<string> EquivalentRepoRoots(string repoRoot)
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            var full = Path.GetFullPath(path);
            if (!roots.Exists(existing => string.Equals(existing, full, StringComparison.Ordinal)))
                roots.Add(full);
        }

        Add(repoRoot);
        try
        {
            var resolved = Directory.ResolveLinkTarget(Path.GetFullPath(repoRoot), returnFinalTarget: true);
            if (resolved is not null)
                Add(resolved.FullName);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return roots;
    }
}
