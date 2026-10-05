namespace CodeyBox.Core;

/// <summary>
/// Hot-reloadable operational knobs for opt-in audit check-run publication.
/// Everything that is a forge limit, batch budget, payload cap, or retry bound
/// lives here — never as a literal in the publisher — so operators can tune it
/// without a rebuild. Disabled by default: no check runs are published unless
/// the project opts in (see <see cref="ProjectAuditChecks"/>).
/// </summary>
public sealed record AuditCheckPublicationOptions
{
    /// <summary>Configuration section name for binding and hot-reload.</summary>
    public const string SectionName = "CodeyBox:AuditCheckPublication";

    /// <summary>Master switch. Default false — publication is strictly opt-in.</summary>
    public bool Enabled { get; init; }

    /// <summary>Check-run name. Default <c>codeybox-audit</c>.</summary>
    public string CheckName { get; init; } = "codeybox-audit";

    /// <summary>Annotations per forge call. Capped at the GitHub limit of 50. Default 50.</summary>
    public int MaxAnnotationsPerBatch { get; init; } = 50;

    /// <summary>Bounded number of annotation batches per check run. Default 2 (up to 100 annotations).</summary>
    public int MaxAnnotationBatches { get; init; } = 2;

    /// <summary>Summary markdown cap in chars (GitHub allows 65 535). Default 32 768.</summary>
    public int MaxSummaryChars { get; init; } = 32_768;

    /// <summary>Per-annotation message cap in chars. Default 4 096.</summary>
    public int MaxAnnotationMessageChars { get; init; } = 4_096;

    /// <summary>Per-annotation title cap in chars. Default 120.</summary>
    public int MaxAnnotationTitleChars { get; init; } = 120;

    /// <summary>Max annotation line span (end - start) for one annotation. Default 20.</summary>
    public int MaxAnnotationLineSpan { get; init; } = 20;

    /// <summary>Omitted-finding entries enumerated in the summary. Default 20.</summary>
    public int MaxOmittedFindingsInSummary { get; init; } = 20;

    /// <summary>Max transport/rate-limit retries per operation (bounded backoff). Default 5.</summary>
    public int RetryMaxAttempts { get; init; } = 5;

    /// <summary>Base delay for bounded exponential backoff. Default 2 seconds.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Upper bound for a single backoff sleep. Default 2 minutes.</summary>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Documented delivery policy hook: when false (the default), a transport
    /// publication failure is surfaced visibly on the publication record but the
    /// audit verdict itself is unchanged and no gate fails. When true, the
    /// service reports delivery failure as a distinct
    /// <c>DeliveryRequired</c> outcome the caller may treat as blocking.
    /// Enabling this never changes merge gating by itself — callers decide.
    /// </summary>
    public bool RequireDelivery { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(CheckName) || CheckName.Length > 255)
            throw new ArgumentException("CheckName must be 1-255 characters.", nameof(CheckName));
        if (MaxAnnotationsPerBatch <= 0 || MaxAnnotationsPerBatch > AuditCheckPayloadBuilder.GitHubMaxAnnotationsPerBatch)
            throw new ArgumentOutOfRangeException(
                nameof(MaxAnnotationsPerBatch), "Must be 1-50 (the GitHub per-call limit).");
        if (MaxAnnotationBatches <= 0 || MaxAnnotationBatches > 10)
            throw new ArgumentOutOfRangeException(
                nameof(MaxAnnotationBatches), "Must be 1-10 to bound API fan-out.");
        if (MaxSummaryChars <= 0 || MaxSummaryChars > 65_535)
            throw new ArgumentOutOfRangeException(nameof(MaxSummaryChars), "Must be 1-65535.");
        if (MaxAnnotationMessageChars <= 0 || MaxAnnotationMessageChars > 65_535)
            throw new ArgumentOutOfRangeException(nameof(MaxAnnotationMessageChars), "Must be 1-65535.");
        if (MaxAnnotationTitleChars <= 0 || MaxAnnotationTitleChars > 255)
            throw new ArgumentOutOfRangeException(nameof(MaxAnnotationTitleChars), "Must be 1-255.");
        if (MaxAnnotationLineSpan < 0 || MaxAnnotationLineSpan > 1_000)
            throw new ArgumentOutOfRangeException(nameof(MaxAnnotationLineSpan), "Must be 0-1000.");
        if (MaxOmittedFindingsInSummary <= 0 || MaxOmittedFindingsInSummary > 100)
            throw new ArgumentOutOfRangeException(nameof(MaxOmittedFindingsInSummary), "Must be 1-100.");
        if (RetryMaxAttempts < 0 || RetryMaxAttempts > 10)
            throw new ArgumentOutOfRangeException(nameof(RetryMaxAttempts), "Must be 0-10 (bounded).");
        if (RetryBaseDelay < TimeSpan.Zero || RetryBaseDelay > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(RetryBaseDelay), "Must be 0-10 minutes.");
        if (RetryMaxDelay < RetryBaseDelay || RetryMaxDelay > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(RetryMaxDelay), "Must be >= base delay and <= 1 hour.");
    }
}

/// <summary>
/// Per-project opt-in for audit check-run publication. Binds from project
/// configuration and reloads with the project list. Default: disabled.
/// </summary>
public sealed record ProjectAuditChecks
{
    /// <summary>When true, completed audits for this project are published as forge check runs.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Optional check-run name override for this project. Null means the global
    /// <see cref="AuditCheckPublicationOptions.CheckName"/> applies.
    /// </summary>
    public string? CheckName { get; init; }
}
