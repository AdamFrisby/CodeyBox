# CodeyBox: Linear Work Sync

One project, one plugin implementing `IWorkSource` (inbound) and `IWorkTracker`
(outbound) against the Linear GraphQL API. Off unless an operator enables it:
the assembly loads only when allowlisted **and** named in `Plugins:Enabled`,
and it polls/posts nothing until `Enabled=true` in its own section.

## What it does

1. **Ingestion** — polls recently-updated Linear issues and parses Linear
   webhooks for issues carrying the operator-configured signal (default:
   assignment to a service account; a label or a workflow-state value work
   too). Signalled issues become work items with the Linear key (`ENG-123`)
   in `ExternalIds["linear"]`. The same key converges webhook redelivery,
   polling overlap, and manual re-sync onto one item — never a duplicate.
2. **Sync back** — posts progress on mapped state transitions, surfaces open
   questions as comments, reports commit SHAs / PR links, and reports terminal
   completion or failure by moving the issue to the mapped workflow state.
3. **State mapping** is an explicit operator declaration (`StateMapping`).
   An unmapped state returns `UnmappedState` and writes nothing — never guessed.
4. **Loop safety** — every outbound body carries
   `<!-- codeybox-work-item:{id} -->` (applied by `WorkTrackerService`); our
   own comments are recognised by that marker or by the service login and
   ignored on the way back in. Progress posts only on changed statuses.
5. **Webhooks and polling both.** `ManageWebhooks=true` registers the delivery
   URL with Linear at startup (recreating it when absent or disabled — never
   assuming it persists). Deployments without inbound traffic leave it off and
   poll.
6. **Credentials from the credential chain.** Configuration holds only
   environment-variable *names*; values come from the host environment
   (vault agent, systemd credentials, container secrets). OAuth access tokens
   are refreshed in-process with single-flight caching.
7. **Capability honesty** — `SupportsWebhooks+SupportsPolling`,
   `CanPostComments+CanSetStatus`. Anything unmappable or unfindable is
   reported (`UnmappedState`, `Failed` with detail), never silently degraded.

## Linear-side setup

1. Create a service account (or OAuth app) and note its user id/email.
2. Decide the signal: assign issues to the service account (recommended),
   or apply a label (e.g. `codeybox`), or move issues to a triage state.
3. Create an API key (or OAuth app with `read` + `write` scopes) and export it
   where the host reads the chain, e.g. `LINEAR_API_KEY`.
4. For webhooks: create a webhook secret in Linear, set the delivery URL to
   your CodeyBox endpoint, and export the same secret as `LINEAR_WEBHOOK_SECRET`.

## Configuration

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.linear-worksync"],
      "Enabled": ["codeybox.linear-worksync"],
      "AssemblyPaths": ["/etc/codeybox/plugins/CodeyBox.LinearWorkSyncPlugin.dll"]
    },
    "Plugins:codeybox.linear-worksync": {
      "Enabled": true,
      "SignalKind": "Assignee",
      "SignalValue": "codeybox-bot-id-or-email",
      "TeamProjectMap": { "ENG": "my-app" },
      "StateMapping": { "Working": "In Progress", "Done": "Done", "Failed": "Cancelled" },
      "TokenEnvVar": "LINEAR_API_KEY",
      "ManageWebhooks": false,
      "WebhookUrl": "https://codeybox.example.com/webhooks/linear",
      "WebhookSecretEnvVar": "LINEAR_WEBHOOK_SECRET"
    }
  }
}
```

All values are hot-reloadable (re-read per poll/post). Full reference: every
property on `LinearWorkSyncOptions` is a config key (`ApiUrl`,
`TimeoutSeconds`, `PollPageSize`, `MaxItemsPerPoll`, `MaxIngestedBodyChars`,
`OAuthClientIdEnvVar` / `OAuthClientSecretEnvVar` / `OAuthRefreshTokenEnvVar`
/ `OAuthTokenUrl`, `ServiceLogins`).

## Security contract

- Nothing in an issue's title, description, or comments can cause ingestion or
  widen it — only the configured signal (upstream metadata) authorises it.
- Ingested content becomes the prompt with an untrusted-input header; agent,
  credentials, grants, capabilities, and priority are never sourced upstream.
- An upstream failure never fails a work item: exceptions become `Failed`
  results and `SyncFailed` audit records.
- Webhook signatures (HMAC-SHA256 over raw bytes) are verified before parsing;
  oversized bodies are rejected before buffering; all bounds are config, not literals.
- No secrets in logs or error text — only ids, status codes, and env-var names.

## Question replies

A surfaced question comment ends with `<!-- codeybox-question:q-001 -->`.
Reply in Linear with the question id as a prefix:

```
q-001: use forward-only migrations
```

The host matches the prefix against the item's open questions and answers
through the question store. Replies from the service account (or carrying the
CodeyBox marker) are ignored.

## Limitations

- The ingestion key is the human identifier (`ENG-123`), because external ids
  must not be UUIDs. Moving a signalled issue across teams renames its key;
  avoid moving in-flight issues or a second item will be ingested.
- Poll candidates carry no last-actor login (Linear's list API exposes none);
  loop safety for comments rests on the marker + service-login check, and
  re-reads converge idempotently on the external id.
- Webhook `Issue` payloads that omit labels/assignee parse as unsignalled and
  are skipped; the next poll (full data) ingests them. Polling is the source
  of truth; webhooks accelerate it.
