# CodeyBox: Plane Work Sync

One project, one plugin implementing `IWorkSource` (inbound) and `IWorkTracker`
(outbound) against the Plane REST API. Off unless an operator enables it:
the assembly loads only when allowlisted **and** named in `Plugins:Enabled`,
and it polls/posts nothing until `Enabled=true` in its own section.

## What it does

1. **Ingestion** — polls Plane issues per mapped project and parses Plane v2
   webhooks for work items carrying the operator-configured signal (default: a
   label such as `codeybox`; an assignee or a state value work too). Signalled
   items become work items with the human key (`WEB-123`) in
   `ExternalIds["plane"]`. The same key converges webhook redelivery, polling
   overlap, and manual re-sync onto one item — never a duplicate.
2. **Sync back** — posts progress on mapped state transitions, surfaces open
   questions as comments, reports commit SHAs / PR links, and reports terminal
   completion or failure by moving the issue to the mapped Plane state.
3. **State mapping** is an explicit operator declaration (`StateMapping`).
   An unmapped state returns `UnmappedState` and writes nothing — never guessed.
4. **Loop safety** — every outbound body carries
   `<!-- codeybox-work-item:{id} -->` (applied by `WorkTrackerService`); our
   own comments are recognised by that marker or by the service login and
   ignored on the way back in. Progress posts only on changed statuses.
5. **Webhooks and polling both.** `ManageWebhooks=true` attempts to register
   the delivery URL with Plane at startup (recreating it when absent — never
   assuming it persists). Plane webhooks are workspace-level and normally
   created in the UI; the REST lifecycle is best-effort and an instance that
   lacks the webhooks API degrades to polling with a warning. Deployments
   without inbound traffic leave it off and poll.
6. **Credentials from the credential chain.** Configuration holds only
   environment-variable *names*; values come from the host environment
   (vault agent, systemd credentials, container secrets). Static personal
   access tokens travel as `X-API-Key`; OAuth access tokens are refreshed
   in-process with single-flight caching and travel as `Authorization: Bearer`.
7. **Capability honesty** — `SupportsWebhooks+SupportsPolling`,
   `CanPostComments+CanSetStatus`. Anything unmappable, unfindable, or absent
   on the instance is reported (`UnmappedState`, `Failed` with detail), never
   silently degraded.

## Plane-side setup

1. Create a service account (or OAuth app) and note its user id/email.
   If you install CodeyBox as a Plane agent (an OAuth app with mentions
   enabled), Plane creates a bot user: its user id doubles as the `Assignee`
   signal value and as a `ServiceLogins` entry.
2. Decide the signal: apply a label (e.g. `codeybox`, recommended), assign
   issues to the service account, or move issues to a triage state.
   An @mention of the bot alone never ingests — mentions arrive as comment
   content, and content can never trigger ingestion.
3. Create a personal access token (**Profile Settings → Personal Access
   Tokens**) and export it where the host reads the chain, e.g.
   `PLANE_API_KEY`.
4. For webhooks: create a workspace webhook (**Workspace settings →
   Webhooks**) subscribed to `workitem.created`, `workitem.updated`, and
   `workitem.comment.created`, pointing at your CodeyBox endpoint. Save the
   downloaded secret CSV and export it as `PLANE_WEBHOOK_SECRET`.

## Configuration

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.plane-worksync"],
      "Enabled": ["codeybox.plane-worksync"],
      "AssemblyPaths": ["/etc/codeybox/plugins/CodeyBox.PlaneWorkSyncPlugin.dll"]
    },
    "Plugins:codeybox.plane-worksync": {
      "Enabled": true,
      "ApiBaseUrl": "https://api.plane.so",
      "WorkspaceSlug": "acme",
      "SignalKind": "Label",
      "SignalValue": "codeybox",
      "ProjectMap": { "<plane-project-uuid>": "my-app" },
      "StateMapping": { "Working": "In Progress", "Done": "Completed", "Failed": "Cancelled" },
      "TokenEnvVar": "PLANE_API_KEY",
      "ManageWebhooks": false,
      "WebhookUrl": "https://codeybox.example.com/webhooks/plane",
      "WebhookSecretEnvVar": "PLANE_WEBHOOK_SECRET"
    }
  }
}
```

All values are hot-reloadable (re-read per poll/post). Full reference: every
property on `PlaneWorkSyncOptions` is a config key (`PageSize`,
`MaxItemsPerPoll`, `MaxIngestedBodyChars`, `TimeoutSeconds`, `IssuesPath`,
`MaxResolvePages`, `WebhookTitle`, `OAuthClientIdEnvVar` /
`OAuthClientSecretEnvVar` / `OAuthRefreshTokenEnvVar` / `OAuthTokenUrl`,
`ServiceLogins`).

## Security contract

- Nothing in an issue's title, description, or comments can cause ingestion or
  widen it — only the configured signal (upstream metadata) authorises it.
- Ingested content becomes the prompt with an untrusted-input header; agent,
  credentials, grants, capabilities, and priority are never sourced upstream.
- An upstream failure never fails a work item: exceptions become `Failed`
  results and `SyncFailed` audit records.
- Webhook signatures (HMAC-SHA256 over raw bytes, `X-Plane-Signature`) are
  verified before parsing; oversized bodies are rejected before buffering; all
  bounds are config, not literals.
- No secrets in logs or error text — only ids, status codes, and env-var names.

## Question replies

A surfaced question comment ends with `<!-- codeybox-question:q-001 -->`.
Reply in Plane with the question id as a prefix:

```
q-001: use forward-only migrations
```

The host matches the prefix against the item's open questions and answers
through the question store. Replies from the service account (or carrying the
CodeyBox marker) are ignored.

## Limitations

- The ingestion key is the human key (`WEB-123`, built from the project
  identifier and sequence id), because external ids must not be UUIDs.
  Renaming a project's identifier while issues are in flight changes their
  keys; avoid renaming in-flight projects or a second item will be ingested.
- Poll candidates carry the last actor only when the REST payload exposes it;
  loop safety for comments rests on the marker + service-login check, and
  re-reads converge idempotently on the external id.
- Webhook issue payloads that omit the project identifier parse as unmapped
  and are skipped; the next poll (full data) ingests them. Polling is the
  source of truth; webhooks accelerate it.
- Plane renamed `issues` to `work items` in newer releases. The client tries
  the configured `IssuesPath` first and falls back to the other spelling on
  404, and degrades honestly (a `Failed` result with detail, never a throw)
  when an instance lacks the states, comments, or webhooks endpoints.
- Plane's Agent Run API (thought/action/response activities) is intentionally
  not used for sync: CodeyBox posts plain comments so the integration works
  on every Plane version, including instances without agent support.
