namespace CodeyBox.Core;

/// <summary>
/// Hot-reloadable operator knobs for external work synchronisation. All
/// operational values live here — never as literals in source — so a change
/// takes effect without restart or code change. Bound under
/// <c>CodeyBox:WorkSync</c>.
/// </summary>
public sealed class WorkSyncOptions
{
    /// <summary>Section path under <c>CodeyBox</c>.</summary>
    public const string SectionName = "WorkSync";

    /// <summary>Master switch. When false, ingestion refuses everything and trackers post nothing.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Upstream logins that identify CodeyBox itself (e.g.
    /// <c>codeybox[bot]</c>). Compared by exact match in the loop guard.
    /// </summary>
    public List<string> CodeyBoxServiceLogins { get; set; } = ["codeybox[bot]"];

    /// <summary>
    /// Priority assigned to ingested items. The external system cannot set
    /// priority: this operator-chosen default is the only input, clamped to
    /// <see cref="MaxIngestedPriority"/>.
    /// </summary>
    public int DefaultIngestedPriority { get; set; }

    /// <summary>
    /// Operator-configured bound on ingested-item priority. The ingested
    /// priority is clamped to <c>[-MaxIngestedPriority, MaxIngestedPriority]</c>.
    /// </summary>
    public int MaxIngestedPriority { get; set; } = 100;

    /// <summary>Upper bound enforced on ingested title/body length (chars) before use.</summary>
    public int MaxIngestedBodyChars { get; set; } = 64 * 1024;

    /// <summary>Maximum external items accepted from a single poll. Enforced before buffering.</summary>
    public int MaxItemsPerPoll { get; set; } = 100;

    /// <summary>
    /// Explicit state-to-status declaration keyed by <see cref="WorkItemState"/>
    /// name (e.g. <c>{ "Working": "in progress", "Done": "done" }</c>). A
    /// state with no entry is reported as unmapped, never guessed.
    /// </summary>
    public Dictionary<string, string> StateMapping { get; set; } = new();

    /// <summary>
    /// What happens to in-flight work when the ingestion signal is removed
    /// upstream. Removing the signal is meaningful and never silently
    /// ignored: the default parks the item for operator review with a visible
    /// note. See <see cref="SignalRemovalBehavior"/>.
    /// </summary>
    public SignalRemovalBehavior OnSignalRemoved { get; set; } =
        SignalRemovalBehavior.ParkForOperatorReview;

    /// <summary>
    /// Resolves the service-login set with ordinal-ignore-case semantics for
    /// the loop guard.
    /// </summary>
    public HashSet<string> ServiceLoginSet() =>
        new(CodeyBoxServiceLogins.Where(s => !string.IsNullOrWhiteSpace(s)), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// What happens to work already in flight when the ingestion signal is
/// removed upstream (e.g. the label is taken off, the item is unassigned).
/// </summary>
public enum SignalRemovalBehavior
{
    /// <summary>
    /// Work continues; a tracker note records that the signal was removed.
    /// The removal stays visible in both systems' audit trails.
    /// </summary>
    ContinueAndAnnotate,
    /// <summary>
    /// The item parks in <see cref="WorkItemState.NeedsOperatorInput"/> with a
    /// note naming the removed signal. The operator resumes or cancels it.
    /// </summary>
    ParkForOperatorReview,
    /// <summary>
    /// The item is cancelled with a note naming the removed signal.
    /// </summary>
    CancelWorkItem,
}
