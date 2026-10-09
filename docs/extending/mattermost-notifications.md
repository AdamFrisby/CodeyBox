# Mattermost notifications (`codeybox.mattermost`)

A CodeyBox notification provider plugin for
[Mattermost](https://mattermost.com) — work-item and fleet notifications
posted to a channel over the REST API **v4** (`POST /api/v4/posts`) with a
notification-only bearer token, rendered as markdown (severity heading,
fields, real answer links), with follow-ups threaded per work item via
`root_id`.

One project, one plugin:
`plugins/notifications/CodeyBox.MattermostPlugin/`. Off unless an operator
enables it (see below).

Pinned surface: `POST {ServerUrl}/api/v4/posts` with
`Authorization: Bearer <token>` and body
`{channel_id, message, root_id?, props?}`, answered `201 {id, …}`.
Compatible with Mattermost Server v9/v10 (API v4 is the long-stable posts
path; see the [Mattermost API reference](https://docs.mattermost.com/api)).

## How it fits together

```
rule fires → provider "mattermost" → POST {ServerUrl}/api/v4/posts
              (Authorization: Bearer <notification token>)
        │
        ▼
channel shows markdown post; follow-ups for the same
work item thread under the first post (root_id)
        │
        ▼
operator answers in CodeyBox (questions page) or steers in Agnes
```

Mattermost is **notification-only** and the plugin says so honestly
(`SupportsInteractions = false`): offered actions never become buttons and
no signed callback exists that the host could verify — there is nothing to
plug into the foundation's inbound endpoint. Correspondingly the plugin
ships **no** interaction verifier: anything POSTed to
`/webhooks/interactions/mattermost` is refused (404 unconfigured / 503 no
verifier) before semantic processing — never acted on, never parsed first.

A work-item question therefore surfaces with its route to answer
elsewhere: `Notification.AnswerUrl` renders as an *Answer in CodeyBox*
link in the post body (plus an *Open in Agnes* deep link for live
steering — linked, never reimplemented here), so a prompt is never
unanswerable.

## Credential model — isolated notification token

The configured token is outbound-only: a Mattermost personal access token
that needs just post creation on the target channel. It is deliberately
**isolated from any future inbound authorization** — the plugin exposes no
inbound route and holds no signing secret, so a future approval-callback
credential would be a separate environment variable consumed by separate
code, never this token.

The plugin never creates accounts or channels and never changes
memberships: it only posts to a channel ID you name. No client secret,
no second credential, and no inbound path needs credentials on this side
at all.

## Mattermost-side setup

1. Note your Mattermost server base URL (`ServerUrl`).
2. Create a **personal access token** (Profile → Security → Personal
   Access Tokens) for the posting user; the user must already be a member
   of the target channel with permission to post there.
3. Copy the **channel ID** (not the display name): open the channel menu
   (ⓘ / channel name dropdown → View Info) and copy the 26-character ID.
   Paste the token into the credential chain (environment variable, see
   below).

No inbound configuration exists on the Mattermost side: there is no
Request URL to set and nothing for the deployment to expose.
Later operator setup (if inbound approvals are ever introduced) would be
a separate credential and a separate documented step — this plugin needs
nothing beyond the token above.

## CodeyBox configuration

The plugin loads only when it is both allowlisted and enabled, like every
plugin (see [`plugins.md`](plugins.md)):

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.mattermost"],
      "Enabled": ["codeybox.mattermost"],
      "codeybox.mattermost": {
        "Enabled": true,
        "ServerUrl": "https://mattermost.example.invalid",
        "TokenEnvVar": "CODEYBOX_MATTERMOST_TOKEN",
        "DefaultChannelId": "abcdefghij1234567890abcdef",
        "AgnesBaseUrl": "https://agnes.example.invalid"
      }
    },
    "Notifications": {
      "Rules": [
        { "Condition": "operator_question", "Providers": ["mattermost"] }
      ]
    }
  }
}
```

Credentials come from the credential chain (the environment variable
named above), never configuration files:

| Secret | Env var (configurable name) | What it is |
|---|---|---|
| Notification token | `CODEYBOX_MATTERMOST_TOKEN` | Personal access token, post rights on the channel |

Because the token travels in the `Authorization` header, a plain-HTTP
`ServerUrl` is refused unless `"AllowPlainHttp": true` — set it only when
the cleartext exposure of an internal deployment is acceptable. HTTPS is
strongly recommended.

Leave `Interactions.Enabled` off — Mattermost needs and accepts nothing
inbound.

## Behaviour notes

- **Rendering**: the post opens with a severity heading
  (`## :rotating_light: Title`), then the body as markdown with fields as
  a bullet list, the answer/Agnes links, and a CodeyBox footer. Content is
  markdown-escaped and flattened to single lines so it cannot inject
  markup; only absolute http(s) URLs are ever emitted, in canonical
  percent-encoded form, and link destinations that could break out of
  markdown are refused. Bodies truncate at `MaxTextChars` (4000) and the
  whole post stays under Mattermost's 16383-character limit.
- **Threading**: the first post for a work item becomes the thread root;
  follow-ups carry its ID as `root_id`. Set `"ThreadByWorkItem": false`
  to post everything top-level.
- **Dedup**: a bounded in-memory store (24h / 10 000 entries by default,
  hot-reloadable) remembers posted IDs per correlation token, so a
  redelivered notification posts once, never twice.
- **Ambiguous responses**: a 2xx without a usable post ID very likely
  means the post already exists, so the token is tombstoned rather than
  reposted — a later redelivery is suppressed with a warning instead of
  duplicate-spamming the channel. Definitive failures (4xx, exhausted rate
  limits) leave no tombstone, so a corrected redelivery still goes out.
- **Rate limits**: an HTTP 429 waits the server's `Retry-After` (capped by
  `MaxRateLimitWaitSeconds`, 30s) and retries at most
  `RateLimitMaxRetries` times (default 1, clamped 0–3); then the
  notification is dropped with a warning rather than hammering a limited
  server. Back-off sleeps always honour cancellation.
- **Failures**: a delivery failure (transport, non-2xx, timeout) is logged
  and swallowed — it never affects a work item. The plugin owns its HTTP
  client and never follows redirects — a 3xx would re-send the bearer
  token to a server-chosen host — bounds the buffered response body at
  64 KiB, and sanitizes server-supplied error text (controls stripped,
  token redacted, length-bounded) before it can reach the logs.
- **Loop-close**: a landed decision is not reflected back into the post;
  the channel of record for "what was decided and by whom" is the
  CodeyBox questions page the post links to.

## Inbound exposure

None required. Mattermost needs no URL on the CodeyBox host; do not
configure an inbound Request URL anywhere and leave
`Interactions.Enabled` off.

## Verification

- `dotnet test --filter "FullyQualifiedName~Mattermost"` — outbound
  rendering (severity, fields, links), channel/recipient routing, thread
  follow-ups, dedup and ambiguity tombstones, 429 retry bounds and
  cancellation, auth-failure redaction, truncation/escaping, capability
  declaration, and transport-level redirect/body-cap handling.
- No live-server test exists: a real Mattermost instance is an operator's
  own deployment, not a sandboxable API, so the suite pins the recorded
  `POST /api/v4/posts` wire shape (`{channel_id, message, root_id?,
  props?}` → `201 {id, …}`) and the recorded error envelope in
  `MattermostNotificationProviderTests` / `MattermostApiClientTests`
  instead.
