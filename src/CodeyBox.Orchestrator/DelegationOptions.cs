namespace CodeyBox.Orchestrator;

/// <summary>
/// Operational tuning knobs for the delegation phase (the operator-triggered
/// escape hatch for items that cannot converge through the normal
/// work/audit/rework cycle). Bound from <c>CodeyBox:Delegation</c> and read
/// through a hot-reloadable accessor so edits take effect on the next
/// delegation turn without restart.
/// </summary>
public sealed class DelegationOptions
{
    /// <summary>
    /// Upper bound on the full-diff text stored per delegation event.
    /// Bounds an unbounded/attacker-shaped diff before it is buffered into
    /// the row. Default 32 KiB chars.
    /// </summary>
    public int MaxResultDiffChars { get; set; } = 32 * 1024;

    /// <summary>
    /// Upper bound on the diff-stat text stored per delegation event.
    /// Default 8 KiB chars.
    /// </summary>
    public int MaxDiffStatChars { get; set; } = 8 * 1024;

    /// <summary>
    /// Upper bound on the park reason stored per delegation event.
    /// Default 2 KiB chars.
    /// </summary>
    public int MaxReasonChars { get; set; } = 2 * 1024;
}
