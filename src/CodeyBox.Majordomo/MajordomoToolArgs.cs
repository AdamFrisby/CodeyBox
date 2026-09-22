namespace CodeyBox.Majordomo;

/// <summary>
/// Base of the closed argument union for the majordomo tool vocabulary.
/// The constructor is <c>internal</c> so only the sealed argument records in
/// this assembly can derive from it — an out-of-vocabulary argument type is
/// unrepresentable rather than rejected late by validation.
/// </summary>
public abstract record MajordomoToolArgs
{
    internal MajordomoToolArgs() { }
}

/// <summary>
/// Base of every MUTATE tool's argument contract. Carries the two fields the
/// authorization layer needs uniformly: <see cref="DryRun"/> and the blast
/// radius of the call (<see cref="AffectedItemCount"/>).
/// </summary>
public abstract record MajordomoMutateArgs : MajordomoToolArgs
{
    internal MajordomoMutateArgs() { }

    /// <summary>
    /// When true the call returns the change set it would produce without
    /// producing it. A dry-run mutates nothing, so
    /// <see cref="MajordomoAuthorization"/> never treats it as a mutation:
    /// it executes in every mode and consumes no per-turn budget.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Number of work items a real (non-dry-run) execution of this call would
    /// create or change. Used by the per-call and per-turn blast-radius
    /// bounds; always &gt;= 1.
    /// </summary>
    public abstract int AffectedItemCount { get; }
}
