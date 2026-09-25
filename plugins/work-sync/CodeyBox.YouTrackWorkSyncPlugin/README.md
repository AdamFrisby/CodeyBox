# CodeyBox: YouTrack Work Sync

One project, one plugin implementing `IWorkSource` (inbound) and `IWorkTracker`
(outbound) against the YouTrack REST API. The same `/api` surface serves
YouTrack Cloud and self-hosted Server, so capabilities are declared statically;
at startup the plugin probes `GET /api/config` and logs the reported
version/build (a missing or forbidden endpoint degrades to "unknown", never a
startup failure). Off unless an operator enables it: the assembly loads only
when allowlisted **and** named in `Plugins:Enabled`, and it polls/posts nothing
until `Enabled=true` in its own section. `ApiBaseUrl` has no default — enabling
without it fails loudly rather than aiming credentials at a placeholder host.

YouTrack's distinctive surface is its **command syntax**: state changes are
commands (`{State} {In Progress}`) applied through `POST /api/commands`, not
field writes — and project/field names are per-instance configurable, so the
state field name, assignee field name, and every state value are explicit
operator declarations.

## What it does

1. **Ingestion** — polls recently-updated YouTrack issues per mapped project
   (`project: {KEY} sort by: updated desc`) and parses Webhook Triggers
   deliveries for issues carrying the operator-configured signal (default:
   assignment to a service account; a tag or a state value work too).
   Signalled issues become work items with the readable id (`PROJ-123`) in
   `ExternalIds["youtrack"]`. The same id converges webhook redelivery,
   polling overlap, and manual re-sync onto one item — never a duplicate.
2. **Sync back** — posts progress comments on mapped state transitions and
   applies the declared state as a command, surfaces open questions as
   comments, reports commit SHAs / PR links, and reports terminal completion
   or failure the same way.
