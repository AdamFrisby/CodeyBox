using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeyBox.Core;

public sealed record PlanReviewDecision(
    bool Approved,
    string Summary,
    PlanReviewFeedback? ReworkFeedback = null);

/// <summary>
/// Structured, bounded feedback shown to a later planning-agent turn. That
/// turn drives a tool-bearing, credentialed agent, so the payload carries only
/// orchestrator-authored enumerated metadata — never model-authored reviewer
/// prose (title/description/location), which could smuggle instructions into
/// the planning prompt. <see cref="BlockingIssueCount"/> is the total blocker
/// count; <see cref="Issues"/> is a bounded sample identifying each blocker by
/// its trusted category, severity, and stable finding id so the agent can
/// locate the full finding out-of-band without the prose crossing the prompt
/// boundary.
/// </summary>
public sealed record PlanReviewFeedback(
    int BlockingIssueCount,
    IReadOnlyList<PlanReviewFeedbackIssue> Issues);

/// <summary>
/// A single blocking plan-review issue reduced to trusted, enumerated metadata.
/// <see cref="Category"/> is derived from the auditor name by the orchestrator,
/// <see cref="Severity"/> is an enum, and <see cref="FindingId"/> is a stable
/// opaque digest — none of which carries free-form reviewer text.
/// </summary>
public sealed record PlanReviewFeedbackIssue(
    AuditSeverity Severity,
    string Category,
    string FindingId);

