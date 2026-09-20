namespace CodeyBox.Core;

/// <summary>
/// Inbound contract: discovers externally-tracked work that carries the
/// operator-configured ingestion signal, and describes it as an
/// <see cref="ExternalWorkItem"/> for <c>WorkIngestionService</c> to turn into
/// a work item. Most providers implement this and <see cref="IWorkTracker"/>
/// against one backend; the interfaces stay separate so a read-only or
/// write-only integration is possible.
/// </summary>
public interface IWorkSource
{
    /// <summary>
    /// Namespace under which ingested items record their external id in
    /// <see cref="WorkItem.ExternalIds"/> (e.g. <c>linear</c>, <c>jira</c>).
    /// Lowercase alphanumeric/dash form per
    /// <see cref="Validation.ValidateExternalIdNamespace"/>.
    /// </summary>
    string Namespace { get; }

    /// <summary>Declares which delivery paths this source supports.</summary>
    WorkSourceCapabilities Capabilities { get; }

    /// <summary>
    /// The operator-configured signal that authorises ingestion for this
    /// source (e.g. label <c>codeybox</c>, assignee <c>codeybox[bot]</c>).
    /// An item without this signal is never ingested.
    /// </summary>
    WorkSignal RequiredSignal { get; }

    /// <summary>
    /// Polls the external system for signalled work. Used by deployments
    /// that cannot receive inbound webhooks, and for manual re-sync. Must be
    /// safe to overlap: ingestion is idempotent on the external id, so
    /// redelivered or re-polled items converge on one work item.
    /// </summary>
    IAsyncEnumerable<ExternalWorkItem> PollAsync(CancellationToken ct = default);

    /// <summary>
    /// Parses an inbound webhook body into a candidate item. The caller MUST
    /// verify the body's authenticity (HMAC signature over the raw bytes)
    /// BEFORE invoking this method — mirroring the
    /// <c>InteractionEndpoints</c> ordering rule — because parsing assigns
    /// meaning to untrusted bytes. Returns null when the payload carries no
    /// candidate work (e.g. an event type this source does not ingest).
    /// Sources without webhook support (<see
    /// cref="WorkSourceCapabilities.SupportsWebhooks"/> false) throw
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    ExternalWorkItem? ParseVerifiedWebhookBody(string verifiedBody);
}

/// <summary>Declares which delivery paths a work source supports.</summary>
public sealed record WorkSourceCapabilities(
    /// <summary>True when the source can receive inbound webhooks.</summary>
    bool SupportsWebhooks,
    /// <summary>True when the source can be polled. A source that cannot do webhooks polls.</summary>
    bool SupportsPolling);

/// <summary>
/// The explicit, operator-configured signal in the external system whose
/// presence authorises ingestion — e.g. a label, an assignee, or a status.
/// Applying the signal is the authorisation: it is performed by someone who
/// already holds permission upstream and leaves an audit trail there.
/// Nothing in the issue's own content (title, description, comments) can
/// cause ingestion or widen what is ingested.
/// </summary>
public sealed record WorkSignal(
    /// <summary>Which upstream field carries the signal.</summary>
    WorkSignalKind Kind,
    /// <summary>
    /// Exact value that must be present (ordinal-ignore-case exact match —
    /// never substring). E.g. label <c>codeybox</c>.
    /// </summary>
    string Value);

/// <summary>Which upstream field carries the ingestion signal.</summary>
public enum WorkSignalKind
{
    /// <summary>A tag/label applied to the item.</summary>
    Label,
    /// <summary>Assignment to a service account.</summary>
    Assignee,
    /// <summary>A workflow status/state value.</summary>
    Status,
}

/// <summary>
/// Externally-tracked work observed by an <see cref="IWorkSource"/>, before
/// ingestion. All content fields are UNTRUSTED input: title and body become
/// the work item prompt, and nothing else on the resulting work item may be
/// influenced by them. There is deliberately no agent, credential, grant,
/// capability, or priority field — those are unrepresentable here so no
/// provider can forward them, however shaped the upstream tool is.
/// </summary>
public sealed record ExternalWorkItem
{
    /// <summary>Provider namespace; must equal the source's <see cref="IWorkSource.Namespace"/>.</summary>
    public required string Namespace { get; init; }

    /// <summary>Stable id in the external system (issue key, number, etc.).</summary>
    public required string ExternalId { get; init; }

    /// <summary>Project the ingested work item belongs to (operator-mapped).</summary>
    public required ProjectId ProjectId { get; init; }

    /// <summary>Upstream title. Truncated to 200 chars at ingestion.</summary>
    public required string Title { get; init; }

    /// <summary>Upstream description/body. Truncated to the configured cap at ingestion.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>
    /// Signals observed on the item right now. Ingestion requires
    /// <see cref="IWorkSource.RequiredSignal"/> to be among them.
    /// </summary>
    public IReadOnlyList<WorkSignal> PresentSignals { get; init; } = [];

    /// <summary>
    /// Login of the actor who last modified the item upstream, when known.
    /// Used by the loop guard to recognise CodeyBox-authored updates.
    /// </summary>
    public string? LastActorLogin { get; init; }

    /// <summary>
    /// True when the item currently carries the ingestion signal. Providers
    /// that only surface signalled items return true; providers that surface
    /// all changed items (webhook <c>unlabeled</c> events, broad polls)
    /// compute it from <see cref="PresentSignals"/>.
    /// </summary>
    public bool HasSignal { get; init; } = true;
}
