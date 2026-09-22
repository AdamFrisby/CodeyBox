using CodeyBox.Core;

namespace CodeyBox.Majordomo;

/// <summary>
/// Replace-set field edit applied to an existing work item by
/// <c>update_work_item</c>. Every field is optional; a null field means
/// "leave unchanged", a non-null field replaces the stored value. For
/// collection fields an empty collection is a real value — it clears the
/// stored list/map.
///
/// A patch with no changes is unrepresentable: the constructor throws when
/// every field is null, so the model cannot emit a no-op edit.
/// </summary>
public sealed record WorkItemPatch
{
    public WorkItemPatch(
        string? title = null,
        string? prompt = null,
        AgentKind? agent = null,
        string? agentClassId = null,
        int? priority = null,
        TimeSpan? workTimeout = null,
        TimeSpan? mergeTimeout = null,
        int? minModelScore = null,
        int? auditMaxIterations = null,
        string? auditComplexity = null,
        IReadOnlyList<string>? requiredCapabilities = null,
        IReadOnlyList<WorkItemId>? dependsOn = null,
        IReadOnlyDictionary<string, string>? externalIds = null,
        IReadOnlyDictionary<string, string>? knobs = null)
    {
        if (title is null && prompt is null && agent is null && agentClassId is null
            && priority is null && workTimeout is null && mergeTimeout is null
            && minModelScore is null && auditMaxIterations is null && auditComplexity is null
            && requiredCapabilities is null && dependsOn is null && externalIds is null
            && knobs is null)
        {
            throw new ArgumentException("patch must change at least one field");
        }

        Title = title is null ? null : WorkItemFieldValidation.Title(title);
        Prompt = prompt is null ? null : WorkItemFieldValidation.Prompt(prompt);
        Agent = agent;
        AgentClassId = agentClassId is null ? null : WorkItemFieldValidation.AgentClassId(agentClassId);
        Priority = priority is { } p ? WorkItemFieldValidation.Priority(p) : null;
        WorkTimeout = workTimeout is { } wt ? WorkItemFieldValidation.WorkTimeout(wt) : null;
        MergeTimeout = mergeTimeout is { } mt ? WorkItemFieldValidation.MergeTimeout(mt) : null;
        MinModelScore = minModelScore is { } mms ? WorkItemFieldValidation.MinModelScore(mms) : null;
        AuditMaxIterations = auditMaxIterations is { } ami ? WorkItemFieldValidation.AuditMaxIterations(ami) : null;
        AuditComplexity = WorkItemFieldValidation.AuditComplexity(auditComplexity);
        RequiredCapabilities = requiredCapabilities is null
            ? null
            : WorkItemFieldValidation.RequiredCapabilities(requiredCapabilities);
        DependsOn = dependsOn is null ? null : WorkItemFieldValidation.DependsOn(dependsOn);
        ExternalIds = externalIds is null ? null : WorkItemFieldValidation.ExternalIds(externalIds);
        Knobs = knobs is null ? null : WorkItemFieldValidation.Knobs(knobs);
    }

    /// <summary>New title; null = unchanged.</summary>
    public string? Title { get; }

    /// <summary>New prompt; null = unchanged.</summary>
    public string? Prompt { get; }

    /// <summary>New agent preference; null = unchanged.</summary>
    public AgentKind? Agent { get; }

    /// <summary>New agent-class route; null = unchanged.</summary>
    public string? AgentClassId { get; }

    /// <summary>New scheduling priority; null = unchanged.</summary>
    public int? Priority { get; }

    /// <summary>New work-phase timeout; null = unchanged.</summary>
    public TimeSpan? WorkTimeout { get; }

    /// <summary>New merge-phase timeout; null = unchanged.</summary>
    public TimeSpan? MergeTimeout { get; }

    /// <summary>New minimum model-quality score; null = unchanged.</summary>
    public int? MinModelScore { get; }

    /// <summary>New audit iteration cap; null = unchanged.</summary>
    public int? AuditMaxIterations { get; }

    /// <summary>New audit complexity label; null = unchanged.</summary>
    public string? AuditComplexity { get; }

    /// <summary>Replace-set required capabilities; null = unchanged, empty = clear.</summary>
    public IReadOnlyList<string>? RequiredCapabilities { get; }

    /// <summary>Replace-set dependency list; null = unchanged, empty = clear.</summary>
    public IReadOnlyList<WorkItemId>? DependsOn { get; }

    /// <summary>
    /// Replace-set namespaced external identifiers; null = unchanged, empty =
    /// clear. Namespace deletion is expressed by omitting the namespace from
    /// the replacement map.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ExternalIds { get; }

    /// <summary>Replace-set knob overrides; null = unchanged, empty = clear.</summary>
    public IReadOnlyDictionary<string, string>? Knobs { get; }
}
