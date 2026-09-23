# Gotify notifications (`codeybox.gotify`)

A CodeyBox notification provider plugin for [Gotify](https://gotify.net) —
the self-hosted push notification server: work-item and fleet
notifications pushed over Gotify's REST API (`POST /message`) with the
application-token model, rendered with severity-as-priority, markdown
fields, and real answer links rather than a dumped text blob.

One project, one plugin:
`plugins/notifications/CodeyBox.GotifyPlugin/`. Off unless an operator
enables it (see below).

## How it fits together

```
rule fires → provider "gotify" → POST {ServerUrl}/message
              (X-Gotify-Key: <application token>)
        │
        ▼
Gotify clients render title + markdown body + priority;
tap-through opens the Answer-here / Open-in-Agnes link
        │
        ▼
operator answers in CodeyBox (questions page) or steers in Agnes
```

Gotify is **notification-only** and the plugin says so honestly
(`SupportsInteractions = false`): Gotify clients can render a message and
open a URL on tap, but the platform has no answer buttons and no signed
callback the host could verify — there is nothing to plug into the
foundation's inbound endpoint. Correspondingly the plugin ships **no**
interaction verifier: anything POSTed to
`/webhooks/interactions/gotify` is refused (404 unconfigured / 503 no
verifier) before semantic processing — never acted on, never parsed first.

A work-item question therefore surfaces with its route to answer
elsewhere: `Notification.AnswerUrl` renders as an
*Answer in CodeyBox* link in the message body **and** as the
notification's click target (`client::notification.click.url`), so a
prompt is never unanswerable. Deep links route the operator to Agnes for
live steering — linked, never reimplemented here.

## Credential model — application vs client tokens

Gotify deliberately splits its token model, and this plugin honours the
split rather than collapsing it into one secret:

| Token kind | Can | Used here? |
|---|---|---|
| **Application token** | Send messages (`POST /message`) | Yes — via `AppTokenEnvVar` |
| **Client token** | Read messages / stream (`GET /message`, `/stream`) | No — the plugin never reads |

The provider sends only, so only an application token is configured. No
client token exists to be confused with it, and no inbound path needs
credentials on this side at all.

## Gotify-side setup

1. Deploy Gotify and reach it at a URL the CodeyBox host can POST to
   (`ServerUrl`; a path prefix such as `https://host/gotify` works).
2. In the Gotify UI, create an **App** (e.g. "CodeyBox") and copy the
   application token shown once on creation into the credential chain
   (environment variable, see below).
3. Point your Gotify clients (web UI, Android app) at the server as usual
   — every client that can see the app receives the notifications.
   Per-recipient routing does not exist in Gotify, so notification
   `Recipients` are intentionally ignored.

No inbound configuration exists on the Gotify side: there is no Request
URL to set and nothing for the deployment to expose.

## CodeyBox configuration

The plugin loads only when it is both allowlisted and enabled, like every
plugin (see [`plugins.md`](plugins.md)):

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.gotify"],
      "Enabled": ["codeybox.gotify"],
      "codeybox.gotify": {
        "Enabled": true,
        "ServerUrl": "https://gotify.example.invalid",
        "AppTokenEnvVar": "CODEYBOX_GOTIFY_APP_TOKEN",
        "AgnesBaseUrl": "https://agnes.example.invalid"
      }
    },
    "Notifications": {
      "Rules": [
        { "Condition": "operator_question", "Providers": ["gotify"] }
      ]
    }
  }
}
```

Credentials come from the credential chain (the environment variable
named above), never configuration files:

| Secret | Env var (configurable name) | What it is |
|---|---|---|
| Application token | `CODEYBOX_GOTIFY_APP_TOKEN` | From Apps → the app's token in the Gotify UI |

Because the token travels in the `X-Gotify-Key` request header, a
plain-HTTP `ServerUrl` is refused unless `"AllowPlainHttp": true` — set it
only when the cleartext exposure of an internal deployment is acceptable.
HTTPS is strongly recommended.

Leave `Interactions.Enabled` off — Gotify needs and accepts nothing
inbound.

## Behaviour notes

- **Rendering**: `title` carries a severity emoji plus the notification
  title; `message` carries the body as markdown (default —
  `client::display.contentType = text/markdown`) with fields as a bullet
  list, the answer/Agnes links, and a CodeyBox footer. Set
  `"Markdown": false` for `text/plain` bodies with raw URLs.
- **Severity → priority**: Information/Warning/Critical map to Gotify's
  0–10 message priority via `InformationPriority` (2), `WarningPriority`
  (5) and `CriticalPriority` (8) — all configurable; values are clamped
  into range.
- **Click-through**: `client::notification.click.url` is the AnswerUrl
  when present, else the Agnes work-item link — a tap opens the route to
  answer or steer.
- **Markdown safety**: notification content is markdown-escaped before
  interpolation, field values are flattened to a single line so they cannot
  inject block structure, and only absolute http(s) URLs are ever emitted —
  in canonical percent-encoded form, with destinations that could break
  out of a markdown link refused.
- **Loop-close**: Gotify has no message-update API and no authenticated
  callback, so a landed decision cannot be reflected back into the
  original message; the channel of record for "what was decided and by
  whom" is the CodeyBox questions page the message links to.
- **Failures**: a delivery failure (transport, non-2xx, timeout) is logged
  and swallowed — it never affects a work item. The plugin owns its HTTP
  client and never follows redirects — a 3xx would re-send the application
  token to a server-chosen host — bounds the buffered response body at
  64 KiB, and sanitizes server-supplied error text before it can reach
  the logs.

## Inbound exposure

None required. Gotify needs no URL on the CodeyBox host; do not configure
an inbound Request URL anywhere and leave `Interactions.Enabled` off.

## Verification

- `dotnet test --filter "FullyQualifiedName~Gotify"` — outbound rendering
  (severity, fields, links, click target), markdown/plain modes, escaping,
  priority mapping, delivery-failure handling, capability declaration, and
  the endpoint refusing signed *and* unsigned `gotify` payloads without
  touching the question store.
- No live-server test exists: a real Gotify instance is an operator's own
  deployment, not a sandboxable API, so the suite pins the recorded
  `POST /message` wire shape
  (`{title, message, priority, extras{client::display, client::notification}}`)
  and the recorded error envelope in `GotifyNotificationProviderTests`
  instead.
