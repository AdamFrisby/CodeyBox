# CodeyBox: Shortcut Work Sync

One project, one plugin implementing `IWorkSource` (inbound) and `IWorkTracker`
(outbound) against the Shortcut REST API v3. Off unless an operator enables it:
the assembly loads only when allowlisted **and** named in `Plugins:Enabled`,
and it polls/posts nothing until `Enabled=true` in its own section.

## What it does

1. **Ingestion** — polls stories via `POST /api/v3/stories/search` (and, when
   `IngestEpics=true`, lists epics via `GET /api/v3/epics`) plus parses
   Shortcut outgoing webhooks for entities carrying the operator-configured
   signal (default: label `codeybox`; assignment to a service account or a
   workflow-state value work too). Signalled stories become work items with
   `sc-{id}` in `ExternalIds["shortcut"]` (Shortcut stories carry no human
   key, so the derived numeric key is the stable readable id). The same key
   converges webhook redelivery, polling overlap, and manual re-sync onto one
   item — never a duplicate.
2. **Sync back** — posts progress on mapped state transitions, surfaces open
   questions as comments, reports commit SHAs / PR links, and reports terminal
   completion or failure by moving the story to the mapped workflow state.
3. **State mapping** is an explicit operator declaration (`StateMapping`).
   An unmapped state returns `UnmappedState` and writes nothing — never guessed.
4. **Loop safety** — every outbound body carries
   `<!-- codeybox-work-item:{id} -->` (applied by `WorkTrackerService`); our
   own comments are recognised by that marker or by the service login and
   ignored on the way back in. Progress posts only on changed statuses.
   Shortcut's entity-level webhook actions make this precise: a
   CodeyBox-authored comment is distinguishable from a human one both by the
   marker in the comment text and by the actor identity on the delivery.
5. **Webhooks and polling both.** Shortcut webhooks are created in the
   Shortcut UI (Settings → API → Outgoing Webhooks) — there is no
   webhook-management API, so `ManageWebhooks=true` only validates the delivery
   URL and logs the registration expectation; it never assumes a registration
   persists. Deployments without inbound traffic skip webhooks entirely and
   poll. Polling is the source of truth; webhooks accelerate it.
6. **Credentials from the credential chain.** Configuration holds only
   environment-variable *names*; values come from the host environment
   (vault agent, systemd credentials, container secrets). OAuth access tokens
   are refreshed in-process with single-flight caching.
7. **Capability honesty** — `SupportsWebhooks+SupportsPolling`,
   `CanPostComments+CanSetStatus`. Anything unmappable or unfindable is
   reported (`UnmappedState`, `Failed` with detail), never silently degraded.

## Epics: one work item, opt-in

Stories and epics are a hierarchy, so the epic policy is explicit rather than
emergent: with `IngestEpics=false` (default) only stories ingest and signalled
epics are ignored entirely. With `IngestEpics=true`, each signalled epic
ingests as **one** work item (`sc-epic-{id}`) — epics never fan out into
per-story items. Tracker state moves apply to stories only; epics receive
comments (progress, questions, outcomes) without a native state change, and the
post still reports `Posted` with the comment id.

## Shortcut-side setup

1. Create a service account (an API token for a dedicated Shortcut user) and
   note its member UUID/email — or just decide on a label such as `codeybox`.
2. Decide the signal: apply the label (recommended), assign stories to the
   service account, or move stories into a triage workflow state.
3. Create an API token (Settings → API → API Tokens) and export it where the
   host reads the chain, e.g. `SHORTCUT_API_TOKEN`.
4. For webhooks: create an outgoing webhook in the Shortcut UI subscribed to
   story, epic, and comment events, pointed at your CodeyBox endpoint.
   Shortcut signs nothing itself; if a signing proxy fronts CodeyBox, export
   its shared secret as `SHORTCUT_WEBHOOK_SECRET` and set
   `WebhookSignatureHeader` to the header the proxy uses.

