# CodeyBox: Asana Work Sync

One project, one plugin implementing `IWorkSource` (inbound) and `IWorkTracker`
(outbound) against the Asana REST API `v1.0` (`https://app.asana.com/api/1.0`,
verified 2026-10-06 against
[`GET /tasks`](https://developers.asana.com/reference/gettasks): bearer auth
for personal access tokens and OAuth, compact tasks by default with
`opt_fields` expansion, `limit` 1–100 pages with opaque `next_page.offset`
tokens, `{"data":[…],"next_page":…}` envelopes, and
`{"errors":[{"message":…}]}` failures). Off unless an operator enables it:
the assembly loads only when allowlisted **and** named in `Plugins:Enabled`,
and it polls/posts nothing until `Enabled=true` in its own section.
`ApiBaseUrl` defaults to the public cloud endpoint — a custom origin is always
an explicit operator choice — and must be `https://` unless the dev-only
`AllowUnsafeHttp` opt-in is set.

Asana's distinctive surface is **GIDs plus stories**: every resource carries
an immutable numeric GID (names are editable and never identity), list reads
select only the fields ingestion needs via `opt_fields`, and every outbound
write lands as a story (comment) — completion and custom-field writes happen
only for caller-resolved statuses the operator explicitly mapped.

## What it does

1. **Ingestion** — polls recently-modified tasks per mapped project
   (`GET /tasks?project={gid}&opt_fields=…&limit=…&modified_since=…`,
   following `next_page.offset`) for tasks carrying the operator-configured
   signal (default: assignment to a service account; a tag or a section name
   works too). Signalled tasks become work items with the task GID in
   `ExternalIds["asana"]`. Overlapping polls and re-polls converge on one
   item — never a duplicate. A multi-homed task (in several mapped projects)
   ingests under the first mapped project in the task's own project order,
   deterministically; outbound writes use the global GID so they stay
   unambiguous.
2. **Sync back** — posts progress stories on mapped state transitions,
   applies the declared completion (`completed`/`incomplete`) or the declared
   custom-field enum write, surfaces open questions as stories, reports
   commit SHAs / PR links, and reports terminal completion or failure the
   same way.
3. **State mapping** is an explicit operator declaration, configured once at
   the host level (`CodeyBox:WorkSync:StateMapping`) and handed to the plugin
   already resolved (`ExternalStatus`) — the plugin applies exactly that
   value and never re-maps through a second, driftable declaration. An
   unmapped state returns `UnmappedState` and writes nothing — never guessed.
   Recognised meanings are `completed` / `incomplete` plus keys of the
   plugin's explicit `StatusCustomFieldMap` (`fieldGid:enumGid`); anything
   else is a reported `Failed` outcome, not a silent skip or a guessed write.
4. **Loop safety and dedup** — every outbound body carries
   `<!-- codeybox-work-item:{id} -->` (applied by `WorkTrackerService`).
   Polls read tasks, writes land as stories, and there are no webhooks, so
   our writes cannot re-enter through polling; additionally every post scans
   the task's recent stories first and reports `SkippedDuplicate` when the
   identical marked story (or the same question tag) is already present.
   Progress posts only on changed statuses.
5. **Polling only.** This plugin never creates webhooks, never changes
   membership, and never imports a workspace: only tasks in explicitly mapped
   projects (by GID) carrying the configured signal are candidates.
   `SupportsWebhooks=false`; `ParseVerifiedWebhookBody` throws.
6. **Credentials from the credential chain.** Configuration holds only the
   environment-variable *name*; the value comes from the host environment
   (vault agent, systemd credentials, container secrets). Personal access
   tokens and OAuth access tokens both travel as `Authorization: Bearer`.
   Asana offers no client-credentials grant, so there is no in-process
   refresh — rotation happens in the host environment and is picked up
   without a restart.
7. **Capability honesty** — `SupportsPolling` (no webhooks),
   `CanPostComments+CanSetStatus`. Anything unmappable or rejected is
   reported (`UnmappedState`, `Failed` with detail), never silently degraded.

## Asana-side setup

1. Create a service account (a user the bot acts as) and generate a
   **personal access token** for it (My Profile → Apps → Manage developer
   apps → Personal access tokens). Note its user GID (`GET /users/me`).
2. Decide the signal: assign tasks to the service account (recommended —
   `SignalValue` is the user GID, the immutable identifier), or apply a tag
   (e.g. `codeybox` — GID preferred, exact name also matches), or move tasks
   to a triage section (e.g. `Ready for CodeyBox`) or complete them.
3. Find each tracked project's GID (the project's URL ends in the GID, or
   `GET /projects?workspace=…`), and map it to a CodeyBox project id.
