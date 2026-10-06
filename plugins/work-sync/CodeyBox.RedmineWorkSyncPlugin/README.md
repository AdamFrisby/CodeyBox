# CodeyBox: Redmine Work Sync

One project, one plugin implementing `IWorkSource` (inbound) and `IWorkTracker`
(outbound) against the documented Redmine JSON REST API (Redmine 4.x–6.x:
`GET /issues.json`, `GET /issues/{id}.json?include=journals`,
`GET /issue_statuses.json`, `PUT /issues/{id}.json`). Off unless an operator
enables it: the assembly loads only when allowlisted **and** named in
`Plugins:Enabled`, and it polls/posts nothing until `Enabled=true` in its own
section. `ApiBaseUrl` has no default — enabling without it fails loudly rather
than aiming the API key at a placeholder host.

**Polling only, explicitly.** Redmine ships no native issue webhooks on any
version, so this plugin declares `SupportsWebhooks: false` and
`ParseVerifiedWebhookBody` throws `NotSupportedException` — polling is the
source of truth, not a fallback. Operator question replies arrive as journal
notes and are observed via `ListQuestionRepliesAsync`.

## What it does

1. **Ingestion** — polls recently-updated issues per mapped project identifier
   (`project_id={identifier}&status_id=*&sort=updated_on:desc`) for issues
   carrying the operator-configured signal (default: a status value; an
   assignee name or a custom-field value work too — Redmine has no native
   labels, so every scalar custom-field value is surfaced as a label-kind
   signal). Signalled issues become work items with the issue number
   (`"123"`, qualified by the `redmine` namespace) in
   `ExternalIds["redmine"]`. The same id converges polling overlap and manual
   re-sync onto one item — never a duplicate.
2. **Sync back** — PUTs progress notes on mapped state transitions together
   with the declared status (resolved to a status id per post), surfaces open
   questions as notes, reports commit SHAs / PR links, and reports terminal
   completion or failure the same way.
3. **State mapping** is an explicit operator declaration, configured once at
   the host level (`CodeyBox:WorkSync:StateMapping`) and handed to the plugin
   already resolved (`ExternalStatus`) — the plugin resolves exactly that
   name to a status id and never re-maps through a second, driftable
   declaration. An unmapped state returns `UnmappedState` and writes nothing —
   never guessed. A name with no Redmine status, or a transition the
   instance workflow disallows (HTTP 422), is a reported `Failed` outcome —
   workflows stay operator-managed in Redmine; this plugin performs no admin
   operations and changes no workflow.
4. **Loop safety** — every outbound body carries
   `<!-- codeybox-work-item:{id} -->` (applied by `WorkTrackerService`); our
   own notes are recognised by that marker in the journal and never re-posted
   (pre-write dedup returns `SkippedDuplicate`). Progress posts only on
   changed statuses.
5. **Uncertain writes reconcile, never blindly repeat** — `PUT` answers with
   an empty body, so a note write interrupted by timeout, cancellation, or
   transport failure re-reads the issue journal for the marker before any
   repetition: present means `Posted` (reconciled), absent means `Failed`.
   PUTs are never retried internally; idempotent GETs retry 429/502/503/504
   (bounded attempts, `Retry-After` honored).
6. **Credentials from the credential chain.** Configuration holds only the
   environment-variable *name*; the value comes from the host environment
   (vault agent, systemd credentials, container secrets). The key travels as
   `X-Redmine-API-Key` (header — never a `?key=` query string, which would
   leak it into access logs).
7. **Capability honesty** — `SupportsPolling` only, `CanPostComments` +
   `CanSetStatus`. Anything unmappable, rejected, or unreconciled is reported
   (`UnmappedState`, `Failed` with detail, `SkippedDuplicate`), never
   silently degraded.

## Redmine-side setup

1. Create a service account (a user the bot acts as) and generate an **API
   key** for it (My account → API access key).
