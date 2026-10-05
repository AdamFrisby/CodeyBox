using System.Text;

namespace CodeyBox.Core;

/// <summary>
/// Pure builder for forge check-run payloads from structured audit findings.
/// All decision logic lives here so it is directly unit-testable: conclusion
/// mapping (via <see cref="AuditCheckConclusionMapper"/>), repository-relative
/// path and line-range validation at the audited revision, secret redaction,
/// batching within forge limits, and omission disclosure.
///
/// Findings lacking a trustworthy location belong in the summary — never as
/// invented line annotations. Truncated <c>RawOutput</c> is never consulted.
/// </summary>
public static class AuditCheckPayloadBuilder
{
    /// <summary>GitHub allows at most 50 annotations per create/update call.</summary>
    public const int GitHubMaxAnnotationsPerBatch = 50;

    /// <summary>GitHub check-run output title cap (256 chars, keep one spare).</summary>
    public const int MaxTitleChars = 255;

    public static string BuildExternalId(
        string workItemId,
        AuditTarget target,
        int iteration,
        int attempt,
        string scope,
        string headSha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);
        if (target.IsDefault)
            throw new ArgumentException("Audit target must be non-empty.", nameof(target));
        var externalId = $"codeybox/{workItemId}/{target.Value}/{iteration}/{attempt}/{scope}/{headSha}";
        if (externalId.Length > 255)
            throw new ArgumentException("External id exceeds the 255-character forge limit.", nameof(scope));
        return externalId;
    }

    public static void ValidateHeadSha(string headSha)
    {
        if (string.IsNullOrWhiteSpace(headSha) || headSha.Length != 40 || !IsHex(headSha))
            throw new AuditCheckValidationException(
                "HeadSha must be the exact 40-hex audited commit; abbreviated or empty SHAs " +
                "would risk attaching the check to a different commit.");
        static bool IsHex(string s)
        {
            foreach (var c in s)
                if (!Uri.IsHexDigit(c))
                    return false;
            return true;
        }
    }

    /// <summary>
    /// Splits structured findings into forge annotations plus omitted entries.
    /// A finding yields an annotation only when it names exactly one
    /// repository-relative path that passes validation and carries at least one
    /// positive line hint; everything else is reported via
    /// <see cref="OmittedAnnotation"/> entries disclosed in the summary.
    /// </summary>
    public static (IReadOnlyList<AuditCheckAnnotation> Annotations, IReadOnlyList<OmittedAnnotation> Omitted)
        BuildAnnotations(
            IReadOnlyList<AuditReportFinding> findings,
            AuditCheckPublicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(options);

        var annotations = new List<AuditCheckAnnotation>();
        var omitted = new List<OmittedAnnotation>();

        foreach (var finding in findings)
        {
            var paths = (finding.Files ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (paths.Count != 1)
            {
                omitted.Add(new OmittedAnnotation(
                    finding.Title,
                    paths.Count == 0 ? "no file location reported" : "multiple file locations reported"));
                continue;
            }

            var normalized = NormalizePath(paths[0]);
            if (normalized is null)
            {
                omitted.Add(new OmittedAnnotation(finding.Title, $"untrusted path '{Truncate(paths[0], 80)}'"));
                continue;
            }

            var lines = (finding.LineHints ?? []).Where(l => l > 0).Distinct().OrderBy(l => l).ToList();
            if (lines.Count == 0)
            {
                omitted.Add(new OmittedAnnotation(finding.Title, "no line location reported"));
                continue;
            }

            var start = lines[0];
            var end = lines[^1];
            if (end - start > options.MaxAnnotationLineSpan)
            {
                omitted.Add(new OmittedAnnotation(
                    finding.Title, $"line span {start}-{end} exceeds the {options.MaxAnnotationLineSpan}-line cap"));
                continue;
            }

            annotations.Add(new AuditCheckAnnotation
            {
                Path = normalized,
                StartLine = start,
                EndLine = end,
                Level = ToAnnotationLevel(finding.Severity),
                Title = Truncate(RawOutputRedactor.Redact(finding.Title), options.MaxAnnotationTitleChars),
                Message = Truncate(RawOutputRedactor.Redact(finding.Message), options.MaxAnnotationMessageChars),
            });
        }

        return (annotations, omitted);
    }

    /// <summary>
    /// Chunks annotations into forge-sized batches. The batch count is bounded
    /// by <see cref="AuditCheckPublicationOptions.MaxAnnotationBatches"/>; the
    /// remainder is returned as overflow so the summary can disclose it and the
    /// complete-report link preserves the full finding set.
    /// </summary>
    public static (IReadOnlyList<IReadOnlyList<AuditCheckAnnotation>> Batches, int OverflowCount)
        ChunkBatches(
            IReadOnlyList<AuditCheckAnnotation> annotations,
            AuditCheckPublicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(options);

        var perBatch = Math.Min(options.MaxAnnotationsPerBatch, GitHubMaxAnnotationsPerBatch);
        if (perBatch <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxAnnotationsPerBatch must be positive.");

        var batches = new List<IReadOnlyList<AuditCheckAnnotation>>();
        var capacity = perBatch * Math.Max(1, options.MaxAnnotationBatches);
        var sendable = Math.Min(annotations.Count, capacity);
        for (var i = 0; i < sendable; i += perBatch)
            batches.Add(annotations.Skip(i).Take(perBatch).ToList());
        return (batches, annotations.Count - sendable);
    }

    /// <summary>
    /// Builds the human-readable check-run summary markdown: aggregate verdict
    /// (from the explicit verdict input, never the findings count), provenance
    /// (work item, target, iteration/attempt, scope, source revision, exact
    /// SHA), unlocatable findings, and omission/uncertainty disclosures.
    /// </summary>
    public static string BuildSummary(
        AuditCheckPublicationRequest request,
        IReadOnlyList<OmittedAnnotation> omitted,
        int batchOverflowCount,
        bool batchUncertain,
        AuditCheckPublicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(omitted);

        var verdictLine = (request.Lifecycle, request.Verdict) switch
        {
            (_, AuditCheckVerdict.Passed) =>
                $"Audit {Escape(request.Scope)} passed with {request.Findings.Count} finding(s).",
            (_, AuditCheckVerdict.Failed) =>
                $"Audit {Escape(request.Scope)} reported {request.Findings.Count} finding(s).",
            (_, AuditCheckVerdict.NotRun) =>
                $"No audit coverage for {Escape(request.Scope)}: {DescribeUnavailability(request.UnavailabilityReason)}. " +
                "This check is not successful coverage.",
            _ => $"Audit {Escape(request.Scope)}: {request.Lifecycle}.",
        };

        var sb = new StringBuilder();
        sb.AppendLine(verdictLine);
        sb.AppendLine();
        sb.AppendLine("Provenance");
        sb.AppendLine($"- Work item: `{Escape(request.WorkItemId)}`");
        sb.AppendLine($"- Target: `{Escape(request.Target.Value)}`");
        sb.AppendLine($"- Iteration: `{request.Iteration}` Attempt: `{request.Attempt}`");
        sb.AppendLine($"- Scope: `{Escape(request.Scope)}`");
        sb.AppendLine($"- Audited commit: `{Escape(request.HeadSha)}`");
        if (!string.IsNullOrWhiteSpace(request.SourceRevision))
            sb.AppendLine($"- Source revision: `{Escape(request.SourceRevision)}`");
        sb.AppendLine("- Publishing this check does not approve merging, bypass required checks, " +
                      "enable a deployment, or grant token scopes.");

        var unlocatable = omitted;
        if (unlocatable.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Findings without a trustworthy file/line location ({unlocatable.Count}) " +
                          "are listed here, not as line annotations:");
            foreach (var entry in unlocatable.Take(options.MaxOmittedFindingsInSummary))
                sb.AppendLine($"- {Escape(Truncate(entry.Title, 120))} ({Escape(entry.Reason)})");
            if (unlocatable.Count > options.MaxOmittedFindingsInSummary)
                sb.AppendLine($"- …and {unlocatable.Count - options.MaxOmittedFindingsInSummary} more (see the complete report).");
        }

        if (batchOverflowCount > 0)
            sb.AppendLine().AppendLine(
                $"{batchOverflowCount} annotation(s) exceed the bounded batch budget " +
                $"({options.MaxAnnotationBatches} batches of {options.MaxAnnotationsPerBatch}) and are " +
                "preserved only in the complete report linked below.");

        if (batchUncertain)
            sb.AppendLine().AppendLine(
                "Warning: an annotation batch write could not be reconciled with forge state " +
                "(ambiguous transport failure). Annotations may be duplicated or incomplete; " +
                "the complete report below is authoritative.");

        if (!string.IsNullOrWhiteSpace(request.DetailsUrl))
        {
            sb.AppendLine();
            sb.AppendLine($"Complete report: {Escape(request.DetailsUrl)}");
        }

        var summary = sb.ToString();
        return summary.Length > options.MaxSummaryChars
            ? summary[..options.MaxSummaryChars]
            : summary;
    }

    public static string BuildTitle(AuditCheckPublicationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var verdict = request.Verdict switch
        {
            AuditCheckVerdict.Passed => "passed",
            AuditCheckVerdict.Failed => "failed",
            _ => DescribeUnavailability(request.UnavailabilityReason),
        };
        var title = $"CodeyBox audit {request.Scope} {request.Target.Value} iter {request.Iteration}: {verdict}";
        var redacted = RawOutputRedactor.Redact(title);
        return redacted.Length > MaxTitleChars ? redacted[..MaxTitleChars] : redacted;
    }

    private static string DescribeUnavailability(AuditCheckUnavailabilityReason? reason) =>
        (reason ?? AuditCheckUnavailabilityReason.Missing) switch
        {
            AuditCheckUnavailabilityReason.Cancelled => "audit cancelled",
            AuditCheckUnavailabilityReason.InfrastructureFailed => "audit infrastructure failed",
            AuditCheckUnavailabilityReason.Skipped => "audit skipped",
            AuditCheckUnavailabilityReason.UnsupportedForge => "audit publishing unsupported on this forge",
            _ => "audit did not run",
        };

    private static string ToAnnotationLevel(string? severity) =>
        severity?.ToLowerInvariant() switch
        {
            "error" => "failure",
            "warning" or "warn" => "warning",
            _ => "notice",
        };

    /// <summary>
    /// Canonicalize-then-contain validation for finding paths: must be a
    /// repository-relative <c>/</c>-separated path with no escapes, no
    /// absolute forms, no drive specs, and no control characters. Absolute
    /// paths are rejected rather than relativized: stripping a leading
    /// <c>/</c> would guess that a foreign absolute path means a repository
    /// root. Returns the normalized path or <c>null</c> when untrustworthy.
    /// </summary>
    public static string? NormalizePath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (raw.StartsWith('/') || raw.StartsWith('\\'))
            return null;
        var path = raw.Trim().Replace('\\', '/');
        if (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (path.Contains("..", StringComparison.Ordinal))
            return null;
        if (path.Contains(':') || path.Contains('\0'))
            return null;
        if (path.Any(c => char.IsControl(c)))
            return null;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
            return null;
        var normalized = string.Join('/', segments);
        return normalized.Length is >= 1 and <= 512 ? normalized : null;
    }

    private static string Truncate(string value, int maxChars) =>
        string.IsNullOrEmpty(value) || value.Length <= maxChars ? value : value[..maxChars];

    private static string Escape(string value) =>
        value.Replace("`", "'", StringComparison.Ordinal)
             .Replace("<", "&lt;", StringComparison.Ordinal)
             .Replace(">", "&gt;", StringComparison.Ordinal)
             .Replace("\r", " ", StringComparison.Ordinal)
             .Replace("\n", " ", StringComparison.Ordinal);
}

/// <summary>A finding withheld from line annotations, disclosed in the summary.</summary>
public sealed record OmittedAnnotation(string Title, string Reason);
