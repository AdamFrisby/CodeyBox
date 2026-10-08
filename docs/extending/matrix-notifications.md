# Matrix notifications (`codeybox.matrix`)

A CodeyBox notification provider plugin for [Matrix](https://matrix.org) —
the open federated messaging network: work-item and fleet notifications
posted as `m.room.message` events to explicitly configured rooms over the
[Matrix Client-Server API](https://spec.matrix.org/latest/client-server-api/)
(`PUT /_matrix/client/v3/rooms/{roomId}/send/…`), rendered with severity
markers, fields, and real answer links rather than a dumped text blob.

One project, one plugin:
`plugins/notifications/CodeyBox.MatrixPlugin/`. Off unless an operator
enables it (see below).

Pinned API surface: `/_matrix/client/v3` paths (Client-Server API v1.1+;
any homeserver implementing v1.1 or later — Synapse, Conduit and
compatible servers — serves them). Threading uses the `m.thread`
relation; idempotent retries reuse one stable transaction ID per
notification, which the server deduplicates on `(access token,
transaction ID)`.

## How it fits together

```
rule fires → provider "matrix" → GET …/state/m.room.encryption/
                                   (unencrypted? else refuse)
        │
        ▼
              PUT …/rooms/{roomId}/send/m.room.message/{txnId}
              (Authorization: Bearer <access token>)
        │
        ▼
room shows text + HTML body with Answer-here / Open-in-Agnes links;
follow-ups for the same work item arrive as m.thread replies
        │
        ▼
operator answers in CodeyBox (questions page) or steers in Agnes
```

Matrix is **notification-only** in this task and the plugin says so
honestly (`SupportsInteractions = false`): offered actions never become
interactive controls, and no inbound approvals or answers are accepted —
there is nothing to plug into the foundation's inbound endpoint.
Correspondingly the plugin ships **no** interaction verifier: anything
POSTed to `/webhooks/interactions/matrix` is refused (404 unconfigured /
503 no verifier) before semantic processing — never acted on, never
parsed first. Genuine E2EE and authorised inbound answers remain deferred
work.

A work-item question therefore surfaces with its route to answer
elsewhere: `Notification.AnswerUrl` renders as an *Answer in CodeyBox*
link in the message (raw URL in the plain-text body, anchor in the HTML
body). Deep links route the operator to Agnes for live steering — linked,
never reimplemented here.

## Unencrypted rooms only — verified, failed closed

The plugin posts **plaintext** and refuses to do otherwise:

1. **Explicit configuration.** A notification targets the first non-empty
   recipient, else `DefaultRoomId` — and the resolved room must exactly
   match an entry in `AllowedRoomIds` (or equal `DefaultRoomId` when no
   allowlist is set). Anything else is skipped with a warning, so
   untrusted notification content can never widen the destination set.
   Room IDs must look like room IDs (`!localpart:server`); user IDs,
   aliases, URLs and path tricks are refused before they reach the path.
2. **Server-side verification, every send.** Before posting, the provider
   reads the room's `m.room.encryption` state event:
   - state event present → **encrypted**: skipped with a warning.
     Encrypted rooms are never downgraded and E2EE is never claimed.
   - `M_NOT_FOUND` → **unencrypted**: the send proceeds.
   - anything else (auth failure, rate limit, server error, malformed or
     oversized body) → **unknown**: fails closed — skipped with a warning,
     exactly like encrypted.

The plugin performs **no** room creation, join, invite, knock, or
membership change. Every room must already exist with the token's user
joined; an operator adds the sender to rooms through their normal Matrix
client.

## Homeserver-side setup

1. Run (or pick) a homeserver implementing Client-Server API v1.1+ with
   `/_matrix/client/v3`, reachable from the CodeyBox host at
   `HomeserverUrl`.
2. Create (or choose) **unencrypted** rooms for CodeyBox traffic. Most
   clients create encrypted rooms by default — create the room with
   encryption disabled and verify it stays that way (the provider
   re-checks on every send and stops posting if encryption appears).
3. Join the sender user to each room with a normal Matrix client.
4. In that client (or via `POST /_matrix/client/v3/login`), obtain an
   **access token** for the sender's device and place it in the credential
   chain (environment variable, see below). Prefer a dedicated device per
   deployment so the token can be revoked independently.

No inbound configuration exists on the Matrix side: there is no Request
URL to set and nothing for the deployment to expose.

## CodeyBox configuration

The plugin loads only when it is both allowlisted and enabled, like every
plugin (see [`plugins.md`](plugins.md)):

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.matrix"],
      "Enabled": ["codeybox.matrix"],
      "codeybox.matrix": {
        "Enabled": true,
        "HomeserverUrl": "https://matrix.example.invalid",
        "AccessTokenEnvVar": "CODEYBOX_MATRIX_ACCESS_TOKEN",
        "DefaultRoomId": "!codeybox:example.invalid",
        "AllowedRoomIds": ["!codeybox:example.invalid"],
        "AgnesBaseUrl": "https://agnes.example.invalid"
      }
    },
    "Notifications": {
      "Rules": [
        { "Condition": "operator_question", "Providers": ["matrix"] }
      ]
    }
  }
}
```

Credentials come from the credential chain (the environment variable
named above), never configuration files:

| Secret | Env var (configurable name) | What it is |
|---|---|---|
| Access token | `CODEYBOX_MATRIX_ACCESS_TOKEN` | The sender user/device token |

Because the token travels in the `Authorization` header, a plain-HTTP
`HomeserverUrl` is refused unless `"AllowPlainHttp": true` — set it only
when the cleartext exposure of an internal deployment is acceptable.
HTTPS is strongly recommended.

Leave `Interactions.Enabled` off — Matrix needs and accepts nothing
inbound in this task.

## Behaviour notes

- **Rendering**: the plain-text `body` carries a severity emoji plus the
  title, the body, fields as `name: value` lines, raw answer/Agnes URLs,
  and a CodeyBox footer. The HTML `formatted_body` (default —
  `org.matrix.custom.html`; set `"UseHtml": false` for text only) carries
  the same content escaped, with real anchors. Notification content is
  HTML-escaped before interpolation, fields are flattened to a single
  line, and only absolute http(s) URLs are ever emitted — in canonical
  percent-encoded form.
- **Threading**: the first notification for a work item posts top-level
  and its event ID becomes the thread root; follow-ups post with an
  `m.relates_to: {rel_type: m.thread, event_id: <root>}` relation, so a
  long-running item reads as one conversation. The thread/event index is
  bounded (24 h / 10 000 entries by default, both configurable) and
  memory-resident — a restart starts new threads rather than failing.
- **Transaction IDs**: one stable ID per notification
  (`cbx-` + 128-bit identity digest), reused across every retry of that
  send. A rate-limit retry re-PUTs the same path and the server returns
  the original `event_id` instead of double-posting.
- **Rate limits**: `M_LIMIT_EXCEEDED` (or HTTP 429) is retried with the
  same transaction ID, honouring `retry_after_ms` capped by
  `MaxRetryDelaySeconds` (30 s), at most `MaxRetries` times (2, clamped to
  5) — then logged and swallowed. Auth failures, redirects (never
  followed — the handler disables them so the bearer token cannot leak to
  a server-chosen host), malformed or oversized responses are not retried.
- **Loop-close**: Matrix offers no message-update path in scope here, so a
  landed decision is not reflected back into the original event; the
  channel of record is the CodeyBox questions page the message links to.
- **Failures**: a delivery failure (transport, non-2xx, timeout,
  encrypted/unknown room) is logged and swallowed — it never affects a
  work item. Response bodies are bounded at 64 KiB, and server-supplied
  error text is sanitised (and token-redacted) before it can reach the
  logs. The token never appears in URLs.

## Inbound exposure

None required. Matrix needs no URL on the CodeyBox host; do not configure
an inbound Request URL anywhere and leave `Interactions.Enabled` off.

## Limits

- Outbound only; no inbound approvals/answers in this task.
- Unencrypted rooms only, verified per send; encrypted or unverifiable
  rooms are never posted to.
- No room, membership, or account lifecycle (create/join/invite/login)
  — all handled by the operator in their Matrix client.
- No E2EE support is claimed or implemented; that remains deferred work
  requiring a genuine E2EE-capable client.

## Verification

- `dotnet test --filter "FullyQualifiedName~Matrix"` — outbound rendering
  (severity, fields, links, HTML/text modes, escaping), room allowlisting
  and ID validation, encryption fail-closed (encrypted and unknown),
  thread correlation, stable transaction IDs across retries, rate-limit
  retry bounds, delivery-failure handling, capability declaration.
- No live-server test exists: a real homeserver is an operator's own
  deployment, not a sandboxable API, so the suite pins the recorded
  `PUT …/send/m.room.message/{txnId}` wire shape and the recorded state /
  error envelopes in `MatrixNotificationProviderTests` instead.
