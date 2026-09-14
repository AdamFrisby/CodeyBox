using System.Text.Json;

namespace CodeyBox.Core;

/// <summary>
/// Pure policy for the human deployment reviewer: the operator brief, the
/// answer↔verdict mapping, and the verdict→findings mapping. All decisions
/// here are pure functions of their inputs so they are trivially
/// unit-testable; the orchestrator owns the IO (park, notify, teardown).
/// </summary>
public static class HumanDeploymentReviewPolicy
{
    /// <summary>Maximum prompt characters embedded in the operator brief.</summary>
    public const int MaxPromptChars = 2000;

    /// <summary>Maximum acceptance-criteria entries embedded in the brief.</summary>
    public const int MaxCriteriaEntries = 20;

    /// <summary>Maximum characters per acceptance-criteria entry.</summary>
    public const int MaxCriteriaEntryChars = 500;

    /// <summary>Maximum characters for reject notes recorded with a verdict.</summary>
    public const int MaxNotesChars = 4000;

    private static readonly JsonSerializerOptions FindingJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Builds the backing operator question id for an audit iteration.
    /// Per-iteration so each verdict is recorded against its own question row.
    /// </summary>
    public static string QuestionIdFor(int iteration)
        => $"{WellKnownAuditorNames.HumanDeploymentReviewQuestionPrefix}-{iteration}";

