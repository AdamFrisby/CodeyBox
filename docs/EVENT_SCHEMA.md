# Event payload schema

This file is the authoritative reference for the JSON envelope that CodeyBox
emits on every webhook delivery and every Server-Sent-Events frame. Trackers
that integrate against multiple CodeyBox installations should validate their
expectations against this schema at startup using `GET /events/schema`, which
returns the same envelope description as a machine-readable JSON document.

The schema mirrors what is wired up in `src/CodeyBox.Webhooks/EventSchema.cs`.
The two are kept in lockstep by `EventSchemaDocSyncTests` — if you edit one
without the other, CI fails.

---

## Current version

```
eventSchemaVersion = "1.5"
```

The `eventSchemaVersion` string is semver (`major.minor`). Trackers should
inspect this string — or the `X-CodeyBox-Schema-Version` HTTP header on
webhook deliveries — to decide whether to accept the payload.

The single canonical source in code is `WebhookEvent.CurrentSchemaVersion`
(in `src/CodeyBox.Core/WebhookEvent.cs`). Anywhere else that needs the
compile-time value — payload defaults, tests, validators — should reference
that const rather than declare its own. The dispatcher does not own the
schema version; the event type does.

---

## Envelope

Every webhook + SSE payload is a JSON object with this shape:

```jsonc
{
  // ── Required (since 1.0) ─────────────────────────────────────
  "eventSchemaVersion": "1.5",                  // semver string
  "eventType":          "work_item.done",       // stable identifier
  "emittedAt":          "2026-05-18T12:34:56.789+00:00",

  // ── Legacy aliases (kept for backwards compatibility) ────────
  "event":              "work_item.done",       // identical to eventType
  "occurredAt":         "2026-05-18T12:34:56.789+00:00",

  // ── Context (any may be null depending on event scope) ───────
  "workItem":   { /* work-item context */ } | null,
  "project":    { /* project context */   } | null,
  "release":    { /* release context */   } | null,
  "details":    { /* event-specific      */ } | null,
  "usage":      { /* per-iteration cost  */ } | null,
  "usageTotal": { /* cumulative cost     */ } | null
}
```

### Required envelope fields (since 1.0)

| Field | Type | Description |
|---|---|---|
| `eventSchemaVersion` | string (semver) | Schema version this payload conforms to. |
| `eventType` | string | Stable event identifier, e.g. `work_item.done`. |
| `emittedAt` | string (ISO-8601 UTC) | Stamped at event construction. Alias of `occurredAt` at schema `1.0`; reserved for differentiation from `occurredAt` in a future minor bump. |

`eventType` is identical to the legacy `event` field. `emittedAt` is a stable
alias of `occurredAt` at schema `1.0` — both are stamped at event construction
and differ only by the handful of ticks between two `UtcNow` reads. The names
are kept separate so future minor versions can differentiate "generated" from
"emitted" without a breaking rename. Trackers should prefer the new names; the
legacy names will remain for the lifetime of the `1.x` series.

### Context fields

See [`webhooks.md`](webhooks.md) for the shape of `workItem`, `project`,
`release`, `usage`, `usageTotal`, and per-event `details` payloads.

---

## Event types

Every event the pipeline emits at this schema version is listed below with
the version it was introduced in. New event types are added in minor bumps.
`GET /events/schema` returns the same list as a JSON object so trackers can
sanity-check at startup that they know how to handle every type they
subscribe to.

