# CodeyBox: OpenProject Work Sync

One project, one plugin implementing `IWorkSource` (inbound) and `IWorkTracker`
(outbound) against the OpenProject REST API v3 (HAL+JSON). Off unless an
operator enables it: the assembly loads only when allowlisted **and** named in
`Plugins:Enabled`, and it polls/posts nothing until `Enabled=true` in its own
section. `ApiBaseUrl` has no default — enabling without it fails loudly rather
than aiming the API token at a placeholder host.

OpenProject's distinctive surface is **optimistic locking**: every status write
carries the `lockVersion` from the immediately-preceding read, and the status
applied is resolved from the host's own `GET /api/v3/statuses` at the moment
of the write — never constructed or guessed. Supported API: v3 on OpenProject
**13.0+** (work-package collections, `lockVersion` PATCH, activity comments).

## What it does

1. **Ingestion** — polls each mapped project's work packages newest-first
   (`GET /api/v3/projects/{key}/work_packages?sortBy=[["updatedAt","desc"]]`)
   and ingests packages carrying the operator-configured signal (default:
   assignment to the service account's **numeric user id**; a status name
   works too). Signalled packages become work items with the immutable numeric
   id in `ExternalIds["openproject"]`. The same id converges polling overlap
   and manual re-sync onto one item — never a duplicate. Unmapped projects are
   skipped, never guessed; there are no wholesale imports.
2. **Sync back** — posts progress comments on mapped state transitions and
   applies the declared status via versioned PATCH, surfaces open questions
   as comments (tagged `<!-- codeybox-question:q-001 -->`), reports commit
   SHAs / PR links, and reports terminal completion or failure the same way.
3. **State mapping** is an explicit operator declaration, configured once at
   the host level (`CodeyBox:WorkSync:StateMapping`) and handed to the plugin
   already resolved (`ExternalStatus`) — the plugin resolves exactly that
   name against the host's status list and applies its href. An unmapped
   state returns `UnmappedState` and writes nothing — never guessed. A status
   the host does not know is a reported `Failed` outcome, not a silent skip.
4. **Loop safety** — every outbound body carries
   `<!-- codeybox-work-item:{id} -->` (applied by `WorkTrackerService`); the
   plugin owns only the status link and activity comments, so its writes can
   never alter the subject/description text ingestion reads. Progress posts
   only on changed statuses, and comment posts are deduplicated by exact-body
   activity match.
5. **Polling only.** The plugin registers no webhooks and opens no live
   connection: capabilities honestly declare `SupportsPolling` without
   `SupportsWebhooks`, and `ParseVerifiedWebhookBody` throws
   `NotSupportedException`.
6. **Credentials from the credential chain.** Configuration holds only the
   environment-variable *name*; the value comes from the host environment
   (vault agent, systemd credentials, container secrets). The API token
   travels as `Authorization: Bearer` per the official API introduction.
7. **Capability honesty** — `CanPostComments+CanSetStatus`. Anything
   unmappable, unknown to the host, conflicted, or rejected is reported
   (`UnmappedState`, `SkippedDuplicate`, `Failed` with detail), never
   silently degraded.

## OpenProject-side setup

1. Create a service account (a user the bot acts as) and generate an **API
   token** for it (My account → Access tokens). Note its **numeric user id**
   (the id in `/users/{id}` or `GET /api/v3/users/{id}`) — display names are
   user-editable and are never matched, so renaming a user cannot forge the
   signal.
2. Decide the signal: assign work packages to the service account
   (recommended — `SignalKind=Assignee`, `SignalValue=<numeric user id>`),
   or move packages to a triage status (`SignalKind=Status`,
   `SignalValue=In progress`). There is no label/tag field on work packages,
   so `SignalKind=Label` never matches (the poll warns about it).
3. Export the token where the host reads the chain, e.g.
   `OPENPROJECT_TOKEN`.
4. Grant the account view + update + comment + status-change rights on the
   tracked projects. No webhook configuration exists for this integration.

## Configuration

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.openproject-worksync"],
      "Enabled": ["codeybox.openproject-worksync"],
      "AssemblyPaths": ["/etc/codeybox/plugins/CodeyBox.OpenProjectWorkSyncPlugin.dll"]
    },
    "WorkSync": {
      "StateMapping": { "Working": "In progress", "Done": "Closed", "Failed": "Rejected" }
    },
    "Plugins:codeybox.openproject-worksync": {
      "Enabled": true,
      "ApiBaseUrl": "https://openproject.example.com",
      "SignalKind": "Assignee",
      "SignalValue": "42",
      "ProjectMap": { "my-project": "my-app" },
      "TokenEnvVar": "OPENPROJECT_TOKEN"
    }
  }
}
```

`ProjectMap` keys are OpenProject project ids or identifier slugs (the
`my-project` in `/projects/my-project`); values are CodeyBox project ids.
All values are hot-reloadable (re-read per poll/post). Full reference: every
property on `OpenProjectWorkSyncOptions` is a config key (`TimeoutSeconds`,
`PageSize`, `MaxItemsPerPoll`, `MaxIngestedBodyChars`, `MaxResponseBytes`,
`MaxPagesPerPoll`, `MaxActivitiesScanned`, `MaxRateLimitRetries`,
`MaxRateLimitDelaySeconds`, `AllowUnsafeHttp`).

## Security contract

- Nothing in a package's subject, description, or activities can cause
  ingestion or widen it — only the configured signal (upstream metadata)
  authorises it.
- Ingested content becomes the prompt with an untrusted-input header; agent,
  credentials, grants, capabilities, and priority are never sourced upstream.
- An upstream failure never fails a work item: exceptions become `Failed`
  results and `SyncFailed` audit records.
- Response bodies are size-bounded before buffering; all bounds are config,
  not literals. Rate-limit retries happen only on HTTP 429 (the server
  guarantees non-execution); ambiguous failures are reconciled by re-reading
  before a single bounded repeat — never retried blindly.
- Path segments are escaped; the status href applied always comes from the
  host's own status list. `ApiBaseUrl` must be `https://` — plaintext
  `http://` is refused unless `AllowUnsafeHttp=true` is set explicitly
  (dev-only opt-in).
- No secrets in logs or error text — only ids, status codes, and env-var names.

## Question replies

A surfaced question comment ends with `<!-- codeybox-question:q-001 -->`.
The documented reply convention (shared `WorkSyncQuestions` protocol) is:

```
q-001: use forward-only migrations
```

This integration is polling-only, so operator replies are currently answered
in CodeyBox itself; the tag is embedded so a future delivery transport can
attribute replies without changing the wire format.

## Limitations

- Polling is the only delivery path: there is no webhook registration and no
  live connection. Replies to surfaced questions are answered in CodeyBox,
  not in OpenProject.
- The work-package resource exposes no "last updater", so poll candidates
  carry no actor attribution (`LastActorLogin` is null). Loop safety rests on
  ingestion idempotency plus field ownership: this plugin never writes
  subject/description, so its own writes cannot re-trigger ingestion content.
- Concurrent status changes surface as HTTP 409: the plugin re-reads, adopts
  the converged value when it matches, and otherwise retries once with the
  fresh `lockVersion`. A second conflict is reported as `Failed` rather than
  stomping the newer change.
- One plugin instance serves one OpenProject host: numeric work-package ids
  are stable only within that instance. Multi-instance setups need one
  CodeyBox deployment per host.
