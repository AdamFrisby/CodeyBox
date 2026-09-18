# Inbound interactions: answering operator questions from chat

Notifications tell an operator something happened. *Interactions* close the
loop: an operator answers a work item question from the channel where the
notification landed, and the answer resumes the work item. This document
covers the contract, the verification requirement, and the authorisation
stance. The blocking protocol itself (how agents ask) lives in
[`../concepts/agent-feedback.md`](../concepts/agent-feedback.md).

## The loop

```
agent emits <codeybox-question>
        ▼
work item parks at NeedsOperatorInput
        ▼
actionable Notification (actions + correlation token) → provider
        ▼
operator takes an action in the channel
        ▼
POST /webhooks/interactions/{provider}  ← verified, deduped, authorised
        ▼
existing question store AnswerAsync  ← one source of truth
        ▼
original message updated with what was decided and by whom (where allowed)
```

## Authorisation stance (decided)

**Connecting an integration is itself the grant.** An operator who wires a
channel to CodeyBox preemptively authorises decisions made from it; a chat
approval is sufficient on its own and does not need confirmation elsewhere.
There is no escalation tier.

What must still hold for every interaction:

1. The request genuinely came from the platform (**signature verification**).
2. It genuinely came from the connected channel or workspace, not an
   arbitrary one (**channel binding** where the platform provides one).

An operator may optionally narrow further to named users via `AllowedUsers`;
that is a refinement of the grant, not a precondition for it.

## Verification requirement

**Never act on an unverified payload. Verification precedes parsing for
meaning.** The endpoint reads the raw body bytes (capped at 64 KB before
buffering), verifies the signature over those bytes, and only then parses
JSON. An unverified interaction returns `401`, is logged with the provider
name and the fixed-vocabulary failure reason only (never the payload or a
secret), and is never partially processed. This mirrors the
`/webhooks/github/release` receiver's ordering; do not re-derive it per
provider.

Supported schemes (`Scheme` per provider entry):

| Scheme | Headers | Secret |
|---|---|---|
| `hmac-sha256` | `X-CodeyBox-Signature: sha256=<hex HMAC over raw body>`, `X-CodeyBox-Timestamp` (unix seconds) | env var named by `SigningSecretEnvVar` |
| `slack-v0` | `X-Slack-Signature: v0=<hex HMAC over "v0:{ts}:{body}">`, `X-Slack-Request-Timestamp` | Slack signing secret via `SigningSecretEnvVar` |

Header names are overridable per provider (`SignatureHeader`,
`TimestampHeader`). Timestamps outside `ReplayWindow` (default 5 minutes)
are rejected as replays. Secrets come from the credential chain
(environment variables named in config); raw secrets never appear in
configuration files.

Providers plug in verification by implementing `IInteractionVerifier`
(`CodeyBox.Notifications`) and registering it; the endpoint refuses any
provider with no registered verifier (`503`). Discord/Ed25519 and similar
schemes follow the same seam — no per-provider endpoint code.

## Replay and idempotency

Platforms retry deliveries. The endpoint claims the `interactionId` in an
`IInteractionDedupStore` before any state change; a replay returns `200
{status: "duplicate"}` without touching the question store. Answers bind to
the question's current state: an interaction against an already-answered,
dismissed, or resumed-away question fails with `409` and a reason
(`already answered`, `no longer open (dismissed)`, `no longer awaiting
operator input`). A mismatched `correlationToken` likewise fails as stale.
Stale buttons fail cleanly and say why — they never overwrite or resurrect
a decided question.

## Identity

The platform identity is recorded in `answeredBy` as
`{provider}:{userId} ({login})` (login omitted when absent), so a human can
audit later which platform user decided. `AllowedUsers` is an optional
exact-match (ordinal) allowlist of platform user IDs; empty means any
member of the connected channel may act. `AllowedChannels` is the same for
channel/workspace IDs; empty means any channel on the verified provider.

## Round-trip

After an answer lands, the endpoint best-effort updates the original
message (`responseUrl` when the platform supplies one, e.g. Slack) with
the decision and who made it. Delivery problems are logged and swallowed —
a notification failure never affects a work item, as the provider contract
requires.

## Honest degradation

`INotificationProvider.SupportsInteractions` declares capability. The
built-in chat (incoming webhooks) and email providers are
notification-only (`false`): they ignore `Notification.Actions` for
rendering and instead surface `Notification.AnswerUrl` as an
`Answer here: <url>` link, so a question routed to them is still
answerable elsewhere. A provider that ignores actions renders an
actionable notification exactly as it would without them when no
`AnswerUrl` is set — actions are purely additive. Deployments using only
outbound providers leave `Interactions.Enabled` (default `false`) off and
are never forced to expose an inbound path; the endpoint answers `404`
until enabled.

## Configuration

```json
{
  "CodeyBox": {
    "PublicBaseUrl": "https://codeybox.example.invalid",
    "Notifications": {
      "Interactions": {
        "Enabled": true,
        "Providers": [
          {
            "Provider": "slack",
            "Scheme": "slack-v0",
            "SigningSecretEnvVar": "CODEYBOX_SLACK_SIGNING_SECRET",
            "ReplayWindow": "00:05:00",
            "AllowedChannels": ["C012345"],
            "AllowedUsers": []
          }
        ]
      }
    }
  }
}
```

`PublicBaseUrl` feeds the `AnswerUrl` fallback link. All operational values
(replay window, allowlists, header overrides) are hot-reloadable options,
not literals. Inbound payload shape:

```json
{
  "interactionId": "evt-001",
  "workItemId": "<work-item-id>",
  "questionId": "q-001",
  "answer": "Use rollbacks.",
  "user": { "userId": "U123", "login": "alice" },
  "channelId": "C012345",
  "responseUrl": "https://hooks.slack.com/...",
  "correlationToken": "<workItemId>:<questionId>"
}
```

`GET /webhooks/interactions/capabilities` reports which inbound providers
are registered and which render providers support interactions.
