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
/// every field normalises to null, so the model cannot emit a no-op edit.
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
        // Priority and ExternalIds have dedicated plan paths, so they are
        // assigned outside the array — every OTHER field's assignment is an
        // element of this array, which serves two guards at once: the
        // no-change check below inspects the normalised values themselves
        // rather than a parallel list of property names, and HasFieldEdits
        // reads the same array. A new field's assignment belongs in the
        // array, where both guards count it automatically.
        // The guard runs on the NORMALISED values: an input that normalises
        // away (a whitespace auditComplexity) is not a change, so a patch
        // carrying only that is still unrepresentable.
        Priority = priority is { } p ? WorkItemFieldValidation.Priority(p) : null;
        ExternalIds = externalIds is null ? null : WorkItemFieldValidation.ExternalIds(externalIds);

        object?[] normalised =
        [
            Title = title is null ? null : WorkItemFieldValidation.Title(title),
            Prompt = prompt is null ? null : WorkItemFieldValidation.Prompt(prompt),
            Agent = agent,
            AgentClassId = agentClassId is null ? null : WorkItemFieldValidation.AgentClassId(agentClassId),
            WorkTimeout = workTimeout is { } wt ? WorkItemFieldValidation.WorkTimeout(wt) : null,
            MergeTimeout = mergeTimeout is { } mt ? WorkItemFieldValidation.MergeTimeout(mt) : null,
            MinModelScore = minModelScore is { } mms ? WorkItemFieldValidation.MinModelScore(mms) : null,
            AuditMaxIterations = auditMaxIterations is { } ami ? WorkItemFieldValidation.AuditMaxIterations(ami) : null,
            AuditComplexity = WorkItemFieldValidation.AuditComplexity(auditComplexity),
            RequiredCapabilities = requiredCapabilities is null
                ? null
                : WorkItemFieldValidation.RequiredCapabilities(requiredCapabilities),
            DependsOn = dependsOn is null ? null : WorkItemFieldValidation.DependsOn(dependsOn),
            Knobs = knobs is null ? null : WorkItemFieldValidation.Knobs(knobs),
        ];

        if (normalised.All(v => v is null) && Priority is null && ExternalIds is null)
            throw new ArgumentException("patch must change at least one field");

        HasFieldEdits = normalised.Any(v => v is not null);
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

    /// <summary>
    /// New audit complexity label; null = unchanged. A whitespace input
    /// normalises to null — unchanged, not cleared — matching the REST PATCH
    /// where the same input is a no-op write.
    /// </summary>
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

    /// <summary>
    /// True when the patch carries at least one edit the shared field-patch
    /// plan applies — every field except <see cref="Priority"/> and
    /// <see cref="ExternalIds"/>, which are committed through their own
    /// dedicated plans. Derived from the constructor's normalised-value
    /// array, so a field added there is counted automatically.
    /// </summary>
    public bool HasFieldEdits { get; }
}
