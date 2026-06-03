using System.Text.Json.Serialization;
using CodeyBox.Core;

namespace CodeyBox.Webhooks;

/// <summary>
/// Single source of truth for the webhook + SSE event schema. Exposed verbatim
/// by <c>GET /events/schema</c> so downstream trackers can validate at startup
/// and enable schema-version-strict mode without scraping <c>docs/EVENT_SCHEMA.md</c>.
///
/// <para>Evolution is additive-only — see <see cref="EvolutionRules"/>. The
/// in-repo doc <c>docs/EVENT_SCHEMA.md</c> must mirror this object; the
/// <c>EventSchemaDocSyncTests</c> guard against drift.</para>
/// </summary>
public static class EventSchema
{
    /// <summary>Current schema version. Bumped per the rules below.</summary>
    public const string CurrentVersion = WebhookEvent.CurrentSchemaVersion;
    private const string InitialVersion = "1.0";

    /// <summary>
    /// Returns the schema document. Plain value type so it serialises cleanly
    /// from the endpoint and is cheap to assert against from tests.
    /// </summary>
    public static EventSchemaDocument GetSchema() => new(
        EventSchemaVersion: CurrentVersion,
        EvolutionRules: new EvolutionRules(
            AdditiveOnly: true,
            MinorBump: ["new optional field", "new event type", "new details field"],
            MajorBump: ["rename of an existing field", "removal of an existing field", "type change of an existing field"]),
        Envelope: BuildEnvelope(),
        EventTypes: BuildEventTypes());

    private static IReadOnlyDictionary<string, FieldSchema> BuildEnvelope() => new Dictionary<string, FieldSchema>
    {
        ["eventSchemaVersion"] = new("string", "Semver schema version this payload conforms to.", InitialVersion),
        ["eventType"] = new("string", "Stable event identifier. Identical to legacy `event`.", InitialVersion),
        ["emittedAt"] = new("string", "ISO-8601 UTC timestamp stamped at event construction. Alias of `occurredAt` at schema 1.0; reserved for differentiation from `occurredAt` in a future minor bump.", InitialVersion),
        ["event"] = new("string", "Legacy alias of `eventType`, retained for backwards compatibility.", InitialVersion),
        ["occurredAt"] = new("string", "ISO-8601 UTC wall-clock time the event was generated.", InitialVersion),
        ["workItem"] = new("object|null", "Work-item context; null for queue/agent/sandbox-level events.", InitialVersion),
        ["project"] = new("object|null", "Project context; null for non-project-scoped events.", InitialVersion),
        ["release"] = new("object|null", "Release context; populated only for `release.*` events.", InitialVersion),
        ["details"] = new("object|null", "Event-specific payload. Shape depends on `eventType`.", InitialVersion),
        ["usage"] = new("object|null", "Token usage / cost for the most recent iteration. Omitted when unavailable.", InitialVersion),
        ["usageTotal"] = new("object|null", "Cumulative token usage / cost across every iteration. Omitted when unavailable.", InitialVersion),
    };