2. Decide the signal: move issues to a triage status (e.g. `Ready for
   CodeyBox`, recommended), assign them to the service account, or set a
   custom field (e.g. list field value `codeybox`). For `SignalKind=Status`,
   `SignalValue` must be the exact status name.
3. Export the key where the host reads the chain, e.g. `REDMINE_API_KEY`.
4. Note the project **identifier** (the URL-safe key in project settings,
   e.g. `my-app`) for `ProjectMap` — display names are editable and never
   matched.

## Configuration

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.redmine-worksync"],
      "Enabled": ["codeybox.redmine-worksync"],
      "AssemblyPaths": ["/etc/codeybox/plugins/CodeyBox.RedmineWorkSyncPlugin.dll"]
    },
    "WorkSync": {
      "StateMapping": { "Working": "In Progress", "Done": "Resolved", "Failed": "Feedback" }
    },
    "Plugins:codeybox.redmine-worksync": {
      "Enabled": true,
      "ApiBaseUrl": "https://redmine.example.com",
      "SignalKind": "Status",
      "SignalValue": "Ready for CodeyBox",
      "ProjectMap": { "my-app": "my-app" },
      "ApiKeyEnvVar": "REDMINE_API_KEY"
    }
  }
}
```

All values are hot-reloadable (re-read per poll/post). Full reference: every
property on `RedmineWorkSyncOptions` is a config key (`TimeoutSeconds`,
`PageSize`, `MaxItemsPerPoll`, `MaxIngestedBodyChars`, `MaxResponseBytes`,
`MaxPagesPerPoll`, `MaxAttempts`, `RetryBaseDelayMs`, `AllowUnsafeHttp`).
Loop-guard attribution (recognising CodeyBox-authored updates by the acting
account) is configured once at the host level via
`CodeyBox:WorkSync:CodeyBoxServiceLogins` — not per plugin. Redmine list
payloads carry only display names (no immutable login), so that match is
best-effort here; loop safety rests primarily on the stable external id,
status-unchanged skips, and journal-marker dedup.

## Security contract

- Nothing in an issue's subject, description, or journals can cause ingestion
  or widen it — only the configured signal (upstream metadata) authorises it.
- Ingested content becomes the prompt with an untrusted-input header; agent,
  credentials, grants, capabilities, and priority are never sourced upstream.
- An upstream failure never fails a work item: exceptions become `Failed`
  results and `SyncFailed` audit records.
- All bounds are config, not literals; response bodies are bounded before
  buffering; oversized bodies fail the request.
- `PUT` bodies are built from typed fields (`notes`, `status_id`) — no string
  concatenation into URLs or payloads; the external id is validated as a
  positive issue number at the sink before it reaches a request URL.
- `ApiBaseUrl` must be `https://` — the API key would otherwise travel in
  cleartext. Plaintext `http://` is refused unless `AllowUnsafeHttp=true` is
  set explicitly (dev-only opt-in, e.g. a local test instance).
- Redirects are refused rather than followed with credentials — configure the
  canonical `ApiBaseUrl` (no redirect).
- No secrets in logs or error text — only ids, status codes, and env-var names.

## Question replies

A surfaced question note ends with `<!-- codeybox-question:q-001 -->`.
Reply in Redmine with the question id as a prefix:

```
q-001: use forward-only migrations
```

The host matches the prefix against the item's open questions (via
`ListQuestionRepliesAsync`) and answers through the question store. Notes
from the service account (or carrying the CodeyBox marker) are ignored.

## Limitations

- No webhooks exist to configure: Redmine offers no issue-webhook facility
  on any version. Polling observes everything, including question replies —
  with poll latency.
- Poll candidates' loop-guard attribution uses the issue author display
  name; journal authors likewise carry names only. Our own writes are caught
  by the journal marker, never re-posted.
- `PUT` returns no note id, so posted results carry no `RemoteId` — the
  audit trail records the journal reconciliation instead.
- Supported versions: Redmine 4.0+ JSON REST API. Redmine exposes no REST
  version endpoint, so the startup probe counts issue statuses instead of
  asserting a version; per-request capability discovery applies throughout.
