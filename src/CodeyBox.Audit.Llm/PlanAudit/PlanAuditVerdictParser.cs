using System.Text.Json;

namespace CodeyBox.Audit.Llm.PlanAudit;

/// <summary>
/// Parses a plan-audit reviewer's raw text response into a
/// <see cref="PlanAuditVerdict"/>. Reuses the shared strict JSON extraction
/// (<see cref="ReviewVerdictJson"/>) so a chatty or prompt-injected response
/// cannot smuggle a second object past the gate, then normalizes the
/// vocabulary tokens through <see cref="PlanAuditVocabulary"/>. Throws
/// <see cref="JsonException"/> on any structurally invalid response; the
/// auditor turns that into a blocking finding rather than a silent pass.
/// </summary>
public static class PlanAuditVerdictParser
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static PlanAuditVerdict Parse(string raw)
    {
        var json = ReviewVerdictJson.ExtractObject(raw);
        var dto = JsonSerializer.Deserialize<VerdictDto>(json, JsonOpts)
            ?? throw new JsonException("null plan-audit verdict");

        var findings = (dto.Findings ?? []).Select(f => new PlanAuditFinding(
            Criterion: Clean(f.Criterion, "unspecified"),
            Severity: PlanAuditVocabulary.ParseSeverity(f.Severity),
            Grounding: PlanAuditVocabulary.ParseGrounding(f.Grounding),
            Title: Clean(f.Title, "(no title)"),
            Description: Clean(f.Description, string.Empty),
            EvidenceFromPlan: NullIfBlank(f.EvidenceFromPlan),
            RequiredFix: NullIfBlank(f.RequiredFix))).ToList();

        var notApplicable = (dto.NotApplicable ?? [])
            .Select(n => new PlanAuditNotApplicable(
                Criterion: Clean(n.Criterion, "unspecified"),
                Reason: Clean(n.Reason, string.Empty)))
            .ToList();

        var openQuestions = (dto.OpenQuestions ?? [])
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Select(q => q!.Trim())
            .ToList();

        return new PlanAuditVerdict(findings, notApplicable, openQuestions);
    }

    private static string Clean(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record VerdictDto(
        List<FindingDto>? Findings,
        List<NotApplicableDto>? NotApplicable,
        List<string?>? OpenQuestions);

    private sealed record FindingDto(
        string? Criterion,
        string? Severity,
        string? Grounding,
        string? Title,
        string? Description,
        string? EvidenceFromPlan,
        string? RequiredFix);

    private sealed record NotApplicableDto(string? Criterion, string? Reason);
}
