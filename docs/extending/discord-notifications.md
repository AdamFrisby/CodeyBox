# Discord notifications (`codeybox.discord`)

A CodeyBox notification provider plugin for Discord: work-item and fleet
notifications rendered as a native embed with action-row buttons,
follow-ups threaded per work item, offered actions as buttons that resolve
through the verified inbound endpoint, and landed decisions reflected back
by editing the originating message. Deep links route the operator to Agnes
for live steering — linked, never reimplemented here.

One project, one plugin:
`plugins/notifications/CodeyBox.DiscordPlugin/`. Off unless an operator
enables it (see below).

## How it fits together

```
rule fires → provider "discord" → POST /channels/{id}/messages (embed + buttons)
        │                              │
        │                              └── thread per work item (first message roots it)
        │  operator presses a button
        ▼
Discord POSTs the interaction to the Interactions Endpoint URL (signed Ed25519)
        ▼
POST /webhooks/interactions/discord  ← discord-ed25519 + replay window
        ▼
existing question store AnswerAsync (no second answer path)
        ▼
in-band acknowledgement (ephemeral "Decided: … — by …") + original
message edited (buttons removed, decision shown)
```

Outbound and inbound meet only at the foundation's contracts: the button
`custom_id` this plugin emits is the binding the host's inbound parser
accepts, and both ends are pinned by the same recorded-shape tests
(`DiscordInteractionParserTests`, `DiscordInteractionEndpointsTests`).

## Discord-side setup

1. Create an application at https://discord.com/developers/applications →
   **New Application**.
2. **Bot → Add Bot**, copy the **Token** into the credential chain (see
   below), and turn off **Public Bot** unless you want anyone to add it.
   No privileged gateway intents are needed — this integration uses the
   REST API and interactions only, never the gateway.
3. **General Information → Interactions Endpoint URL**: set
   `https://<your-host>/webhooks/interactions/discord` and save. Discord
   immediately sends a PING challenge; the host answers it (verified, like
   every delivery) before anything else, or Discord refuses to save the URL
   and the integration cannot be configured at all.
4. **OAuth2 → URL Generator**: scopes `bot` (and `applications.commands`
   only if you also register slash commands, which this plugin does not
   need), bot permissions **Send Messages**, **Embed Links**,
   **Create Public Threads**, **Send Messages in Threads**. Open the URL,
   add the bot to your server, and note a channel ID (enable Developer
   Mode → right-click channel → Copy Channel ID).
5. **General Information → Application Public Key**: copy into the
   credential chain for the inbound verifier.

## CodeyBox configuration

The plugin loads only when it is both allowlisted and enabled, like every
plugin (see [`plugins.md`](plugins.md)):

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.discord"],
      "Enabled": ["codeybox.discord"],
      "codeybox.discord": {
        "Enabled": true,
        "BotTokenEnvVar": "CODEYBOX_DISCORD_BOT_TOKEN",
        "DefaultChannelId": "123456789012345678",
        "AgnesBaseUrl": "https://agnes.example.invalid"
      }
    },
    "Notifications": {
      "Rules": [
        { "Condition": "operator_question", "Providers": ["discord"] }
      ],
      "Interactions": {
        "Enabled": true,
        "Providers": [
          {
            "Provider": "discord",
            "Scheme": "discord-ed25519",
            "SigningSecretEnvVar": "CODEYBOX_DISCORD_PUBLIC_KEY",
            "ReplayWindow": "00:05:00",
            "AllowedChannels": ["123456789012345678"],
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
| Bot token | `CODEYBOX_DISCORD_BOT_TOKEN` | Bot → Token |
| Application public key | `CODEYBOX_DISCORD_PUBLIC_KEY` | General Information → Application Public Key (32-byte hex) |

Connecting the integration is itself the grant (see
[`interactions.md`](interactions.md)): any member of the connected
channel may approve from it once the signature verifies. Narrow with
`AllowedChannels` (exact channel IDs) and optionally `AllowedUsers`
(exact platform user IDs).

## Inbound exposure

- **Buttons mode** (default) needs Discord to reach the host: expose
  `POST /webhooks/interactions/discord` at a public HTTPS URL and set it
  as the app's Interactions Endpoint URL. Discord acknowledges every
  press against a hard deadline (~3 seconds): the host answers from the
  local question pipeline — fast enough to acknowledge in-band — and the
  channel-visible message edit follows best-effort. A delivery failure
  never affects the work item.
- **Outbound-only deployments** (no inbound path) set
  `"ActionsMode": "Links"`: questions render with *Answer in CodeyBox* /
  *Open in Agnes* link buttons only, so a prompt is never unanswerable.
  Leave `Interactions.Enabled` off entirely in that case — nothing inbound
  is exposed.

## Behaviour notes

- **Threads**: the first notification for a work item posts top-level and
  roots a thread named after it; every follow-up for the same work item
  posts into that thread, so a long-running item reads as one
  conversation — well suited to community servers where several people
  watch one item. Fleet notifications (no work item bound) post
  top-level. Thread state is bounded (10 000 entries, 24 h lifetime by
  default) and kept in memory — a restart starts new threads rather than
  failing.
- **Severity and fields** render natively: embed colour
  (red / amber / green), a unicode-emoji header, the body, and up to 10
  structured fields. Over-long text truncates with a marker. Every post
  sets `allowed_mentions: {parse: []}` so untrusted notification text can
  never ping anyone.
- **Button budget**: Discord caps `custom_id` at 100 characters, so the
  binding is a compact `cb:{workItem}:{question}:{answer}` token rather
  than JSON. Buttons whose answer would exceed the budget degrade to the
  answer/Agnes links instead of posting an unresolvable button.
- **Loop-close**: the pressing user gets an ephemeral
  `Decided: <answer> — by discord:<userId> (<login>)` acknowledgement,
  and the originating message is edited to the same decision with its
  buttons removed, via the bot API when the bot token is available. Both
  are best-effort — a delivery failure never affects the work item.
- **Agnes links**: `AgnesBaseUrl` supplies an *Open in Agnes* button per
  work item (`{AgnesBaseUrl}/workitems/{id}`); leave it empty to omit the
  link. The *Answer here* link points at CodeyBox's own questions page and
  appears whenever the notification carries one.

## Verification

- `dotnet test --filter "FullyQualifiedName~Discord"` — outbound
  rendering, threading, decision edits, native-payload parsing, Ed25519
  verification, and the signed end-to-end loop (answer-once, PING,
  tamper/replay rejection, channel allowlist, stale-button reasons,
  capability declaration).
- No live-server test exists: CI cannot hold a Discord application's
  credentials, so the suite runs against the recorded interaction envelope
  in `DiscordInteractionParserTests` instead — field values are synthetic,
  the envelope shape mirrors Discord's documented format.
