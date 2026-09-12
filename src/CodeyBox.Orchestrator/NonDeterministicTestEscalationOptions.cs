namespace CodeyBox.Orchestrator;

/// <summary>
/// Controls escalation of audit test failures classified as
/// <see cref="CodeyBox.Core.TestFailureAttribution.NotDiffAttributable"/>.
/// Bound from <c>CodeyBox:NonDeterministicTestEscalation</c> and hot-reloaded
/// via <see cref="NonDeterministicTestEscalationSnapshot"/>.
/// </summary>
public sealed class NonDeterministicTestEscalationOptions
{
    /// <summary>
    /// Master switch. When false, NotDiffAttributable failures keep the
    /// historical behaviour (fed back to the rework agent as blocking
    /// findings). When true, they spawn an isolated base-branch fix item and
    /// park the parent on a dependsOn gate. Default true: the gate only fires
    /// when attribution actually produced a NotDiffAttributable verdict, which
    /// itself requires TestFailureAttribution to be enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum flaky test names carried into a single child item. Bounds the
    /// child prompt and title. Clamped to [1, 50]. Default 10.
    /// </summary>
    public int MaxTestsPerChild { get; set; } = 10;

    /// <summary>
    /// Maximum test names embedded in the child title. Remaining names are
    /// summarised as "+N more". Clamped to [1, 10]. Default 3.
    /// </summary>
    public int MaxTitleTestNames { get; set; } = 3;

    /// <summary>Validates and clamps this instance in place.</summary>
    public void Normalize()
    {
        MaxTestsPerChild = Math.Clamp(MaxTestsPerChild, 1, 50);
        MaxTitleTestNames = Math.Clamp(MaxTitleTestNames, 1, 10);
    }

    /// <summary>Returns a normalized copy of this instance.</summary>
    public NonDeterministicTestEscalationOptions Normalized()
    {
        var copy = new NonDeterministicTestEscalationOptions
        {
            Enabled = Enabled,
            MaxTestsPerChild = MaxTestsPerChild,
            MaxTitleTestNames = MaxTitleTestNames,
        };
        copy.Normalize();
        return copy;
    }
}

/// <summary>
/// Shared, swappable holder for the current
/// <see cref="NonDeterministicTestEscalationOptions"/>. Registered as a DI
/// singleton so <see cref="PipelineRunner"/> reads through the same reference
/// the hot-reload coordinator writes to.
/// </summary>
public sealed class NonDeterministicTestEscalationSnapshot
{
    private NonDeterministicTestEscalationOptions _current;

    public NonDeterministicTestEscalationSnapshot(NonDeterministicTestEscalationOptions initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial.Normalized();
    }

    /// <summary>Current snapshot. Volatile read so concurrent Replace is safe.</summary>
    public NonDeterministicTestEscalationOptions Current => Volatile.Read(ref _current);

    /// <summary>Atomically publishes <paramref name="next"/> as the new snapshot.</summary>
    public void Replace(NonDeterministicTestEscalationOptions next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref _current, next.Normalized());
    }
}
