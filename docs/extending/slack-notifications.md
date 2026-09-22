# Slack notifications (`codeybox.slack`)

A CodeyBox notification provider plugin for Slack: work-item and fleet
notifications rendered as native Block Kit, follow-ups threaded per work
item, offered actions as buttons that resolve through the verified inbound
endpoint, and landed decisions reflected back into the originating message.
Deep links route the operator to Agnes for live steering — linked, never
reimplemented here.

One project, one plugin:
`plugins/notifications/CodeyBox.SlackPlugin/`. Off unless an operator
enables it (see below).

## How it fits together

```
rule fires → provider "slack" → chat.postMessage (Block Kit + buttons)
        │
        │  operator presses a button
        ▼
Slack POSTs block_actions to the Request URL (form body, signed)
        ▼
POST /webhooks/interactions/slack  ← slack-v0 HMAC + replay window
        ▼
existing question store AnswerAsync (no second answer path)
        ▼
original message updated (chat.update with what was decided and by whom)
```

Outbound and inbound meet only at the foundation's contracts: the button
`value` this plugin emits is the binding the host's inbound parser accepts,
and both ends are pinned by the same recorded-shape test
(`SlackInteractionParserTests`, `SlackInteractionEndpointsTests`).

## Slack-side setup

1. Create a Slack app at https://api.slack.com/apps (from scratch or from
   the manifest below) and install it to the workspace.
2. **OAuth & Permissions → Bot Token Scopes**: add `chat:write`. Copy the
   **Bot User OAuth Token** (`xoxb-…`) into the credential chain (see below).
3. Invite the bot to the channel (`/invite @botname`) — or post to a channel
   the bot is already in.
4. **Interactivity & Shortcuts → Interactivity**: on, with Request URL
   `https://<your-host>/webhooks/interactions/slack`. Slack retries until
   the URL answers; the host verifies every delivery and answers replays
   with `{status: "duplicate"}` without touching state.
5. **Basic Information → App Credentials**: copy the **Signing Secret** into
   the credential chain for the inbound verifier.

Minimal app manifest (JSON) equivalent:

```json
{
  "display_information": { "name": "CodeyBox" },
  "oauth_config": { "scopes": { "bot": ["chat:write"] } },
  "settings": {
    "interactivity": {
      "is_enabled": true,
      "request_url": "https://HOST/webhooks/interactions/slack"
    }
  }
}
```

## CodeyBox configuration

The plugin loads only when it is both allowlisted and enabled, like every
plugin (see [`plugins.md`](plugins.md)):

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.slack"],
      "Enabled": ["codeybox.slack"],
      "codeybox.slack": {
        "Enabled": true,
        "BotTokenEnvVar": "CODEYBOX_SLACK_BOT_TOKEN",
        "DefaultChannel": "C012345",
        "AgnesBaseUrl": "https://agnes.example.invalid"
      }
    },
    "Notifications": {
      "Rules": [
        { "Condition": "operator_question", "Providers": ["slack"] }
      ],
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

Credentials and verification secrets come from the credential chain
(environment variables named above), never configuration files:

| Secret | Env var (configurable name) | What it is |
|---|---|---|
| Bot token | `CODEYBOX_SLACK_BOT_TOKEN` | `xoxb-…` from OAuth & Permissions |
| Signing secret | `CODEYBOX_SLACK_SIGNING_SECRET` | From Basic Information → App Credentials |

Connecting the integration is itself the grant (see
[`interactions.md`](interactions.md)): any member of the connected channel
may approve from it once the signature verifies. Narrow with
`AllowedChannels` (exact channel IDs) and optionally `AllowedUsers` (exact
platform user IDs).

## Inbound exposure

- **Buttons mode** (default) needs Slack to reach the host: expose
  `POST /webhooks/interactions/slack` at a public HTTPS URL and set it as
  the app's Request URL.
- **Outbound-only deployments** (no inbound path) set
  `"ActionsMode": "Links"`: questions render with *Answer here* /
  *Open in Agnes* link buttons only, so a prompt is never unanswerable.
  Leave `Interactions.Enabled` off entirely in that case — nothing inbound
  is exposed.

## Behaviour notes

- **Threads**: the first notification for a work item posts top-level; its
  timestamp becomes the thread root and every follow-up for the same work
  item replies in that thread, so a long-running item reads as one
  conversation. Fleet notifications (no work item bound) post top-level.
  Thread state is bounded (10 000 entries, 24 h lifetime by default) and
  kept in memory — a restart starts new threads rather than failing.
- **Severity and fields** render natively: attachment colour
  (`good`/`warning`/`danger`), an emoji header, section text, and up to 10
  structured fields. Over-long text truncates with a marker; buttons whose
  binding would exceed Slack's 2000-character value budget degrade to the
  answer links instead of posting an unresolvable button.
- **Loop-close**: after an answer lands, the originating message is updated
  to `Decided: <answer> — by slack:<userId> (<login>)`, via `chat.update`
  when the bot token is available and via Slack's `response_url` in any
  case. Both are best-effort — a delivery failure never affects the work
  item.
- **Agnes links**: `AgnesBaseUrl` supplies an *Open in Agnes* button per
  work item (`{AgnesBaseUrl}/workitems/{id}`); leave it empty to omit the
  link. The *Answer here* link points at CodeyBox's own questions page and
  appears whenever the notification carries one.

## Verification

- `dotnet test --filter "FullyQualifiedName~Slack"` — outbound rendering,
  threading, decision updates, native-payload parsing, and the signed
  end-to-end loop (answer-once, tamper/replay rejection, stale-button 409,
  capability declaration).
- No live-workspace test exists: Slack has no sandbox API and CI cannot
  hold workspace credentials, so the suite runs against the recorded
  `block_actions` envelope in `SlackInteractionParserTests` instead.
