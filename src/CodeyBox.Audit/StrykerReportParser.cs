using System.Text.Json;

namespace CodeyBox.Audit;

/// <summary>
/// One mutant from a Stryker JSON report, with its report-local file key.
/// </summary>
public sealed record StrykerParsedMutant(
    string FileKey,
    string Mutator,
    string Replacement,
    int Line,
    string Status,
    int CoveredBy);

/// <summary>
/// Aggregated counts and the computed score of one Stryker JSON report.
/// Score formula (matching Stryker's own "final mutation score": detected /
/// scorable): <c>(Killed + Timeout) / (Killed + Survived + Timeout +
/// NoCoverage) * 100</c>, or null when the report holds no scorable mutants.
/// Ignored, compile-error, runtime-error, and unknown statuses are excluded
/// from the denominator and counted separately so a broken mutant never
/// inflates or deflates the score.
/// </summary>
public sealed record StrykerParsedReport(
    IReadOnlyList<StrykerParsedMutant> Mutants,
    IReadOnlyList<string> Files,
    int Killed,
    int Survived,
    int Timeout,
    int NoCoverage,
    int Ignored,
    int Errored,
    int SchemaVersion,
    string? ReportProjectRoot,
    double? ScorePercent);

/// <summary>Outcome of <see cref="StrykerReportParser.TryParse"/>.</summary>
public sealed record StrykerParseResult(
    bool Success,
    StrykerParsedReport? Report,
    string? Error);

/// <summary>
/// Parses the machine-readable Stryker JSON report
/// (<c>mutation-report.json</c>) into killed/surviving/uncovered/timeout/
/// error buckets. Pure function of the report text: no I/O, no ambient
/// state, bounded work. Rejects missing/invalid/truncated payloads with an
/// explicit reason instead of inferring scores from exit codes or console
/// wording.
/// </summary>
public static class StrykerReportParser
{
    /// <summary>Maximum mutants parsed from one report; bounds CPU and memory.</summary>
    public const int MaxMutants = 200_000;

    /// <summary>Maximum accepted length of one report file key.</summary>
    public const int MaxFileKeyLength = 4096;

    /// <summary>Maximum accepted length of one mutator/replacement string.</summary>
    public const int MaxTokenLength = 4096;

    /// <summary>
    /// Statuses Stryker uses for detected mutants. Timeouts count as detected:
    /// the test run had to be cancelled, so the suite observed the mutation.
    /// </summary>
    public static readonly IReadOnlySet<string> DetectedStatuses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Killed", "Timeout" };

    /// <summary>Statuses Stryker uses for undetected mutants.</summary>
    public static readonly IReadOnlySet<string> UndetectedStatuses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Survived", "NoCoverage" };

