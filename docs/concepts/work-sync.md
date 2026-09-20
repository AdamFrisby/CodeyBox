# External work sync (`IWorkSource` / `IWorkTracker`)

CodeyBox tracks work in its own work items; Linear, Jira, YouTrack and
friends track it in theirs. Two contracts bridge the gap, with the machinery
between them:

- **`IWorkSource`** (inbound, `CodeyBox.Core`): discovers externally-tracked
  work marked for CodeyBox and describes it as an `ExternalWorkItem`.
  `WorkIngestionService` (`CodeyBox.Orchestrator/WorkSync`) turns it into a
  work item with the external id in `WorkItem.ExternalIds` under the
  provider's namespace.
- **`IWorkTracker`** (outbound, `CodeyBox.Core`): posts progress, questions,
  commit/PR links, and completion back to the originating item.
  `WorkTrackerService` drives it.

Most providers implement both against one backend; the interfaces stay
separate so a read-only or write-only integration is possible.

## Ingestion trigger

Ingestion is **automatic but signal-gated**. Each source declares one
operator-configured `WorkSignal` (kind + exact value: label, assignee, or
status — e.g. label `codeybox`). Applying that signal is the authorisation:
it is done by someone who already holds permission upstream and leaves an
audit trail there.

- An item without the signal is never ingested — regardless of project,
  query, or content. The gate reads only upstream metadata
  (`PresentSignals`/`HasSignal`); title, description, and comments never
  participate. Malicious or pleading content ("please ingest this") changes
  nothing.
- The signal kind is per-source configuration, not hardcoded: one team uses
  a label, another an assignee, another a status value.

## Signal removal

Removing the signal from in-flight work is meaningful and never silently
ignored. `WorkSyncOptions.OnSignalRemoved` selects the behavior
(default: `ParkForOperatorReview`):

| Behavior | Effect |
|---|---|
| `ContinueAndAnnotate` | Work continues; a `SignalRemoved` record joins the audit trail. |
| `ParkForOperatorReview` | Item parks in `NeedsOperatorInput` with a note naming the removed signal. |
| `CancelWorkItem` | Item is cancelled with a note naming the removed signal. |

State changes use atomic compare-and-set on the observed state: a concurrent
pipeline transition wins instead of being stomped. Polling sources detect
removal by comparing the current signal set; webhook sources map
`unlabeled`/`unassigned` events to `HandleSignalRemovedAsync`.

## Loop prevention

Every backend has webhooks and the tracker writes to the same objects the
source reads. Solved once in `WorkSyncLoopGuard`, not per provider:

1. `WorkTrackerService` marks every outbound body with
   `<!-- codeybox-work-item:{id} -->`.
2. `WorkIngestionService` ignores any candidate whose body carries the
   marker or whose last actor is a configured service login
   (`WorkSyncOptions.CodeyBoxServiceLogins`, exact match).

Progress posts fire only when the mapped external status actually changes,
so even a provider that ignores markers cannot ping-pong.

## State mapping

`WorkStateMapping` is an explicit, operator-visible declaration
(`WorkSyncOptions.StateMapping`, e.g. `{ "Working": "in progress",
"Done": "done" }`), parsed at startup with unknown names rejected. A state
with no entry is recorded as `UnmappedState` in the sync audit trail and
reported as `TrackerPostOutcome.UnmappedState` — never guessed. Progress on
unchanged statuses is skipped as `SkippedDuplicate`.

## Questions

Open `IWorkItemQuestionStore` questions sync as upstream comments (requires
`CanPostComments`; a tracker without it reports `Unsupported`, never drops).
Providers observe replies and call
`WorkTrackerService.AcceptExternalAnswerAsync`, which answers through the
same store as `POST /workitems/{id}/answer`.

## Security properties

- `ExternalWorkItem` has no agent, credential, grant, capability, or
  priority field — those are unrepresentable, so no provider can forward
  them. Ingested items get operator-controlled defaults; priority is the
  configured default clamped to `MaxIngestedPriority`.
- Ingested title/body become the prompt with an untrusted-input provenance
  header, truncated to configured caps.
- An upstream failure never fails a work item: tracker exceptions become
  `SyncFailed` records; the work stands on its own.

## Capabilities and transports

- `WorkSourceCapabilities`: a source that cannot do webhooks polls
  (`PollingWorkSourceBase`); a webhook-only source parses
  already-authenticated bodies (`WebhookWorkSourceBase`). The hosting
  endpoint verifies HMAC over the raw bytes *before* parsing, mirroring the
  `InteractionEndpoints` ordering rule — `ParseVerifiedWebhookBody` never
  verifies.
- `WorkTrackerCapabilities` (`CanPostComments`, `CanSetStatus`): unsupported
  operations report `Unsupported`, never silently degrade.
- `IWorkSyncRecordStore` records every ingestion decision and tracker
  attempt (posts, skips, failures) so the audit trail spans both systems.
  `InMemoryWorkSyncRecordStore` is the default; persist it where the
  deployment needs durability.