| `eventType` | Introduced in | Notes |
|---|---|---|
| `queue.paused` | 1.0 | Operator paused the global pickup queue. |
| `queue.resumed` | 1.0 | Operator resumed the global pickup queue. |
| `worker_pool.stalled` | 1.2 | Worker pool had free slots and runnable work with an available agent, but no worker spawn occurred past the configured watchdog threshold; self-recovery was attempted. |
| `worker_pool.restart_required` | 1.2 | Worker-pool watchdog self-recovery did not restore dispatch progress; operator restart is required. |
| `agent.smoke_failed` | 1.0 | Credential smoke probe failed at startup or pickup, runtime auth/login-prompt detection benched the agent, or fast-fail circuit breaker excluded the agent after consecutive sub-threshold non-zero exits. |
| `agent.smoke_recovered` | 1.0 | Previously-excluded agent recovered: a subsequent smoke probe passed. |
| `agent.fallback` | 1.0 | Agent class router fell back to an alternate agent. |
| `agent.paused` | 1.3 | Operator paused new dispatch to one agent kind or pooled instance route. |
| `agent.resumed` | 1.3 | Operator resumed dispatch to one agent kind or pooled instance route. |
| `sandbox.leak_detected` | 1.0 | Leaked `codeybox-*` sandbox detected by reaper. |
| `sandbox.leak_disposed` | 1.0 | Reaper successfully disposed a leaked sandbox. |
| `sandbox.leak_dispose_failed` | 1.0 | Reaper failed to dispose a leaked sandbox. |
| `project.queue_paused` | 1.0 | Per-project queue paused. |
| `project.queue_resumed` | 1.0 | Per-project queue resumed. |
| `project.budget_warning` | 1.0 | Project crossed the cost warning threshold. |
| `project.budget_exceeded` | 1.0 | Project crossed the cost hard cap. |
| `project.budget_recovered` | 1.0 | Project spend dropped below the warning threshold. |
| `work_item.planning` | 1.5 | Planning-only agent turn started. |
| `work_item.plan_review` | 1.5 | Planning artifact entered review. |
| `work_item.plan_approved` | 1.5 | Planning artifact was approved; implementation may start. |
| `work_item.working` | 1.0 | Agent starts the work phase. |
| `work_item.work_complete` | 1.0 | Work phase succeeded. |
| `work_item.auditing` | 1.0 | Audit phase started. |
| `work_item.audit_iteration` | 1.0 | One audit iteration completed (pass or fail). |
| `work_item.audit_passed` | 1.0 | All audit iterations passed. |
| `work_item.audit_failed` | 1.0 | Audit did not converge within max iterations. |
| `work_item.reworking` | 1.0 | Agent starts rework after blocking findings. |
| `work_item.merging` | 1.0 | Merge phase started. |
| `work_item.merged` | 1.0 | Merge phase succeeded. |
| `work_item.merge_conflict_resolution_failed` | 1.0 | Auto-resolution of a merge conflict failed. |
| `work_item.upstream_pushing` | 1.0 | Upstream push started. |
| `work_item.pull_request_opened` | 1.0 | GitHub pull request opened for the work branch. |
| `work_item.done` | 1.0 | Work item completed successfully. |
| `work_item.failed` | 1.0 | Work item failed (unrecoverable). |
| `work_item.cancelled` | 1.0 | Work item cancelled via API. |
| `work_item.resumed` | 1.0 | Operator-cancelled work item resumed via `POST /workitems/{id}/resume`. |
| `work_item.needs_operator_input` | 1.0 | Work item parked awaiting operator answers. |
| `work_item.waiting_for_quota_reset` | 1.0 | Work item parked until quota reset window. |
| `work_item.waiting_for_agent_resume` | 1.3 | Work item parked because its only eligible agent is paused. |
| `work_item.waiting_for_transient_retry` | 1.4 | Work item parked until the transient transport/network retry backoff expires. |
| `work_item.agent_stuck` | 1.0 | Stuck-agent probe killed a hung agent. |
| `work_item.auto_retry` | 1.0 | Quota auto-retry re-queued a failed item. |
| `work_item.recovered` | 1.0 | Dead-worker reaper recovered an item with a state-changing transition. |
| `work_item.suggestion` | 1.0 | Agent emitted a suggestion entry. |
| `work_item.question_asked` | 1.0 | Agent parked an item waiting for an answer. |
| `work_item.question_answered` | 1.0 | Operator answered a parked question. |
| `work_item.question_dismissed` | 1.0 | Operator dismissed a parked question. |
| `budget.deferred` | 1.0 | Work-item start deferred by a per-project budget cap. |
| `release.created` | 1.0 | New release created. |
| `release.closed` | 1.0 | Release transitioned `open → closed`. |
| `release.abandoned` | 1.0 | Release abandoned. |
| `release.reopened` | 1.0 | Failed release re-opened. |
| `release.has_failed_work_items` | 1.0 | Release closed but some work items failed. |
| `release.in_review` | 1.0 | Release transitioned `closed → in_review`; deep audit starting. |
| `release.deep_audit_iteration_complete` | 1.0 | One deep audit iteration finished. |
| `release.deep_audit_remediation_dispatched` | 1.0 | Remediation work item auto-created for a deep-audit iteration. |
| `release.work_item_added` | 1.0 | Work item linked to a release. |
| `release.published` | 1.0 | Release merged to main. |
| `release.failed` | 1.0 | Deep audit exceeded max iterations. |
| `release.sync_conflict` | 1.0 | Conflict merging `main` into a release branch. |
| `upstream.pr_stale_base` | 1.1 | A CodeyBox-authored PR has been left unmergeable by motion on the base branch; needs operator rebase. |

