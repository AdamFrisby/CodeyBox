namespace CodeyBox.Core;

/// <summary>
/// Single source of truth for work-item field bounds shared by the REST API
/// (create / PATCH / PUT validation) and any other queue-facing surface such
/// as the majordomo tool contract. Keeping the numbers here stops two entry
/// points drifting on what a valid title, prompt, priority, or dependency
/// list looks like.
/// </summary>
public static class WorkItemLimits
{
    /// <summary>Maximum length of <see cref="WorkItem.Title"/> in characters.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Maximum length of <see cref="WorkItem.Prompt"/> in characters (64 KiB).</summary>
    public const int MaxPromptLength = 64 * 1024;

    /// <summary>Minimum work-item scheduling priority.</summary>
    public const int MinPriority = -1000;

    /// <summary>Maximum work-item scheduling priority.</summary>
    public const int MaxPriority = 1000;

    /// <summary>Maximum entries in <see cref="WorkItem.DependsOn"/>.</summary>
    public const int MaxDependsOn = 100;

    /// <summary>Maximum entries in <see cref="WorkItem.ExternalIds"/>.</summary>
    public const int MaxExternalIds = 16;

    /// <summary>Maximum tags in <see cref="WorkItem.RequiredCapabilities"/>.</summary>
    public const int MaxRequiredCapabilities = 16;

    /// <summary>Maximum length of a single required-capability tag.</summary>
    public const int MaxCapabilityLength = 64;

    /// <summary>Maximum length of the <see cref="WorkItem.AuditComplexity"/> label.</summary>
    public const int MaxAuditComplexityLength = 64;

    /// <summary>Maximum length of an <see cref="WorkItem.AgentClassId"/> reference.</summary>
    public const int MaxAgentClassIdLength = 200;

    /// <summary>Lowest selectable <see cref="WorkItem.MinModelScore"/>.</summary>
    public const int MinModelScoreFloor = 0;

    /// <summary>Highest selectable <see cref="WorkItem.MinModelScore"/>.</summary>
    public const int MinModelScoreCeiling = 200;

    /// <summary>Lower bound (minutes) for a per-item merge-phase timeout.</summary>
    public const int MinMergeTimeoutMinutes = 1;

    /// <summary>Upper bound (minutes) for a per-item merge-phase timeout.</summary>
    public const int MaxMergeTimeoutMinutes = 240;
}
