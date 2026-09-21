# CodeyBox: Jira Work Sync

One project, one plugin implementing `IWorkSource` (inbound) and `IWorkTracker`
(outbound) against the Jira Cloud REST API v3 (with a legacy search fallback
for Server/Data Center). Off unless an operator enables it: the assembly loads
only when allowlisted **and** named in `Plugins:Enabled`, and it polls/posts
nothing until `Enabled=true` in its own section.

## What it does

1. **Ingestion** — polls recently-updated Jira issues per mapped project and
   parses Jira webhooks for issues carrying the operator-configured signal
   (default: assignment to a service account; a label or a workflow-status
   value work too). Signalled issues become work items with the Jira key
   (`PROJ-123`) in `ExternalIds["jira"]`. The same key converges webhook
   redelivery, polling overlap, and manual re-sync onto one item — never a
   duplicate.
2. **Sync back** — posts progress on mapped state transitions, surfaces open
   questions as comments, reports commit SHAs / PR links, and reports terminal
   completion or failure by executing the mapped workflow transition.
3. **State mapping** is an explicit operator declaration (`StateMapping`).
   An unmapped state returns `UnmappedState` and writes nothing — never
   guessed. Because Jira workflows are per-project and operator-defined,
   transitions are never free-form status writes: the target must be reachable
   from the issue's current state (matched exactly against the issue's
   available transitions), and an unreachable target is a reported `Failed`
   outcome — nothing is written.
4. **Loop safety** — every outbound body carries
   `<!-- codeybox-work-item:{id} -->` (applied by `WorkTrackerService`); our
   own comments are recognised by that marker or by the service login and
   ignored on the way back in. Progress posts only on changed statuses.
5. **Webhooks and polling both.** `ManageWebhooks=true` registers the delivery
   URL with Jira at startup and renews its 30-day expiry (recreating it when
   absent — never assuming it persists). Deployments without inbound traffic
   leave it off and poll.
6. **Credentials from the credential chain.** Configuration holds only
   environment-variable *names*; values come from the host environment
   (vault agent, systemd credentials, container secrets). API tokens travel as
   Basic auth; OAuth 3LO access tokens are refreshed in-process with
   single-flight caching.
7. **Capability honesty** — `SupportsWebhooks+SupportsPolling`,
   `CanPostComments+CanSetStatus`. Anything unmappable, unreachable, or
   unfindable is reported (`UnmappedState`, `Failed` with detail), never
   silently degraded.

## Jira-side setup

1. Create a service account and note its account id (preferred) or email.
   For Jira Cloud, create an API token for that account
   (Atlassian account → Security → API tokens).
2. Decide the signal: assign issues to the service account (recommended),
   or apply a label (e.g. `codeybox`), or move issues to a triage status.
3. Export the token and email where the host reads the chain, e.g.
   `JIRA_USER_EMAIL` and `JIRA_API_TOKEN`.
4. For webhooks: pick an unguessable delivery token, set the delivery URL to
   your CodeyBox endpoint with that token in the query string, and export the
   same token as `JIRA_WEBHOOK_SECRET`. Jira Cloud webhooks are **unsigned** —
   this token is the delivery authentication, so treat it like a credential.

## Configuration

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.jira-worksync"],
      "Enabled": ["codeybox.jira-worksync"],
      "AssemblyPaths": ["/etc/codeybox/plugins/CodeyBox.JiraWorkSyncPlugin.dll"]
    },
    "Plugins:codeybox.jira-worksync": {
      "Enabled": true,
      "ApiBaseUrl": "https://acme.atlassian.net",
      "SignalKind": "Assignee",
      "SignalValue": "712020:3f1a2b3c-4d5e-6f70-a8b9-c0d1e2f3a4b5",
      "ProjectMap": { "PROJ": "my-app" },
      "StateMapping": { "Working": "In Progress", "Done": "Done", "Failed": "Done" },
      "TokenEnvVar": "JIRA_API_TOKEN",
      "UserEmailEnvVar": "JIRA_USER_EMAIL",
      "ManageWebhooks": false,
      "WebhookUrl": "https://codeybox.example.com/webhooks/jira?token=REPLACE-ME",
      "WebhookSecretEnvVar": "JIRA_WEBHOOK_SECRET"
    }
  }
}
```

All values are hot-reloadable (re-read per poll/post). Full reference: every
property on `JiraWorkSyncOptions` is a config key (`TimeoutSeconds`,
`PageSize`, `MaxItemsPerPoll`, `MaxIngestedBodyChars`,
`OAuthClientIdEnvVar` / `OAuthClientSecretEnvVar` /
`OAuthRefreshTokenEnvVar` / `OAuthTokenUrl` / `OAuthCloudId`,
`ServiceLogins`).

OAuth 3LO (instead of an API token): set the three `OAuth*EnvVar` names and
`OAuthCloudId` (the tenant id; find it via
`https://{tenant}.atlassian.net/rest/api/3/_edge/tenant_info`). API calls then
go to `https://api.atlassian.com/ex/jira/{cloudId}` with a refreshed Bearer
token. Basic-auth env vars are ignored while OAuth is fully configured.

## Security contract

- Nothing in an issue's summary, description, or comments can cause ingestion
  or widen it — only the configured signal (upstream metadata) authorises it.
- Ingested content becomes the prompt with an untrusted-input header; agent,
  credentials, grants, capabilities, and priority are never sourced upstream.
- An upstream failure never fails a work item: exceptions become `Failed`
  results and `SyncFailed` audit records.
- Webhook deliveries are authenticated by the shared-secret query token
  before parsing (constant-time comparison); oversized bodies are rejected
  before buffering; all bounds are config, not literals.
- No secrets in logs or error text — only ids, status codes, and env-var names.

## Question replies

A surfaced question comment ends with `<!-- codeybox-question:q-001 -->`.
Reply in Jira with the question id as a prefix:

```
q-001: use forward-only migrations
```

The host matches the prefix against the item's open questions and answers
through the question store. Replies from the service account (or carrying the
CodeyBox marker) are ignored.

## Limitations

- Jira's v3 `description` is Atlassian Document Format; the plugin extracts
  plain text (formatting is not preserved) and posts comments the same way.
- Poll candidates carry no last-actor login; loop safety for comments rests
  on the marker + service-login check, and re-reads converge idempotently on
  the issue key.
- Webhook `jira:issue_*` payloads that omit labels/assignee parse as
  unsignalled and are skipped; the next poll (full data) ingests them.
  Polling is the source of truth; webhooks accelerate it.
- Webhook registrations expire after 30 days; with `ManageWebhooks=true` the
  plugin renews its own registration on every host start. If the host runs
  longer than 30 days without a restart, re-start it (or re-run registration)
  so the renewal executes — expiry is Jira-side and cannot be disabled.