    /// <summary>Statuses excluded from scoring (never killed nor survived).</summary>
    public static readonly IReadOnlySet<string> ExcludedStatuses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Ignored", "CompileError", "RuntimeError",
        };

    /// <summary>
    /// Parses <paramref name="json"/> as a Stryker JSON report. Never throws
    /// on malformed input: failures return <c>Success: false</c> with a
    /// bounded, operator-actionable reason (the raw payload is never echoed
    /// — it can be megabytes of tool output).
    /// </summary>
    public static StrykerParseResult TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Fail("report is empty or missing.");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return Fail($"report is not valid JSON: {StrykerPaths.SanitizeForLog(ex.Message, 160)}");
        }
        using (document)
        {
            return ParseDocument(document.RootElement);
        }
    }

    private static StrykerParseResult ParseDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return Fail("report root must be a JSON object.");
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
            return Fail("report has no 'files' object (not a Stryker JSON report).");
        var schemaVersion = 0;
        if (root.TryGetProperty("schemaVersion", out var schema)
            && schema.ValueKind == JsonValueKind.Number
            && schema.TryGetInt32(out var parsed))
            schemaVersion = parsed;
        string? reportProjectRoot = null;
        if (root.TryGetProperty("projectRoot", out var projectRoot)
            && projectRoot.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(projectRoot.GetString())
            && projectRoot.GetString()!.Length <= MaxFileKeyLength)
            reportProjectRoot = projectRoot.GetString();

        var mutants = new List<StrykerParsedMutant>();
        var fileKeys = new List<string>();
        var killed = 0;
        var survived = 0;
        var timeout = 0;
        var noCoverage = 0;
        var ignored = 0;
        var errored = 0;

        foreach (var file in files.EnumerateObject())
        {
            var key = file.Name;
            if (string.IsNullOrWhiteSpace(key) || key.Length > MaxFileKeyLength)
                return Fail("report contains an empty or over-long file key.");
            if (file.Value.ValueKind != JsonValueKind.Object)
                return Fail($"report entry for '{StrykerPaths.SanitizeForLog(key)}' is not an object.");
            fileKeys.Add(key);
            if (!file.Value.TryGetProperty("mutants", out var items)
                || items.ValueKind != JsonValueKind.Array)
                return Fail($"report entry for '{StrykerPaths.SanitizeForLog(key)}' has no 'mutants' array.");
            foreach (var item in items.EnumerateArray())
            {
                if (mutants.Count >= MaxMutants)
                    return Fail($"report exceeds the {MaxMutants} mutant cap.");
                if (!TryParseMutant(key, item, out var mutant))
                    return Fail($"report contains a malformed mutant under '{StrykerPaths.SanitizeForLog(key)}'.");
                mutants.Add(mutant);
                if (DetectedStatuses.Contains(mutant.Status))
                {
                    killed += mutant.Status.Equals("Killed", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                    timeout += mutant.Status.Equals("Timeout", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                }
                else if (mutant.Status.Equals("Survived", StringComparison.OrdinalIgnoreCase))
                {
                    survived++;
                }
                else if (mutant.Status.Equals("NoCoverage", StringComparison.OrdinalIgnoreCase))
                {
                    noCoverage++;
                }
                else if (ExcludedStatuses.Contains(mutant.Status))
                {
                    if (mutant.Status.Equals("Ignored", StringComparison.OrdinalIgnoreCase))
                        ignored++;
                    else
                        errored++;
                }
                else
                {
                    errored++;
                }
            }
        }

        var valid = killed + survived + timeout + noCoverage;
        double? score = valid > 0 ? 100.0 * (killed + timeout) / valid : null;
        return new StrykerParseResult(true,
            new StrykerParsedReport(
                mutants, fileKeys, killed, survived, timeout, noCoverage, ignored, errored,
                schemaVersion, reportProjectRoot, score),
            Error: null);
    }

    private static bool TryParseMutant(string fileKey, JsonElement item, out StrykerParsedMutant mutant)
    {
        mutant = null!;
        if (item.ValueKind != JsonValueKind.Object)
            return false;
        if (!item.TryGetProperty("mutatorName", out var mutator)
            || mutator.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(mutator.GetString())
            || mutator.GetString()!.Length > MaxTokenLength
            || ContainsControl(mutator.GetString()!))
            return false;
        if (!item.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(status.GetString())
            || status.GetString()!.Length > 64
            || ContainsControl(status.GetString()!))
            return false;
        if (!item.TryGetProperty("location", out var location)
            || location.ValueKind != JsonValueKind.Object
            || !location.TryGetProperty("start", out var start)
            || start.ValueKind != JsonValueKind.Object
            || !start.TryGetProperty("line", out var line)
            || line.ValueKind != JsonValueKind.Number
            || !line.TryGetInt32(out var lineNumber)
            || lineNumber < 1)
            return false;
        var replacement = "";
        if (item.TryGetProperty("replacement", out var replacementElement)
            && replacementElement.ValueKind == JsonValueKind.String)
            replacement = replacementElement.GetString() ?? "";
        if (replacement.Length > MaxTokenLength)
            replacement = replacement[..MaxTokenLength];
        // Report strings echo attacker-influenceable repo content (mutated
        // source text) and reach findings/RawOutput, hence the rework prompt:
        // reject control characters (newlines, ANSI escapes) rather than
        // embedding them. The report is then reported as malformed, never
        // silently trimmed into a passing score.
        if (ContainsControl(replacement))
            return false;
        var coveredBy = 0;
        if (item.TryGetProperty("coveredBy", out var coveredByElement)
            && coveredByElement.ValueKind == JsonValueKind.Array)
            coveredBy = coveredByElement.GetArrayLength();
        mutant = new StrykerParsedMutant(
            fileKey, mutator.GetString()!, replacement, lineNumber, status.GetString()!, coveredBy);
        return true;
    }

    private static StrykerParseResult Fail(string error) =>
        new(false, Report: null, Error: error);

    private static bool ContainsControl(string value)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c))
                return true;
        }
        return false;
    }
}
