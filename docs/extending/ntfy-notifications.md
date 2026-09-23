# ntfy notifications (`codeybox.ntfy`)

A CodeyBox notification provider plugin for [ntfy](https://ntfy.sh) —
self-hosted or ntfy.sh — with mobile push notifications and actionable
HTTP buttons. Work-item and fleet notifications publish to a topic as JSON
(priority, severity tag, fields), offered actions render as native `http`
action buttons that resolve through the verified inbound endpoint, and a
landed decision republishes over the same `sequence_id` so the question
notification turns into the decision. Deep links route the operator to
Agnes for live steering — linked, never reimplemented here.

One project, one plugin:
`plugins/notifications/CodeyBox.NtfyPlugin/`. Off unless an operator
enables it (see below).

## How it fits together

```
rule fires → provider "ntfy" → POST {ntfy}/  (publish-as-JSON + actions)
        │
        │  operator taps a button on the subscribed device
        ▼
the device itself POSTs the button's body to the host
        ▼
POST /webhooks/interactions/ntfy  ← sha256 HMAC over the exact body
        ▼
existing question store AnswerAsync (no second answer path)
        ▼
original notification replaced (same sequence_id → decided + by whom)
```

Outbound and inbound meet only at the foundation's contracts: the `body`
this plugin emits is the canonical interaction payload the host's inbound
endpoint accepts, and both ends are pinned by the same recorded-shape
tests (`NtfyNotificationProviderTests`, `NtfyInteractionEndpointsTests`).

## Verification is genuinely different here

ntfy action buttons invoke HTTP endpoints **directly from the client** —
the platform signs nothing and there is no sender timestamp. So instead of
verifying a platform signature, this provider *mints* the signature at
publish time: each button's `body` is the canonical interaction payload
and each button carries `X-CodeyBox-Signature: sha256=<HMAC-SHA256 over
those exact bytes>` keyed by the shared interaction secret. The endpoint
verifies the MAC over the raw body before parsing — a captured header can
replay only the one decision that button already grants, and replays are
absorbed by the dedup claim and the question-state checks.

The MAC — never the secret — travels inside the notification. Anyone who
can read the topic can press the buttons: protect the topic (ACLs on a
self-hosted server, an unguessable name on ntfy.sh). Connecting the
integration is itself the grant (see
[`interactions.md`](interactions.md)); `AllowedChannels` narrows it to
exact topic names. `AllowedUsers` has no meaningful narrowing here — ntfy
delivers no per-press user identity, so answers are recorded as
`ntfy:subscriber`.

## ntfy-side setup

1. Pick or deploy a server: `https://ntfy.sh` or a self-hosted instance.
2. Choose a topic. On ntfy.sh a topic name is effectively a password —
   use an unguessable one. On a self-hosted server, also enable ACLs and
   create an access token (`ntfy token add …`) if the topic is protected.
3. If you use an access token, put it in the environment variable named by
   `TokenEnvVar` (default `CODEYBOX_NTFY_TOKEN`). Open topics need none.
4. Subscribe to the topic in the ntfy mobile or web app.

## CodeyBox configuration

The plugin loads only when it is both allowlisted and enabled, like every
plugin (see [`plugins.md`](plugins.md)):

```json
{
  "CodeyBox": {
    "PublicBaseUrl": "https://codeybox.example.invalid",
    "Plugins": {
      "Allowlist": ["codeybox.ntfy"],
      "Enabled": ["codeybox.ntfy"],
      "codeybox.ntfy": {
        "Enabled": true,
        "BaseUrl": "https://ntfy.sh",
        "DefaultTopic": "codeybox-fleet-9f2c",
        "TokenEnvVar": "CODEYBOX_NTFY_TOKEN",
        "InteractionSecretEnvVar": "CODEYBOX_NTFY_INTERACTION_SECRET",
        "AgnesBaseUrl": "https://agnes.example.invalid"
      }
    },
    "Notifications": {
      "Rules": [
        { "Condition": "operator_question", "Providers": ["ntfy"] }
      ],
      "Interactions": {
        "Enabled": true,
        "Providers": [
          {
            "Provider": "ntfy",
            "Scheme": "ntfy-hmac",
            "SigningSecretEnvVar": "CODEYBOX_NTFY_INTERACTION_SECRET",
            "AllowedChannels": ["codeybox-fleet-9f2c"],
            "AllowedUsers": []
          }
        ]
      }
    }
  }
}
```

Credentials and verification secrets come from the credential chain
(environment variables named above), never configuration files:

| Secret | Env var (configurable name) | What it is |
|---|---|---|
| Publish token | `CODEYBOX_NTFY_TOKEN` | ntfy access token (`tk_…`) for ACL-protected topics; optional |
| Interaction secret | `CODEYBOX_NTFY_INTERACTION_SECRET` | Operator-chosen shared secret; `SigningSecretEnvVar` must name the same variable |

## Inbound exposure — read this before enabling buttons

The ntfy **client** (phone, browser) calls the button URL itself — the
ntfy server never contacts CodeyBox. Buttons therefore work only when the
device holding the subscription can reach
`https://<host>/webhooks/interactions/ntfy`:

- `PublicBaseUrl` must be a URL that resolves **on the operator's
  device**, which may differ from how the host sees itself (NAT,
  split-horizon DNS, VPN). The plugin-specific `PublicBaseUrl` overrides
  `CodeyBox:PublicBaseUrl` when they differ. HTTPS is required; HTTP is
  accepted only for loopback.
- A self-hosted deployment with **no inbound exposure** (or a phone on a
  network that cannot reach the host) must set
  `"ActionsMode": "Links"`: questions render with *Answer here* /
  *Open in Agnes* view actions only, so a prompt is never unanswerable.
  Leave `Interactions.Enabled` off entirely in that case — nothing inbound
  is exposed.

## Behaviour notes

- **Severity and fields** render natively: `priority` (5 critical / 4
  warning / 3 default), a severity tag (`rotating_light` / `warning` /
  `information_source`), the body as plain text (ntfy renders markdown on
  the web app only), and up to `MaxFields` `key: value` lines. Over-long
  text truncates with a marker (`MaxMessageChars` stays under ntfy's
  4096-byte message ceiling; `MaxTitleChars` under the 1 KB title limit).
- **Actions**: ntfy accepts at most three buttons per message. `http`
  answer buttons take slots first; *Answer in CodeyBox* / *Open in Agnes*
  view actions fill what remains, and any link that does not fit is
  appended to the message body as a plain `Label: URL` line. Buttons whose
  answer would exceed the endpoint's bound degrade the same way. Buttons
  carry `clear: true` so a successful press dismisses the prompt.
- **Loop-close**: after an answer lands, the provider republishes the
  notification with the same `sequence_id` — ntfy clients replace the
  question with `Decided: <answer> — by ntfy:subscriber`. Best-effort: a
  delivery failure never affects the work item. If the update arrives at a
  client that already cleared the prompt it appears as a new decision
  notification.
- **Topic routing**: `Recipients` on the notification win over
  `DefaultTopic`. The decision update republishes to the topic the
  question actually went to (remembered per correlation token, bounded and
  expiring in memory; a restart falls back to `DefaultTopic`).
- **Click action**: tapping the notification opens `AnswerUrl` when the
  notification carries one, else the Agnes work-item link.
- **Agnes links**: `AgnesBaseUrl` supplies the *Open in Agnes* link per
  work item (`{AgnesBaseUrl}/workitems/{id}`); leave it empty to omit.

## Verification

- `dotnet test --filter "FullyQualifiedName~Ntfy"` — outbound rendering,
  signed-button minting, links-mode and degraded rendering, decision
  republish over `sequence_id`, the verifier, and the signed end-to-end
  loop (answer-once, tamper/replay rejection, channel allowlist, stale
  question 409, capability declaration).
- No live-server test exists: ntfy has no sandbox API and CI cannot hold
  server credentials, so the suite pins the recorded shape by feeding the
  provider's own published action (body + MAC header) back through the
  real endpoint.
