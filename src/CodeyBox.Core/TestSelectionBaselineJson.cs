using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeyBox.Core;

/// <summary>
/// Writer for the <see cref="TestSelectionBaseline"/> JSON artifact. Emits the
/// exact property names the strict reader
/// (<see cref="TestSelectionBaselineParser"/>) requires. Size caps are applied
/// to the fully assembled document BEFORE any destination file is created or
/// overwritten — a cap miss fails loudly and never writes a truncated baseline.
/// </summary>
public static class TestSelectionBaselineJson
{
    /// <summary>
    /// Serialises <paramref name="baseline"/> and enforces
    /// <paramref name="limits"/> against the in-memory model and the resulting
    /// JSON character length (the same metric
    /// <see cref="TestSelectionBaselineParser.Parse"/> uses for
    /// <see cref="BaselineReadLimits.MaxBytes"/>).
    /// </summary>
    /// <exception cref="FormatException">A size, test, or covered-line cap would be exceeded.</exception>
    public static string Serialize(TestSelectionBaseline baseline, BaselineReadLimits limits)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "MaxBytes must be positive.");
        if (limits.MaxTests <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "MaxTests must be positive.");
        if (limits.MaxCoveredLines <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "MaxCoveredLines must be positive.");

        if (baseline.Tests.Count > limits.MaxTests)
        {
            throw new FormatException(string.Create(
                CultureInfo.InvariantCulture,
                $"Baseline exceeds the test cap ({limits.MaxTests} tests)."));
        }

        long coveredLines = 0;
        foreach (var entry in baseline.Tests.Values)
        {
            foreach (var lines in entry.Covers.Values)
            {
                coveredLines += CountPositiveDistinct(lines);
                if (coveredLines > limits.MaxCoveredLines)
                {
                    throw new FormatException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Baseline exceeds the covered-line cap ({limits.MaxCoveredLines} lines)."));
                }
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            WriteDocument(writer, baseline);
        }

        var json = Encoding.UTF8.GetString(stream.ToArray());
        if (json.Length > limits.MaxBytes)
        {
            throw new FormatException(string.Create(
                CultureInfo.InvariantCulture,
                $"Baseline exceeds the size cap ({json.Length} chars > {limits.MaxBytes})."));
        }

        return json;
    }

    /// <summary>
    /// Serialises under <paramref name="limits"/>, then replaces
    /// <paramref name="path"/> via <see cref="AtomicFile"/>'s same-directory
    /// temp file + move. On any cap miss or serialisation failure the
    /// destination is left untouched (and is not created if it did not
    /// exist).
    /// </summary>
    public static void WriteAtomic(string path, TestSelectionBaseline baseline, BaselineReadLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = Serialize(baseline, limits);
        AtomicFile.WriteAllText(path, json);
    }

    private static void WriteDocument(Utf8JsonWriter writer, TestSelectionBaseline baseline)
    {
        writer.WriteStartObject();
        writer.WriteString("format", TestSelectionBaseline.FormatMarker);
        writer.WriteString("commit", baseline.Commit);
        writer.WriteString("producedAtUtc", baseline.ProducedAtUtc.ToUniversalTime());

        writer.WritePropertyName("fileProject");
        WriteStringMap(writer, baseline.ProjectGraph.FileProject);

        writer.WritePropertyName("projects");
        WriteStringListMap(writer, baseline.ProjectGraph.AffectedTestsByProject);

        writer.WritePropertyName("tests");
        writer.WriteStartObject();
        foreach (var name in baseline.Tests.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var entry = baseline.Tests[name];
            writer.WritePropertyName(name);
            writer.WriteStartObject();
            writer.WriteString("file", entry.DefiningFile);
            writer.WritePropertyName("covers");
            writer.WriteStartObject();
            foreach (var file in entry.Covers.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var lines = NormaliseLines(entry.Covers[file]);
                if (lines.Count == 0)
                    continue;
                writer.WritePropertyName(file);
                writer.WriteStartArray();
                foreach (var line in lines)
                    writer.WriteNumberValue(line);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void WriteStringMap(Utf8JsonWriter writer, IReadOnlyDictionary<string, string> map)
    {
        writer.WriteStartObject();
        foreach (var key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
            writer.WriteString(key, map[key]);
        writer.WriteEndObject();
    }

    private static void WriteStringListMap(
        Utf8JsonWriter writer,
        IReadOnlyDictionary<string, IReadOnlyList<string>> map)
    {
        writer.WriteStartObject();
        foreach (var key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            writer.WritePropertyName(key);
            writer.WriteStartArray();
            foreach (var value in map[key].OrderBy(v => v, StringComparer.Ordinal))
                writer.WriteStringValue(value);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private static int CountPositiveDistinct(IReadOnlyList<int> lines)
    {
        var set = new HashSet<int>();
        foreach (var line in lines)
        {
            if (line >= 1)
                set.Add(line);
        }
        return set.Count;
    }

    private static List<int> NormaliseLines(IReadOnlyList<int> lines)
    {
        var set = new SortedSet<int>();
        foreach (var line in lines)
        {
            if (line >= 1)
                set.Add(line);
        }
        return [.. set];
    }
}