4. Export the token where the host reads the chain, e.g. `ASANA_TOKEN`.
5. Decide completion semantics in the host state mapping
   (`Working → incomplete`, `Done → completed`, …) and, only if a workflow
   needs it, map a status to a custom-field enum write via
   `StatusCustomFieldMap` (`fieldGid:enumGid`, both numeric GIDs from
   `GET /custom_fields`).

## Configuration

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.asana-worksync"],
      "Enabled": ["codeybox.asana-worksync"],
      "AssemblyPaths": ["/etc/codeybox/plugins/CodeyBox.AsanaWorkSyncPlugin.dll"]
    },
    "WorkSync": {
      "StateMapping": { "Working": "incomplete", "Done": "completed" }
    },
    "Plugins:codeybox.asana-worksync": {
      "Enabled": true,
      "SignalKind": "Assignee",
      "SignalValue": "1234567890",
      "ProjectMap": { "555666777": "my-app" },
      "StatusCustomFieldMap": { "In Review": "2001:2002" },
      "TokenEnvVar": "ASANA_TOKEN"
    }
  }
}
```

All values are hot-reloadable (re-read per poll/post), including paging
(`PageSize`, `MaxPagesPerPoll`, `MaxItemsPerPoll`), recency
(`ModifiedSinceHours`), retries (`RetryMaxAttempts`, `RetryBaseDelayMs`,
`RetryMaxDelaySeconds`), dedup depth (`DedupScanLimit`), and all byte/char
bounds (`MaxResponseBytes`, `MaxIngestedBodyChars`, `TimeoutSeconds`).
Login-based loop-guard attribution is not applicable — poll candidates carry
no actor field, and our writes are stories while polls read tasks.

## Security contract

- Nothing in a task's name, notes, or stories can cause ingestion or widen
  it — only the configured signal (upstream metadata: assignee, tags,
  sections, completion) authorises it.
- Ingested content becomes the prompt with an untrusted-input header; agent,
  credentials, grants, capabilities, and priority are never sourced upstream.
- An upstream failure never fails a work item: exceptions become `Failed`
  results and `SyncFailed` audit records.
- GIDs are validated numeric at every sink before a URL is built; a
  non-numeric project key or external id is refused, never interpolated.
- `ApiBaseUrl` must be `https://` — the bearer token would otherwise travel
  in cleartext. Plaintext `http://` is refused unless `AllowUnsafeHttp=true`
  is set explicitly (dev-only opt-in, e.g. a local mock).
- Retries on 429/5xx are bounded (`RetryMaxAttempts`) and honour
  `Retry-After` within its cap; response bodies are bounded before buffering.
- No secrets in logs or error text — only GIDs, status codes, and env-var names.
- The plugin performs no process execution, no filesystem access, and no
  webhook/membership administration.

## Question replies

A surfaced question story ends with `<!-- codeybox-question:q-001 -->`.
There is no webhook path, so answers arrive the same way as any other
operator input: reply in Asana with the question id as a prefix…

```
q-001: use forward-only migrations
```

…and report the reply through the host question store. The shared
`WorkSyncQuestions` reply shape applies; stories carrying the CodeyBox
marker are ours and never parse as replies.

## Limitations

- Polling is the only intake path: there is no webhook acceleration, so newly
  signalled tasks appear on the next poll (bounded by `ModifiedSinceHours`
  and the paging caps).
- `Status` signals observe section names and `completed`; Asana has no other
  native status — workflow state beyond that lives in custom fields, which
  are write-only here and read never.
- Duplicate detection scans the most recent `DedupScanLimit` stories; a task
  with heavier story traffic may re-post after the window scrolls — host-side
  status/question records remain the primary guard.
- A status change that lands followed by a story failure is reported
  `Failed` with the partial application named in the detail — reconcile by
  re-reporting; both writes are convergent.
- OAuth access tokens are accepted but never refreshed in-process (Asana has
  no client-credentials grant); prefer a PAT for service integrations and
  rotate via the host environment.
