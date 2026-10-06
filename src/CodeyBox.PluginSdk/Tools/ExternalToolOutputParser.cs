using System.Text.Json;

namespace CodeyBox.PluginSdk.Tools;

/// <summary>
/// A single tool-reported problem, still carrying the tool's own severity
/// vocabulary. <see cref="ExternalToolAuditorBase"/> applies the author's
/// <see cref="ExternalToolSeverityMapping"/> when converting these to
/// <c>AuditFinding</c> records, so raw tool levels never reach the audit
/// report.
/// </summary>
public sealed record ExternalToolFinding(
    /// <summary>Tool severity as reported (e.g. <c>"high"</c>, <c>"note"</c>). May be null.</summary>
    string? SeverityLevel,
    /// <summary>Rule identifier from the tool (SARIF <c>ruleId</c>). May be null when the tool supplies none.</summary>
    string? RuleId,
    /// <summary>Human-readable problem description from the tool.</summary>
    string Message,
    /// <summary>Repository-relative file path as reported by the tool. May be null.</summary>
    string? Path = null,
    /// <summary>1-based line number when the tool supplies one. Null otherwise.</summary>
    int? Line = null);

/// <summary>
/// Inputs handed to an <see cref="IExternalToolOutputParser"/>.
/// </summary>
/// <param name="ScanRoot">The absolute directory the tool's scan ran
/// in as the tool saw it — sandbox providers may translate the audit's
/// working directory, so auditors that need it resolve it per run through
/// <see cref="ExternalToolAuditorBase.ResolveScanRootAsync"/>. Parsers use
/// it to relativize absolute paths the tool reports when the report itself
/// embeds no working directory. Null when the auditor did not resolve one —
/// parsers must then leave absolute paths unrelativized rather than guess.
/// </param>
/// <param name="WorkingDirectory">
/// The directory the tool ran in (the exec working directory). Tools that
/// report absolute paths (e.g. PHPStan's JSON report keys <c>files</c> by
/// absolute path) use it to relativize them onto repository-relative paths so
/// finding locations and <see cref="ExternalToolAuditorOptions.ExcludePaths"/>
/// behave like every other auditor's. Null when the caller did not supply one.
/// </param>
/// <param name="ExpectedVerdictCount">
/// Optional lower bound on the number of per-file verdict records the report
/// must contain — for per-translation-unit tools, the count of units the
/// auditor selected for analysis. Drivers whose exit aggregation can hide a
/// unit that produced no verdict at all (e.g. iwyu_tool folds a
/// signal-killed child's negative returncode into exit 0) need this
/// reconciliation: fewer verdicts than selected units means coverage is
/// unverifiable, and the parser fails closed rather than reporting a silent
/// partial pass. Null — the default — means no reconciliation.
/// </param>
/// <param name="RelativePathAnchors">
/// Optional map resolving a relative path spelling in the report to its
/// repository-relative location — for tools that run each unit with a
/// different working directory and report paths as spelled in that unit's
/// invocation (iwyu_tool runs each compile command with
/// <c>cwd = entry.directory</c>). The key is the dot-segment-collapsed
/// relative spelling (e.g. <c>../src/a.cc</c>), the value the
/// repository-relative path it resolves to. Parsers consult it only for
/// reported paths that are relative; absolute paths and unmapped spellings
/// keep the standard relativize-then-<c>file://</c> policy.
/// </param>
public sealed record ExternalToolParseInput(
    string ToolName,
    string Stdout,
    string Stderr,
    int ExitCode,
    string? ScanRoot = null,
    string? WorkingDirectory = null,
    int? ExpectedVerdictCount = null,
    IReadOnlyDictionary<string, string>? RelativePathAnchors = null);

/// <summary>
/// Parses a finished tool invocation into findings. The tool does not need to
/// emit SARIF — implement this interface for line-based or JSON output.
/// Throw <see cref="ExternalToolParseException"/> when the output is not
/// recognizable: the base reports that as infrastructure (the check could not
/// run), never as a pass.
/// </summary>
public interface IExternalToolOutputParser
{
    IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input);
}

/// <summary>
/// Typed parse failure. Carries no tool output beyond the message so stack
/// traces and raw dumps never flow into findings.
/// </summary>
public sealed class ExternalToolParseException : Exception
{
    public ExternalToolParseException(string message)
        : base(message) { }

    public ExternalToolParseException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Bounds shared by every <see cref="IExternalToolOutputParser"/>
/// implementation, independent of the tool's report format.
/// </summary>
public static class ExternalToolReportLimits
{
    /// <summary>
    /// Default upper bound on results consumed from one report document —
    /// the format-neutral cap every parser starts from so one pathological
    /// report cannot grow findings without limit.
    /// </summary>
    public const int DefaultMaxResults = 10_000;
}

/// <summary>
/// Parser adapter for tools with simple output: supply a function, get an
/// <see cref="IExternalToolOutputParser"/>.
/// </summary>
public sealed class DelegateToolOutputParser : IExternalToolOutputParser
{
    private readonly Func<ExternalToolParseInput, IReadOnlyList<ExternalToolFinding>> _parse;

