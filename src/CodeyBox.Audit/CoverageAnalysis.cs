using System.Xml;
using System.Xml.Linq;

namespace CodeyBox.Audit;

/// <summary>
/// Per-source-file executable-line hit counts parsed from a single Cobertura
/// XML document (as produced by coverlet's <c>XPlat Code Coverage</c> collector).
/// Only lines the tool considered executable appear here; comments, blank lines,
/// and braces are absent — which is exactly the "executable line" definition the
/// coverage gate needs.
/// </summary>
public sealed record CoberturaReport(
    IReadOnlyList<string> Sources,
    IReadOnlyList<CoberturaFile> Files);

/// <summary>Executable line → hit count for one source file.</summary>
public sealed record CoberturaFile(string FileName, IReadOnlyDictionary<int, int> LineHits);

/// <summary>
/// Pure, XXE-safe parser for Cobertura coverage XML plus the normalisation that
/// maps tool-emitted source paths onto repository-relative paths so they can be
/// intersected with a git diff.
/// </summary>
public static class CoberturaParser
{
    /// <summary>
    /// Parses one Cobertura document. Untrusted input (it is produced inside the
    /// sandbox from repository code), so external entity resolution and DTD
    /// processing are disabled. Multiple <c>&lt;class&gt;</c> elements sharing a
    /// filename (partial classes, several classes per file) are merged, taking
    /// the maximum hit count seen for each line.
    /// </summary>
    /// <exception cref="System.Xml.XmlException">The input is not well-formed XML.</exception>
    public static CoberturaReport Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
        };

        XDocument doc;
        using (var stringReader = new StringReader(xml))
        using (var reader = XmlReader.Create(stringReader, settings))
        {
            doc = XDocument.Load(reader);
        }

        var root = doc.Root;
        if (root is null)
            return new CoberturaReport([], []);

        var sources = root
            .Elements("sources")
            .Elements("source")
            .Select(s => s.Value.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        // Accumulate per filename so partial classes / multiple classes in one
        // file merge into a single line map (max hits wins — a line covered by
        // any class is covered).
        var perFile = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        foreach (var cls in root.Descendants("class"))
        {
            var filename = (string?)cls.Attribute("filename");
            if (string.IsNullOrWhiteSpace(filename))
                continue;

            if (!perFile.TryGetValue(filename, out var lineHits))
                perFile[filename] = lineHits = new Dictionary<int, int>();

            foreach (var lineEl in cls.Elements("lines").Elements("line"))
            {
                if (!TryReadInt(lineEl.Attribute("number"), out var number))
                    continue;
                TryReadInt(lineEl.Attribute("hits"), out var hits);
                lineHits[number] = lineHits.TryGetValue(number, out var existing)
                    ? Math.Max(existing, hits)
                    : hits;
            }
        }

        var files = perFile
            .Select(kvp => new CoberturaFile(kvp.Key, kvp.Value))
            .ToList();

        return new CoberturaReport(sources, files);
    }

    /// <summary>
    /// Merges one or more parsed reports (coverlet emits one per test assembly)
    /// into a repository-relative <c>file → (line → hits)</c> map. Hit counts are
    /// combined with <c>max</c> so a line executed by any assembly's tests counts
    /// as covered. Source paths are made relative to <paramref name="repoRoot"/>
    /// (falling back to the report's own <c>&lt;source&gt;</c> roots) so they line
    /// up with git's repository-relative diff paths.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> BuildLineMap(
        IEnumerable<CoberturaReport> reports,
        string repoRoot)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(repoRoot);

        var merged = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        foreach (var report in reports)
        {
            foreach (var file in report.Files)
            {
                var key = ToRepositoryRelative(file.FileName, report.Sources, repoRoot);
                if (!merged.TryGetValue(key, out var lineHits))
                    merged[key] = lineHits = new Dictionary<int, int>();

                foreach (var (line, hits) in file.LineHits)
                {
                    lineHits[line] = lineHits.TryGetValue(line, out var existing)
                        ? Math.Max(existing, hits)
                        : hits;
                }
            }
        }

        return merged.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyDictionary<int, int>)kvp.Value,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Normalises a Cobertura <c>filename</c> to a repository-relative,
    /// forward-slash path. Absolute filenames have the repo root (or a report
    /// source root) stripped; relative filenames are assumed already relative to
    /// the source root (coverlet's default) and are returned as-is. Deterministic:
    /// roots are tried repo-root-first, then in source declaration order.
    /// </summary>
    public static string ToRepositoryRelative(
        string filename,
        IReadOnlyList<string> sources,
        string repoRoot)
    {
        var normalized = filename.Replace('\\', '/').Trim();

        if (!IsAbsolute(normalized))
            return normalized.TrimStart('/');

        foreach (var root in Roots(repoRoot, sources))
        {
            var prefix = root.Replace('\\', '/').TrimEnd('/') + "/";
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
                return normalized[prefix.Length..];
        }

        // Absolute path under no known root: strip the leading slash so it is at
        // least shaped like a relative path (it simply will not match a diff key).
        return normalized.TrimStart('/');
    }

    private static IEnumerable<string> Roots(string repoRoot, IReadOnlyList<string> sources)
    {
        if (!string.IsNullOrWhiteSpace(repoRoot))
            yield return repoRoot;
        foreach (var source in sources)
        {
            if (!string.IsNullOrWhiteSpace(source))
                yield return source;
        }
    }

    private static bool IsAbsolute(string path)
        => path.StartsWith('/') || (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':');

    private static bool TryReadInt(XAttribute? attribute, out int value)
    {
        value = 0;
        return attribute is not null
            && int.TryParse(attribute.Value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>
/// A justified exemption of one or more changed lines from the coverage gate.
/// Config-binding shape (mutable) so operators can list untestable changed lines
/// (e.g. generated code) under <c>CodeyBox:Audit:Coverage:Exclusions</c>. An
/// exclusion without a <see cref="Justification"/> is invalid and does NOT
/// suppress — the honesty rule forbids silent skips.
/// </summary>
public sealed class CoverageExclusion
{
    /// <summary>Repository-relative path (matches git diff / coverage paths).</summary>
    public string File { get; set; } = "";

    /// <summary>First line covered by the exclusion (1-based, inclusive).</summary>
    public int Line { get; set; }

    /// <summary>
    /// Last line covered (1-based, inclusive). When null or below
    /// <see cref="Line"/>, the exclusion covers the single <see cref="Line"/>.
    /// </summary>
    public int? LineEnd { get; set; }

    /// <summary>Why this changed code is genuinely untestable. Required.</summary>
    public string Justification { get; set; } = "";

    /// <summary>Effective inclusive end line.</summary>
    public int EffectiveLineEnd => LineEnd is int end && end >= Line ? end : Line;

    /// <summary>An exclusion only suppresses when it carries a justification.</summary>
    public bool HasJustification => !string.IsNullOrWhiteSpace(Justification);

    public bool Covers(string file, int line)
        => string.Equals(File, file, StringComparison.Ordinal)
           && line >= Line
           && line <= EffectiveLineEnd;
}

/// <summary>An executable changed line no test exercised.</summary>
public sealed record UncoveredChangedLine(string File, int Line);

/// <summary>A changed uncovered line that a justified exclusion suppressed.</summary>
public sealed record AppliedCoverageExclusion(string File, int Line, string Justification);

/// <summary>
/// Outcome of intersecting the changed-line set with the coverage report. All
/// lists are sorted deterministically (by file then line) so the same
/// (diff, coverage, exclusions) input always yields the identical finding list.
/// </summary>
public sealed record CoverageGateResult(
    IReadOnlyList<UncoveredChangedLine> UncoveredLines,
    IReadOnlyList<AppliedCoverageExclusion> AppliedExclusions,
    IReadOnlyList<CoverageExclusion> UnusedExclusions,
    IReadOnlyList<CoverageExclusion> InvalidExclusions);

/// <summary>
/// Pure core of the coverage gate: given the lines a work item changed, the
/// coverage report, and the operator's exclusions, computes which changed
/// executable lines are uncovered. A changed line gates only when it appears in
/// the coverage report (i.e. the tool considered it executable) with zero hits;
/// changed non-executable lines and unchanged uncovered lines are ignored.
/// </summary>
public static class CoverageGateEvaluator
{
    public static CoverageGateResult Evaluate(
        IReadOnlyDictionary<string, IReadOnlySet<int>> changedLinesByFile,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> coverageByFile,
        IReadOnlyList<CoverageExclusion> exclusions)
    {
        ArgumentNullException.ThrowIfNull(changedLinesByFile);
        ArgumentNullException.ThrowIfNull(coverageByFile);
        ArgumentNullException.ThrowIfNull(exclusions);

        var valid = exclusions.Where(e => e.HasJustification).ToList();
        var invalid = exclusions.Where(e => !e.HasJustification).ToList();

        var uncovered = new List<UncoveredChangedLine>();
        var applied = new List<AppliedCoverageExclusion>();
        var usedExclusions = new HashSet<CoverageExclusion>(ReferenceEqualityComparer.Instance);

        foreach (var (file, changedLines) in changedLinesByFile)
        {
            if (!coverageByFile.TryGetValue(file, out var lineHits))
                continue; // No executable coverage lines for this file → out of scope.

            foreach (var line in changedLines)
            {
                // Only executable lines (present in the report) with zero hits gate.
                if (!lineHits.TryGetValue(line, out var hits) || hits > 0)
                    continue;

                // A valid exclusion suppresses (and is logged); an invalid one
                // (no justification) is intentionally NOT honoured here so the
                // line still gates and the missing justification is surfaced.
                var match = valid.FirstOrDefault(e => e.Covers(file, line));
                if (match is not null)
                {
                    applied.Add(new AppliedCoverageExclusion(file, line, match.Justification));
                    usedExclusions.Add(match);
                    continue;
                }

                uncovered.Add(new UncoveredChangedLine(file, line));
            }
        }

        var unused = valid.Where(e => !usedExclusions.Contains(e)).ToList();

        return new CoverageGateResult(
            Sort(uncovered, u => u.File, u => u.Line),
            Sort(applied, a => a.File, a => a.Line),
            unused,
            invalid);
    }

    private static IReadOnlyList<T> Sort<T>(
        IEnumerable<T> items,
        Func<T, string> file,
        Func<T, int> line)
        => items
            .OrderBy(file, StringComparer.Ordinal)
            .ThenBy(line)
            .ToList();
}