    private static IReadOnlyDictionary<string, EventTypeSchema> BuildEventTypes()
        => KnownEventTypes
            .Select(name => new KeyValuePair<string, EventTypeSchema>(name, new EventTypeSchema(InitialVersion)))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>
    /// Authoritative list of every event the pipeline can emit at this schema
    /// version. New types append to this list (minor bump); removals require a
    /// major bump (don't do this without operator notice).
    /// </summary>
    public static readonly IReadOnlyList<string> KnownEventTypes =
    [
        // Queue-level
        "queue.paused",
        "queue.resumed",
        // Worker pool / dispatcher health
        "worker_pool.stalled",
        "worker_pool.restart_required",
        // Agent-level (smoke probe, fallback)
        "agent.smoke_failed",
        "agent.smoke_recovered",
        "agent.fallback",
        // Sandbox lifecycle (leak reaper only — provisioning is audit-log not webhook)
        "sandbox.leak_detected",
        "sandbox.leak_disposed",
        "sandbox.leak_dispose_failed",
        // Project-level
        "project.queue_paused",
        "project.queue_resumed",
        "project.budget_warning",
        "project.budget_exceeded",
        "project.budget_recovered",
        // Work-item state transitions
        "work_item.working",
        "work_item.work_complete",
        "work_item.auditing",
        "work_item.audit_iteration",
        "work_item.audit_passed",
        "work_item.audit_failed",
        "work_item.reworking",
        "work_item.merging",
        "work_item.merged",
        "work_item.merge_conflict_resolution_failed",
        "work_item.upstream_pushing",
        "work_item.pull_request_opened",
        "work_item.done",
        "work_item.failed",
        "work_item.cancelled",
        "work_item.resumed",
        "work_item.needs_operator_input",
        "work_item.waiting_for_quota_reset",
        // Work-item lifecycle / operator interaction
        "work_item.agent_stuck",
        "work_item.auto_retry",
        "work_item.recovered",
        "work_item.suggestion",
        "work_item.question_asked",
        "work_item.question_answered",
        "work_item.question_dismissed",
        "budget.deferred",
        // Release lifecycle
        "release.created",
        "release.closed",
        "release.abandoned",
        "release.reopened",
        "release.has_failed_work_items",
        "release.in_review",
        "release.deep_audit_iteration_complete",
        "release.deep_audit_remediation_dispatched",
        "release.work_item_added",
        "release.published",
        "release.failed",
        "release.sync_conflict",
        // Upstream/forge state surfaced by background sweeps (not state transitions)
        "upstream.pr_stale_base",
    ];

    /// <summary>
    /// Validates that an event payload carries the three required envelope
    /// fields and that <c>evt.Event</c> is one of <see cref="KnownEventTypes"/>.
    /// Used by the test-mode validator to fail fast on schema drift — a new
    /// event name added at an emit site without a matching entry in
    /// <see cref="KnownEventTypes"/> would otherwise ship without breaking any
    /// test even though <c>GET /events/schema</c> tells trackers the new name
    /// does not exist. Returns null on success; otherwise a human-readable
    /// error.
    /// </summary>
    public static string? ValidateEnvelope(WebhookEvent? evt)
    {
        if (evt is null) return "event is null";
        // eventType is validated first so the per-field error messages can
        // safely interpolate evt.Event without producing "event '': ...".
        if (string.IsNullOrEmpty(evt.Event))
            return "eventType is required";
        if (string.IsNullOrEmpty(evt.EventSchemaVersion))
            return $"event '{evt.Event}': eventSchemaVersion is required";
        if (evt.EmittedAt == default)
            return $"event '{evt.Event}': emittedAt is required";
        if (!KnownEventTypesSet.Contains(evt.Event))
            return $"event '{evt.Event}': eventType is not in EventSchema.KnownEventTypes — add it to the list (and docs/EVENT_SCHEMA.md) or fix the emit-site spelling";
        return null;
    }

    private static readonly HashSet<string> KnownEventTypesSet =
        new(KnownEventTypes, StringComparer.Ordinal);
}

public sealed record EventSchemaDocument(
    string EventSchemaVersion,
    EvolutionRules EvolutionRules,
    IReadOnlyDictionary<string, FieldSchema> Envelope,
    IReadOnlyDictionary<string, EventTypeSchema> EventTypes);

public sealed record EvolutionRules(
    bool AdditiveOnly,
    IReadOnlyList<string> MinorBump,
    IReadOnlyList<string> MajorBump);

public sealed record FieldSchema(
    string Type,
    string Description,
    [property: JsonPropertyName("introducedIn")] string IntroducedIn);

public sealed record EventTypeSchema(
    [property: JsonPropertyName("introducedIn")] string IntroducedIn);