public sealed record PlanArtifactDocument(
    string Approach,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> TestStrategy,
    IReadOnlyList<string> Risks,
    string SatisfiesTask)
{
    private const int MaxFieldChars = 4000;

    /// <summary>
    /// Maximum number of entries kept per plan string-array field
    /// (<see cref="Files"/>, <see cref="TestStrategy"/>, <see cref="Risks"/>).
    /// Also the upper bound on how many plan-derived test cases a single work
    /// item can emit, which <c>PlanTestCaseReconciler</c> relies on to bound
    /// its prune sweep.
    /// </summary>
    public const int MaxListItems = 25;
    private const int MaxListItemChars = 600;
    private const int PressureTrimmedListItems = 10;
    private const int PressureTrimmedFieldChars = MaxFieldChars / 2;
    private const int PressureTrimmedListItemChars = MaxListItemChars / 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string NormalizeRaw(string raw, int maxChars)
    {
        var redacted = RawOutputRedactor.Redact(raw).Trim();
        if (string.IsNullOrWhiteSpace(redacted))
            return string.Empty;

        var document = ParseJson(redacted);
        var normalized = JsonSerializer.Serialize(document, JsonOptions);
        if (normalized.Length <= maxChars)
            return normalized;

        var trimmed = document with
        {
            Approach = Truncate(document.Approach, PressureTrimmedFieldChars),
            Files = document.Files.Take(PressureTrimmedListItems).Select(v => Truncate(v, PressureTrimmedListItemChars)).ToArray(),
            TestStrategy = document.TestStrategy.Take(PressureTrimmedListItems).Select(v => Truncate(v, PressureTrimmedListItemChars)).ToArray(),
            Risks = document.Risks.Take(PressureTrimmedListItems).Select(v => Truncate(v, PressureTrimmedListItemChars)).ToArray(),
            SatisfiesTask = Truncate(document.SatisfiesTask, PressureTrimmedFieldChars),
        };
        normalized = JsonSerializer.Serialize(trimmed, JsonOptions);
        if (normalized.Length <= maxChars)
            return normalized;

        throw new InvalidOperationException(
            $"Planning phase produced a structured PLAN artifact larger than {maxChars} characters after normalization.");
    }

    public static PlanArtifactDocument ParseCanonical(string artifact)
    {
        var parsed = ParseJson(artifact);
        return parsed;
    }

    public static string ToImplementationGuidance(string artifact)
    {
        var plan = ParseCanonical(artifact);
        var files = plan.Files
            .Distinct(StringComparer.Ordinal)
            .Take(MaxListItems)
            .ToArray();

        var sb = new StringBuilder();
        sb.Append("""
            ## Reviewed planning metadata

            A schema-valid PLAN artifact was approved before implementation. Use this reviewed plan as guidance; adapt only when repository facts require a narrower correction.

            """);
        AppendField(sb, "Approach", plan.Approach);
        AppendList(sb, "Plan-declared files/areas", files);
        AppendList(sb, "Test strategy", plan.TestStrategy);
        AppendList(sb, "Risks and mitigations", plan.Risks);
        AppendField(sb, "How this satisfies the task", plan.SatisfiesTask);
        return sb.ToString().TrimEnd();
    }

    private static PlanArtifactDocument ParseJson(string raw)
    {
        var json = ExtractJsonObject(raw);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Planning phase must produce a JSON object PLAN artifact.");

        var root = doc.RootElement;
        var result = new PlanArtifactDocument(
            RequiredString(root, "approach"),
            RequiredStringList(root, "files", "filesToChange", "areasToChange"),
            RequiredStringList(root, "testStrategy", "tests", "e2eStrategy"),
            RequiredStringList(root, "risks", "risksAndMitigations"),
            RequiredString(root, "satisfiesTask", "taskSatisfaction", "howItSatisfiesTheTask"));

        return result;
    }

    private static string ExtractJsonObject(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
                trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
        }

        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
            return trimmed;

        var first = trimmed.IndexOf('{');
        var last = trimmed.LastIndexOf('}');
        if (first >= 0 && last > first)
            return trimmed[first..(last + 1)];

        throw new InvalidOperationException(
            "Planning phase must produce a structured JSON PLAN artifact with approach, files, testStrategy, risks, and satisfiesTask fields.");
    }

    private static string RequiredString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException($"PLAN field '{name}' must be a string.");
            var normalized = NormalizeText(value.GetString());
            if (!string.IsNullOrWhiteSpace(normalized))
                return Truncate(normalized, MaxFieldChars);
        }

        throw new InvalidOperationException($"PLAN artifact is missing required string field '{names[0]}'.");
    }

    private static IReadOnlyList<string> RequiredStringList(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
                continue;

            string[] list = value.ValueKind switch
            {
                JsonValueKind.String => ScalarStringList(value),
                JsonValueKind.Array => value.EnumerateArray()
                    .Select((element, index) =>
                    {
                        if (element.ValueKind != JsonValueKind.String)
                            throw new InvalidOperationException($"PLAN field '{name}' item {index} must be a string.");
                        return Truncate(NormalizeText(element.GetString()), MaxListItemChars);
                    })
                    .Where(static s => !string.IsNullOrWhiteSpace(s))
                    .Take(MaxListItems)
                    .ToArray(),
                _ => throw new InvalidOperationException($"PLAN field '{name}' must be a string array."),
            };

            if (list.Length > 0)
                return list;
        }

        throw new InvalidOperationException($"PLAN artifact is missing required string-array field '{names[0]}'.");
    }

    private static string[] ScalarStringList(JsonElement value)
    {
        var normalized = NormalizeText(value.GetString());
        return string.IsNullOrWhiteSpace(normalized)
            ? []
            : [Truncate(normalized, MaxListItemChars)];
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var redacted = RawOutputRedactor.Redact(value);
        var sb = new StringBuilder(redacted.Length);
        var previousWhitespace = false;
        foreach (var ch in redacted)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                if (!previousWhitespace)
                    sb.Append(' ');
                previousWhitespace = true;
                continue;
            }

            sb.Append(ch);
            previousWhitespace = false;
        }

        return sb.ToString().Trim();
    }

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars];

    private static void AppendField(StringBuilder sb, string label, string value)
    {
        sb.Append(label).Append(":\n");
        sb.Append(value).Append("\n\n");
    }

    private static void AppendList(StringBuilder sb, string label, IEnumerable<string> values)
    {
        sb.Append(label).Append(":\n");
        foreach (var value in values)
            sb.Append("- ").Append(value).Append('\n');
        sb.Append('\n');
    }
}