    public DelegateToolOutputParser(Func<ExternalToolParseInput, IReadOnlyList<ExternalToolFinding>> parse)
        => _parse = parse ?? throw new ArgumentNullException(nameof(parse));

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return _parse(input) ?? [];
    }
}

/// <summary>
/// First-class SARIF parser (<c>runs[].results[]</c> with <c>ruleId</c>,
/// <c>level</c>, <c>message.text</c>, and the first physical location's
/// artifact URI plus <c>region.startLine</c>). Reads from stdout; a non-empty
/// stderr is kept for the audit raw output but does not affect parsing.
/// Malformed JSON throws <see cref="ExternalToolParseException"/>.
/// </summary>
public sealed class SarifToolOutputParser : IExternalToolOutputParser
{
    /// <summary>Upper bound on results consumed from one document.</summary>
    public const int DefaultMaxResults = ExternalToolReportLimits.DefaultMaxResults;

    private readonly int _maxResults;

    public SarifToolOutputParser(int maxResults = DefaultMaxResults)
    {
        if (maxResults <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxResults), "Maximum SARIF results must be positive.");
        _maxResults = maxResults;
    }

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stdout))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no SARIF output on stdout.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.Stdout);
        }
        catch (JsonException ex)
        {
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced output that is not valid SARIF JSON: {ToolOutputText.SingleLine(ex.Message)}.",
                ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("runs"u8, out var runs)
                || runs.ValueKind != JsonValueKind.Array)
                throw new ExternalToolParseException(
                    $"Tool '{input.ToolName}' produced JSON without a SARIF 'runs' array.");

            var findings = new List<ExternalToolFinding>();
            foreach (var run in runs.EnumerateArray())
            {
                if (findings.Count >= _maxResults)
                    break;
                if (run.ValueKind != JsonValueKind.Object
                    || !run.TryGetProperty("results"u8, out var results)
                    || results.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var result in results.EnumerateArray())
                {
                    if (findings.Count >= _maxResults)
                        break;
                    if (result.ValueKind == JsonValueKind.Object)
                        findings.Add(ParseResult(result));
                }
            }

            return findings;
        }
    }

    private static ExternalToolFinding ParseResult(JsonElement result)
    {
        string? ruleId = null;
        if (result.TryGetProperty("ruleId"u8, out var ruleIdElement))
            ruleId = CoerceString(ruleIdElement);
        if (string.IsNullOrWhiteSpace(ruleId)
            && result.TryGetProperty("rule"u8, out var rule)
            && rule.ValueKind == JsonValueKind.Object
            && rule.TryGetProperty("id"u8, out var ruleInnerId))
            ruleId = CoerceString(ruleInnerId);

        // SARIF defaults an absent level to "warning".
        var level = CoerceString(GetProperty(result, "level"u8)) ?? "warning";

        var message = "(no message)";
        if (result.TryGetProperty("message"u8, out var messageElement)
            && messageElement.ValueKind == JsonValueKind.Object)
        {
            message = CoerceString(GetProperty(messageElement, "text"u8))
                ?? CoerceString(GetProperty(messageElement, "markdown"u8))
                ?? message;
        }

        string? path = null;
        int? line = null;
        var location = FirstLocation(result);
        if (location.HasValue)
        {
            var physical = GetProperty(location.Value, "physicalLocation"u8);
            if (physical.ValueKind == JsonValueKind.Object)
            {
                var artifact = GetProperty(physical, "artifactLocation"u8);
                if (artifact.ValueKind == JsonValueKind.Object)
                    path = NormalizeArtifactUri(CoerceString(GetProperty(artifact, "uri"u8)));
                var region = GetProperty(physical, "region"u8);
                if (region.ValueKind == JsonValueKind.Object
                    && region.TryGetProperty("startLine"u8, out var startLine)
                    && startLine.ValueKind == JsonValueKind.Number
                    && startLine.TryGetInt32(out var startLineValue)
                    && startLineValue > 0)
                    line = startLineValue;
            }
        }

        return new ExternalToolFinding(
            SeverityLevel: string.IsNullOrWhiteSpace(level) ? null : level,
            RuleId: string.IsNullOrWhiteSpace(ruleId) ? null : ruleId,
            Message: string.IsNullOrWhiteSpace(message) ? "(no message)" : message,
            Path: string.IsNullOrWhiteSpace(path) ? null : path,
            Line: line);
    }

    private static JsonElement? FirstLocation(JsonElement result)
    {
        if (!result.TryGetProperty("locations"u8, out var locations)
            || locations.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var location in locations.EnumerateArray())
        {
            if (location.ValueKind == JsonValueKind.Object)
                return location;
        }

        return null;
    }

    private static JsonElement GetProperty(JsonElement element, ReadOnlySpan<byte> name)
        => element.TryGetProperty(name, out var value) ? value : default;

    private static string? CoerceString(JsonElement element)
        => ExternalToolJsonHelpers.CoerceString(element);

    private static string? NormalizeArtifactUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;
        var trimmed = uri.Trim();
        if (trimmed.StartsWith(
                ExternalToolJsonHelpers.FileSchemePrefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[ExternalToolJsonHelpers.FileSchemePrefix.Length..].TrimStart('/');
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