## Configuration

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.shortcut-worksync"],
      "Enabled": ["codeybox.shortcut-worksync"],
      "AssemblyPaths": ["/etc/codeybox/plugins/CodeyBox.ShortcutWorkSyncPlugin.dll"]
    },
    "Plugins:codeybox.shortcut-worksync": {
      "Enabled": true,
      "SignalKind": "Label",
      "SignalValue": "codeybox",
      "ProjectMap": { "12": "my-app" },
      "StateMapping": { "Working": "In Progress", "Done": "Done", "Failed": "Cancelled" },
      "IngestEpics": false,
      "TokenEnvVar": "SHORTCUT_API_TOKEN",
      "ManageWebhooks": false,
      "WebhookUrl": "https://codeybox.example.com/webhooks/shortcut",
      "WebhookSecretEnvVar": "SHORTCUT_WEBHOOK_SECRET"
    }
  }
}
```

All values are hot-reloadable (re-read per poll/post). Full reference: every
property on `ShortcutWorkSyncOptions` is a config key (`ApiBaseUrl`,
`TimeoutSeconds`, `PageSize`, `MaxItemsPerPoll`, `MaxIngestedBodyChars`,
`MemberCacheMinutes`, `OAuthClientIdEnvVar` / `OAuthClientSecretEnvVar` /
`OAuthRefreshTokenEnvVar` / `OAuthTokenUrl`, `ServiceLogins`,
`WebhookSignatureHeader`).

`ProjectMap` keys are Shortcut project ids (numeric, as strings). Stories whose
project is unmapped are skipped, never guessed. Members resolve `owner_ids` to
email/mention-name for `Assignee` signals (cached for `MemberCacheMinutes`); on
webhook deliveries only the member UUID is available, so configure the UUID
(not the email) when you need webhook-speed assignee matching.

## Security contract

- Nothing in a story's name, description, or comments can cause ingestion or
  widen it — only the configured signal (upstream metadata) authorises it.
- Ingested content becomes the prompt with an untrusted-input header; agent,
  credentials, grants, capabilities, and priority are never sourced upstream.
- An upstream failure never fails a work item: exceptions become `Failed`
  results and `SyncFailed` audit records.
- Proxy-added webhook signatures (HMAC-SHA256 over raw bytes) are verified
  before parsing when a secret is configured; oversized bodies are rejected
  before buffering; all bounds are config, not literals.
- No secrets in logs or error text — only ids, status codes, and env-var names.

## Question replies

A surfaced question comment ends with `<!-- codeybox-question:q-001 -->`.
Reply in Shortcut with the question id as a prefix:

```
q-001: use forward-only migrations
```

The host matches the prefix against the item's open questions and answers
through the question store. Replies from the service account (or carrying the
CodeyBox marker) are ignored.

## Webhook payload shape

Shortcut deliveries vary by workspace configuration, so the parser accepts a
documented envelope and ignores the rest:

```json
{
  "id": "evt-1",
  "action": "update",
  "entity_type": "story",
  "actor": { "mention_name": "op", "email": "op@example.com" },
  "story": {
    "id": 48,
    "name": "Login redirect is broken",
    "description": "Fix the login redirect.",
    "project_id": 12,
    "labels": [{ "name": "codeybox" }],
    "owner_ids": ["11111111-2222-3333-4444-555555555555"],
    "workflow_state_id": 500,
    "workflow_state_name": "Unstarted"
  }
}
```

`entity_type` accepts `story`, `epic`, `story-comment`/`story_comment`,
`epic-comment`/`epic_comment` (`resource_type`/`type` are aliases; `entity` and
`data` alias the envelope). Comment deliveries carry the comment under
`comment` (`text` preferred, `body` accepted) plus the parent `story`/`epic`.
`delete` actions parse as no candidate — removal is detected by polling
comparison on the host, never by acting on a delete payload.

## Limitations

- Story keys are derived (`sc-{id}`): stable and non-UUID, but Shortcut-native
  tooling shows the bare number — the mapping is documented here and in audit
  records.
- Poll candidates carry no last-actor login (the search API exposes none);
  loop safety for comments rests on the marker + service-login check, and
  re-reads converge idempotently on the external id.
- Epic listing has no project scoping in the API: epic candidates map to the
  first configured project. Scope epic ingestion with the signal, not the map.
- Webhook `story` payloads that omit labels/owners parse as unsignalled and
  are skipped; the next poll (full data + member resolution) ingests them.
  Polling is the source of truth; webhooks accelerate it.
