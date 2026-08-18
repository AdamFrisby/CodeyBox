namespace CodeyBox.Core;

/// <summary>
/// Represents a single pipeline event to be dispatched to configured webhook endpoints.
/// Constructed by the pipeline and published via <see cref="IWebhookDispatcher"/>.
///
/// <para><see cref="WorkItem"/> and <see cref="Project"/> are null for agent-level
/// events (e.g. <c>agent.smoke_failed</c>) that have no associated work item.</para>
/// </summary>
public sealed record WebhookEvent
{
    /// <summary>
    /// Semver string identifying the event-payload schema this event was emitted
    /// under. Bumped by additive-only rules in <c>docs/reference/events.md</c>: new
    /// fields or event types are minor bumps; renames or removals are major.
    /// Trackers opting into strict mode reject majors they don't recognise.
    /// </summary>
    public const string CurrentSchemaVersion = "1.5";

    public Guid DeliveryId { get; init; } = Guid.NewGuid();

    /// <summary>Dot-separated event name, e.g. "work_item.audit_passed".</summary>
    public required string Event { get; init; }

    /// <summary>Wall-clock time the event was generated.</summary>
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// ISO-8601 UTC timestamp on the envelope. At schema 1.0 the default value
    /// is a separate <c>DateTimeOffset.UtcNow</c> read taken at construction —
    /// it is a stable alias of <see cref="OccurredAt"/> in practice but the
    /// two reads can differ by a handful of ticks. Kept as a distinct envelope
    /// field so future versions can differentiate generation vs. emission time
    /// without a breaking rename; a dispatcher that wants a true "leaves the
    /// pipeline" stamp can override it explicitly at publish time.
    /// </summary>
    public DateTimeOffset EmittedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Schema version of the event payload. Defaults to <see cref="CurrentSchemaVersion"/>;
    /// tests can pin a specific value.
    /// </summary>
    public string EventSchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Null for agent-level events that have no associated work item.</summary>
    public WorkItem? WorkItem { get; init; }

    /// <summary>Null for agent-level events that have no associated project.</summary>
    public Project? Project { get; init; }

    /// <summary>Optional event-specific payload; serialised as-is into the "details" field.</summary>
    public object? Details { get; init; }

    /// <summary>
    /// Set for release-scoped events (event names starting with "release.").
    /// Null for work-item and agent events.
    /// </summary>
    public Release? Release { get; init; }

    /// <summary>
    /// Token usage and estimated cost for the most recent iteration of the work
    /// item, set on iteration- and completion-boundary events. Null when no cost
    /// data is available for this event (no extractor for the agent, or the
    /// event does not pertain to a single work item).
    /// </summary>
    public WorkItemIterationUsage? Usage { get; init; }

    /// <summary>
    /// Cumulative token usage and estimated cost across every iteration of the
    /// work item. Paired with <see cref="Usage"/>; null under the same conditions.
    /// </summary>
    public WorkItemUsageTotal? UsageTotal { get; init; }

    /// <summary>
    /// Current monotonic prompt-generation counter on the work item at terminal
    /// state. Top-level (not in <see cref="Details"/>) so JobTrack and other
    /// trackers can read <c>payload.promptRevision</c> directly without first
    /// resolving the polymorphic details object. Null for non-terminal events
    /// and for any event whose work item predates the prompt-revision feature.
    /// </summary>
    public int? PromptRevision { get; init; }

    /// <summary>
    /// Revision that was active when the LAST dispatched iteration began. Lets
    /// a tracker tell "agent finished against the latest prompt" from "agent
    /// finished against a stale prompt; the operator's mid-iteration edit was
    /// not yet visible." Null when no iteration rows exist (e.g. the item
    /// failed before any dispatch).
    /// </summary>
    public int? RevisionAtCompletion { get; init; }

    /// <summary>
    /// Convenience flag: <see cref="RevisionAtCompletion"/> equals
    /// <see cref="PromptRevision"/>. False means the last iteration ran against
    /// a stale prompt and the tracker should offer a one-click re-run. Null
    /// when <see cref="RevisionAtCompletion"/> is null.
    /// </summary>
    public bool? RevisionMatches { get; init; }
}
