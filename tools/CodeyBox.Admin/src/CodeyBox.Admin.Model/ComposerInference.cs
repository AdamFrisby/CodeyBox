namespace CodeyBox.Admin.Model;

/// <summary>
/// Where a composer field value came from. Everything the interface
/// already knows is inferred and shown as a chip one click from being
/// overridden; the operator's own edits always win.
/// </summary>
public enum ComposerFieldSource
{
    /// <summary>No value yet.</summary>
    None,

    /// <summary>Deep link or navigation query (?projectId=, ?agent=, …).</summary>
    Query,

    /// <summary>The selected project's configured default.</summary>
    ProjectDefault,

    /// <summary>A follow-up context (depends on this item, not its words).</summary>
    FollowUp,

    /// <summary>The suggestion being promoted.</summary>
    Suggestion,

    /// <summary>The operator typed or picked it.</summary>
    Operator,
}

/// <summary>
/// A composer field that carries its provenance. Inferred values flow in
/// via <see cref="ApplyInferred"/>; the moment the operator edits the
/// field, <see cref="SetOperator"/> freezes it and later inference —
/// project switches included — must not silently replace it.
/// Immutable; every transition returns a new instance.
/// </summary>
/// <typeparam name="T">Field value type; null means unset.</typeparam>
public sealed record InferredField<T>(T? Value, ComposerFieldSource Source, bool Overridden)
{
    /// <summary>An empty field with no value and no provenance.</summary>
    public static InferredField<T> Empty { get; } = new(default, ComposerFieldSource.None, false);

    /// <summary>
    /// Offers an inferred value. Ignored when the operator has overridden
    /// the field; otherwise replaces the current value and source, even if
    /// the new value is null (a project switch clears stale inference).
    /// </summary>
    public InferredField<T> ApplyInferred(T? value, ComposerFieldSource source)
    {
        if (Overridden)
        {
            return this;
        }

        return this with { Value = value, Source = source };
    }

    /// <summary>
    /// Records the operator's own edit. Marks the field overridden so
    /// future <see cref="ApplyInferred"/> calls leave it alone.
    /// </summary>
    public InferredField<T> SetOperator(T? value) =>
        this with { Value = value, Source = ComposerFieldSource.Operator, Overridden = true };

    /// <summary>
    /// Hands the field back to inference: clears the override lock and
    /// offers <paramref name="value"/> as the new inferred value.
    /// </summary>
    public InferredField<T> ResetToInferred(T? value, ComposerFieldSource source) =>
        new(value, source, false);
}

/// <summary>
/// Precedence rules shared by every inferred composer field: an explicit
/// deep-link value beats the project default, which beats nothing.
/// Pure over its inputs.
/// </summary>
public static class ComposerInference
{
    /// <summary>
    /// Resolves one field: first non-blank of query value, project
    /// default; blank everywhere means unset. Returns the value and
    /// where it came from.
    /// </summary>
    public static (string? Value, ComposerFieldSource Source) ResolveText(
        string? queryValue, string? projectDefault)
    {
        if (!string.IsNullOrWhiteSpace(queryValue))
        {
            return (queryValue.Trim(), ComposerFieldSource.Query);
        }

        if (!string.IsNullOrWhiteSpace(projectDefault))
        {
            return (projectDefault.Trim(), ComposerFieldSource.ProjectDefault);
        }

        return (null, ComposerFieldSource.None);
    }

    /// <summary>
    /// Resolves the audit-budget chip: query override, else the project's
    /// configured budget (values ≤ 0 mean the project reports none).
    /// </summary>
    public static (int? Value, ComposerFieldSource Source) ResolveAuditBudget(
        int? queryValue, int projectDefault)
    {
        if (queryValue is { } q && q > 0)
        {
            return (q, ComposerFieldSource.Query);
        }

        if (projectDefault > 0)
        {
            return (projectDefault, ComposerFieldSource.ProjectDefault);
        }

        return (null, ComposerFieldSource.None);
    }
}