3. **State mapping** is an explicit operator declaration (`StateMapping`).
   An unmapped state returns `UnmappedState` and writes nothing — never
   guessed. A mapped value is expressed as `{field} {value}` with both
   operands brace-quoted; a value the command language cannot express (or
   that the instance's state-machine rules reject) is a reported `Failed`
   outcome, not a silent skip.
4. **Loop safety** — every outbound body carries
   `<!-- codeybox-work-item:{id} -->` (applied by `WorkTrackerService`); our
   own comments and command-driven updates are recognised by that marker or
   by the service login (`updater`/`author` on inbound reads) and ignored on
   the way back in. Progress posts only on changed statuses.
5. **Webhooks and polling both.** Deployments without inbound traffic leave
   the app unconfigured and poll. Webhook deliveries arrive from YouTrack's
   **Webhook Triggers app**, authenticated by a shared token in a
   configurable header (default `X-YouTrack-Token`). YouTrack exposes **no
   REST API for registering webhooks on any version** — registration is
   configured in the app UI and stays operator-managed; there is nothing for
   this plugin to recreate or renew. What the plugin *does* own is the
   verification side: it validates the shared token on every delivery.
6. **Credentials from the credential chain.** Configuration holds only
   environment-variable *names*; values come from the host environment
   (vault agent, systemd credentials, container secrets). Permanent tokens
   (`perm:…`) travel as `Authorization: Bearer`; Hub OAuth2
   client-credentials access tokens are refreshed in-process with
   single-flight caching.
7. **Capability honesty** — `SupportsWebhooks+SupportsPolling`,
   `CanPostComments+CanSetStatus`. Anything unmappable, unquotable, or
   rejected is reported (`UnmappedState`, `Failed` with detail), never
   silently degraded.

## YouTrack-side setup

1. Create a service account (a user the bot acts as) and generate a
   **permanent token** for it (Profile → Account Security → Tokens).
   Alternatively create a Hub *service* with a client id/secret for the
   OAuth2 client-credentials grant.
2. Decide the signal: assign issues to the service account (recommended),
   or apply a tag (e.g. `codeybox`), or move issues to a triage state.
3. Export the token where the host reads the chain, e.g. `YOUTRACK_TOKEN`.
   For OAuth2 export the client id and secret instead, and set
   `OAuthScope` to the Hub service id of the YouTrack service if the
   instance requires it.
4. For webhooks: install/enable the **Webhook Triggers** app
   (Administration → Apps), attach it to the tracked projects, generate a
   webhook token (32+ chars; `openssl rand -hex 32`), enter it in the app
   settings with your header name, and point the *Issue* and *Comment*
   event URLs at your CodeyBox endpoint. Export the same token as
   `YOUTRACK_WEBHOOK_TOKEN` for verification.

## Configuration

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.youtrack-worksync"],
      "Enabled": ["codeybox.youtrack-worksync"],
      "AssemblyPaths": ["/etc/codeybox/plugins/CodeyBox.YouTrackWorkSyncPlugin.dll"]
    },
    "Plugins:codeybox.youtrack-worksync": {
      "Enabled": true,
      "ApiBaseUrl": "https://acme.youtrack.cloud",
      "SignalKind": "Assignee",
      "SignalValue": "codeybox-bot",
      "ProjectMap": { "PROJ": "my-app" },
      "StateMapping": { "Working": "In Progress", "Done": "Fixed", "Failed": "Won't fix" },
      "StateFieldName": "State",
      "AssigneeFieldName": "Assignee",
      "TokenEnvVar": "YOUTRACK_TOKEN",
      "WebhookTokenHeader": "X-YouTrack-Token",
      "WebhookSecretEnvVar": "YOUTRACK_WEBHOOK_TOKEN"
    }
  }
}
```

All values are hot-reloadable (re-read per poll/post). Full reference: every
property on `YouTrackWorkSyncOptions` is a config key (`TimeoutSeconds`,
`PageSize`, `MaxItemsPerPoll`, `MaxIngestedBodyChars`, `MaxResponseBytes`,
`OAuthTokenUrl`, `OAuthClientIdEnvVar` / `OAuthClientSecretEnvVar`,
`OAuthScope`). Login-based loop-guard attribution (recognising CodeyBox-authored
updates by the acting account) is configured once at the host level via
`CodeyBox:WorkSync:CodeyBoxServiceLogins` — not per plugin.

OAuth2 (instead of a permanent token): create a Hub service (Administration →
Hub → Services) to get a client id/secret, set the two `OAuth*EnvVar` names
and optionally `OAuthScope` (the YouTrack service's Hub id — often
`0-0-0-0-0`). Access tokens are fetched from
`{ApiBaseUrl}/hub/api/rest/oauth2/token` (override with `OAuthTokenUrl`) via
`grant_type=client_credentials` and cached until shortly before expiry.

## Security contract

- Nothing in an issue's summary, description, or comments can cause ingestion
  or widen it — only the configured signal (upstream metadata) authorises it.
- Ingested content becomes the prompt with an untrusted-input header; agent,
  credentials, grants, capabilities, and priority are never sourced upstream.
- An upstream failure never fails a work item: exceptions become `Failed`
  results and `SyncFailed` audit records.
- Webhook deliveries are authenticated by the shared header token before
  parsing (constant-time comparison); oversized bodies are rejected before
  buffering; all bounds are config, not literals.
- Command operands are brace-quoted and validated at the sink: a field name
  or status value containing characters the command language cannot express
  is refused, never interpolated raw (an unguarded `}` would break out of
  the quoting into command syntax).
- No secrets in logs or error text — only ids, status codes, and env-var names.

## Question replies

A surfaced question comment ends with `<!-- codeybox-question:q-001 -->`.
Reply in YouTrack with the question id as a prefix:

```
q-001: use forward-only migrations
```

The host matches the prefix against the item's open questions and answers
through the question store. Replies from the service account (or carrying the
CodeyBox marker) are ignored.

## Limitations

- Webhook issue payloads from the Webhook Triggers app carry the changed
  fields, not the full tag/custom-field set: ingestion via webhook fires when
  the delivery shows the signal being applied (e.g. the assignee change is in
  `changedFields`). A signal that was already present before the watched
  change is picked up by the next poll. Polling is the source of truth;
  webhooks accelerate it.
- `issueCreated` deliveries carry no assignee/tag values at all — signalled
  new issues are ingested by the poll.
- Poll candidates' loop-guard attribution uses the issue `updater`; comment
  deliveries use the comment `author`. Our own writes are caught by the
  marker or the service-login match, never re-ingested.
- Webhook registration cannot be managed from REST (YouTrack offers no such
  endpoint); the app keeps it. If the app is removed or the token rotates,
  update both the app and the env var — verification fails closed.