    /// <summary>
    /// True when <paramref name="questionId"/> is a human-review backing
    /// question: the bare marker or the marker plus a numeric iteration
    /// suffix (<c>human-deployment-review-3</c>). Compared by exact shape —
    /// the id is then resolved through the review store by exact equality,
    /// never trusted as an iteration number.
    /// </summary>
    public static bool IsReviewQuestion(string? questionId)
    {
        if (questionId is null)
            return false;
        if (questionId.Equals(
                WellKnownAuditorNames.HumanDeploymentReviewQuestionPrefix,
                StringComparison.Ordinal))
            return true;
        var head = WellKnownAuditorNames.HumanDeploymentReviewQuestionPrefix + "-";
        if (!questionId.StartsWith(head, StringComparison.Ordinal))
            return false;
        var suffix = questionId[head.Length..];
        return suffix.Length > 0 && suffix.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// Maps an operator answer to a verdict. Exactly "approve" (trimmed,
    /// case-insensitive) approves; any other text rejects with the text as
    /// the review notes. Exact equality only — never substring.
    /// </summary>
    public static bool IsApprovalAnswer(string? answer)
        => string.Equals(answer?.Trim(), "approve", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Human-readable one-line description of a deployment endpoint for
    /// notifications. Prefers the URL, then host:port, then artifact path.
    /// </summary>
    public static string DescribeEndpoint(DeploymentEndpoint? endpoint)
    {
        if (endpoint is null)
            return "(endpoint unavailable)";
        if (!string.IsNullOrWhiteSpace(endpoint.Url))
            return endpoint.Url!;
        if (!string.IsNullOrWhiteSpace(endpoint.Host) && endpoint.Port is { } port)
            return $"{endpoint.Host}:{port}";
        if (!string.IsNullOrWhiteSpace(endpoint.Path))
            return endpoint.Path!;
        return $"({endpoint.Kind} endpoint)";
    }

    /// <summary>
    /// Builds the operator brief: what is deployed where, when the review
    /// expires, what to verify (acceptance criteria), and how to record the
    /// verdict. Bounded: the prompt and criteria are truncated to the
    /// <c>Max*</c> caps above. The item prompt is operator-authored and the
    /// criteria are pipeline-assembled; the endpoint is driver-provided —
    /// all are rendered as plain text, never executed.
    /// </summary>
    public static string BuildBrief(
        string workItemTitle,
        string workItemPrompt,
        string endpointDescription,
        DateTimeOffset deadline,
        IReadOnlyList<(string Name, string Description)> acceptanceCriteria,
        int iteration,
        string deploymentId)
    {
        var safeTitle = string.IsNullOrWhiteSpace(workItemTitle) ? "(untitled)" : workItemTitle.Trim();
        var prompt = workItemPrompt ?? string.Empty;
        var trimmedPrompt = prompt.Length > MaxPromptChars
            ? prompt[..MaxPromptChars] + "… [truncated]"
            : prompt;

        var brief = new System.Text.StringBuilder();
        brief.AppendLine($"Human deployment review requested (audit iteration {iteration}).");
        brief.AppendLine($"Deployment: {deploymentId} at {endpointDescription}.");
        brief.AppendLine($"This review expires at {deadline:O} — an undecided review fails closed as 'expired unreviewed'.");
        brief.AppendLine();
        brief.AppendLine($"Work item: {safeTitle}");
        if (!string.IsNullOrWhiteSpace(trimmedPrompt))
        {
            brief.AppendLine("Original request:");
            brief.AppendLine(trimmedPrompt);
            brief.AppendLine();
        }

        var criteria = acceptanceCriteria ?? [];
        if (criteria.Count > 0)
        {
            brief.AppendLine("Acceptance criteria to verify against the live deployment:");
            var shown = 0;
            foreach (var (name, description) in criteria)
            {
                if (shown >= MaxCriteriaEntries)
                {
                    brief.AppendLine($"- … [{criteria.Count - shown} more, truncated]");
                    break;
                }

                var entry = string.IsNullOrWhiteSpace(description) ? name : $"{name}: {description}";
                if (entry.Length > MaxCriteriaEntryChars)
                    entry = entry[..MaxCriteriaEntryChars] + "… [truncated]";
                brief.AppendLine($"- {entry}");
                shown++;
            }

            brief.AppendLine();
        }
        else
        {
            brief.AppendLine("No linked acceptance criteria — verify the live deployment satisfies the request above.");
            brief.AppendLine();
        }

        brief.Append("Record the verdict with the deployment-review approve endpoint, ");
        brief.Append("the reject endpoint with notes describing what fails, ");
        brief.Append("or answer this question with \"approve\" to approve (any other answer rejects with that text as notes).");
        return brief.ToString();
    }

    /// <summary>
    /// Maps a human verdict to audit findings with full blocking authority:
    /// approve yields no findings (pass); reject and expiry yield Error
    /// findings that flow into the normal rework loop. Never demoted —
    /// a human reviewer is an objective gate like any deployment probe.
    /// </summary>
    public static IReadOnlyList<AuditFinding> BuildVerdictFindings(
        string auditorName,
        HumanDeploymentReviewStatus status,
        string? notes,
        DateTimeOffset? deadline = null)
    {
        if (string.IsNullOrWhiteSpace(auditorName))
            throw new ArgumentException("Auditor name must be non-empty.", nameof(auditorName));

        return status switch
        {
            HumanDeploymentReviewStatus.Approved => [],
            HumanDeploymentReviewStatus.Rejected => [new AuditFinding(
                auditorName,
                AuditSeverity.Error,
                "Human review rejected the deployment",
                string.IsNullOrWhiteSpace(notes)
                    ? "The operator rejected the verification deployment without notes."
                    : TruncateNotes(notes!))],
            HumanDeploymentReviewStatus.Expired => [new AuditFinding(
                auditorName,
                AuditSeverity.Error,
                "Human review expired unreviewed",
                deadline is { } d
                    ? $"No operator verdict was recorded before the review deadline {d:O}. Silence never passes: the deployment was torn down and the iteration fails closed."
                    : "No operator verdict was recorded before the review deadline. Silence never passes: the deployment was torn down and the iteration fails closed.")],
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Only a decided or expired review maps to findings; pending reviews must park, not complete."),
        };
    }

    /// <summary>Serializes automated deployment-stage findings for the review row.</summary>
    public static string SerializeFindings(IReadOnlyList<AuditFinding> findings)
    {
        var dtos = findings.Select(f => new StoredFinding(
            f.AuditorName, (int)f.Severity, f.Title, f.Description, f.Location)).ToList();
        return JsonSerializer.Serialize(dtos, FindingJsonOptions);
    }

    /// <summary>
    /// Deserializes stored findings. Throws <see cref="InvalidOperationException"/>
    /// on corrupt payloads so the caller fails closed instead of passing.
    /// </summary>
    public static IReadOnlyList<AuditFinding> DeserializeFindings(string json)
    {
        List<StoredFinding>? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<List<StoredFinding>>(json, FindingJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Stored automated deployment findings are corrupt.", ex);
        }

        if (dtos is null)
            throw new InvalidOperationException("Stored automated deployment findings are corrupt.");
        return dtos.Select(d => new AuditFinding(
            d.AuditorName, (AuditSeverity)d.Severity, d.Title, d.Description, d.Location)).ToList();
    }

    public static string SerializeStrings(IReadOnlyList<string> values)
        => JsonSerializer.Serialize(values, FindingJsonOptions);

    public static IReadOnlyList<string> DeserializeStrings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, FindingJsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Stored human-review string list is corrupt.", ex);
        }
    }

    public static string TruncateNotes(string notes)
        => notes.Length > MaxNotesChars ? notes[..MaxNotesChars] + "… [truncated]" : notes;

    private sealed record StoredFinding(
        string AuditorName,
        int Severity,
        string Title,
        string Description,
        string? Location);
}