See [`webhooks.md`](webhooks.md) for the per-event `details` payload shapes.
Schema 1.1 adds the sandbox leak `reason` details field. Schema 1.2 adds
worker-pool health watchdog events for dispatcher stalls and restart
escalation. Schema 1.3 adds per-agent pause/resume and agent-pause waiting
events. Schema 1.4 adds transient transport retry waiting events. Schema 1.5
adds planning-phase transition events.

---

## Evolution rules (additive-only)

CodeyBox follows additive-only schema evolution so trackers can opt in to
strict version-major checking without fear of silent breaks.

| Change | Version bump |
|---|---|
| Add a new optional envelope field | minor (`1.0` → `1.1`) |
| Add a new event type | minor |
| Add a new field inside a `details` block | minor |
| Rename an existing field | **major** (`1.x` → `2.0`) — avoid |
| Remove an existing field or event type | **major** — avoid |
| Change the JSON type of an existing field | **major** — avoid |

Major bumps must be telegraphed to operators with at least one release of
deprecation notice. The default posture is to never do them.

---

## Opting in to strict-major handling (tracker side)

CodeyBox does not gate deliveries by tracker version — every endpoint
configured under `CodeyBox:Webhooks` receives every matching event regardless
of schema version. The opt-in is on the tracker:

1. At startup, call `GET /events/schema` against the CodeyBox API. The
   response includes the current `eventSchemaVersion` and the full list of
   known `eventType`s.
2. Decide which `major` versions you accept. The conservative default is
   "only the major I was tested against".
3. On every webhook delivery, inspect either:
   - the `X-CodeyBox-Schema-Version` HTTP header, or
   - the `eventSchemaVersion` field in the JSON body.
4. If the major doesn't match your allow-list, log and reject. Returning a
   `400` causes CodeyBox to back off and surface the failure in
   `WebhookDeliveryFailed` audit-log entries — operators will notice.

Example (Python):

```python
ACCEPTED_MAJORS = {"1"}

def handle(headers, body_bytes):
    version = headers.get("X-CodeyBox-Schema-Version", "0")
    major = version.split(".", 1)[0]
    if major not in ACCEPTED_MAJORS:
        return Response("unsupported eventSchemaVersion", status=400)
    payload = json.loads(body_bytes)
    ...
```

---

## Validating during development

`EventSchema.ValidateEnvelope(WebhookEvent)` (in `CodeyBox.Webhooks`) is the
test-mode validator. The test-assembly module initialiser in
`tests/CodeyBox.Tests/TestAssemblyInitializer.cs` flips
`WebhookEventBroadcaster.StrictSchemaValidationForTests = true`, which routes
every event published through `BroadcastingWebhookDispatcher` past the
validator. The direct unit tests in `tests/CodeyBox.Tests/EventSchemaEnvelopeTests.cs`
pin the envelope contract and exercise each ValidateEnvelope branch.

If you add a new event type, follow this checklist:

1. Append the event-type string to `EventSchema.KnownEventTypes`.
2. Add a row in the "Event types" table above.
3. If the change is additive at the same major version, update
   `WebhookEvent.CurrentSchemaVersion` for the minor bump and document the
   newly introduced field or event type.
4. If the change is a rename/removal/type-change, that is a major bump:
   coordinate with operators first, then update `CurrentSchemaVersion` and
   set `introducedIn` on the affected entries.

---

## Out of scope (for now)

- Auto-generated client SDKs from the schema — tracked as a follow-up if a
  consumer asks for it. The current shape is small enough that hand-rolled
  validators work fine.
