# Teams notifications (`codeybox.teams`)

A CodeyBox notification provider plugin for Microsoft Teams: work-item and
fleet notifications rendered as native Adaptive Cards (severity styling,
fields as facts, offered actions as submit controls with a custom-answer
follow-up), posted through the Bot Framework Connector, and landed
decisions reflected back by refreshing the originating card. Deep links
route the operator to Agnes for live steering — linked, never
reimplemented here.

One project, one plugin:
`plugins/notifications/CodeyBox.TeamsPlugin/`. Off unless an operator
enables it (see below).

## How it fits together

```
rule fires → provider "teams" → Connector POST .../activities
        │      (Adaptive Card + submit controls)
        │  operator presses a button / sends the follow-up prompt
        ▼
Bot Framework POSTs the activity to the bot's messaging endpoint
        ▼
POST /webhooks/interactions/teams  ← botframework-jwt bearer token
        ▼
existing question store AnswerAsync (no second answer path)
        ▼
original card refreshed (activity update with what was decided and by whom)
```

Outbound and inbound meet only at the foundation's contracts: the submit
`value` this plugin emits is the binding the host's inbound parser accepts,
and both ends are pinned by the same recorded-shape test
(`TeamsInteractionFoundationTests`, `TeamsInteractionEndpointsTests`).

## Teams-side setup

1. Register a bot at https://dev.botframework.com (or an Azure Bot
   resource) and note the **Microsoft App ID** and client secret.
   Add the **Teams channel** to the bot.
2. Note the bot's **service URL** and target **conversation ID** (the
   channel to post to). The plugin logs a warning and skips delivery until
   both are configured.
3. Set the bot's **messaging endpoint** to
   `https://<your-host>/webhooks/interactions/teams`. The Bot Framework
   retries until the URL answers; the host verifies every delivery as a
   signed Bot Framework bearer token and answers replays with
   `{status: "duplicate"}` without touching state.
4. Copy the App ID and App password into the credential chain (see below).

## CodeyBox configuration

The plugin loads only when it is both allowlisted and enabled, like every
plugin (see [`plugins.md`](plugins.md)):

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.teams"],
      "Enabled": ["codeybox.teams"],
      "codeybox.teams": {
        "Enabled": true,
        "ServiceUrl": "https://smba.trafficmanager.net/teams/",
        "ConversationId": "19:channel-id@thread.tacv2",
        "AgnesBaseUrl": "https://agnes.example.invalid"
      }
    },
    "Notifications": {
      "Rules": [
        { "Condition": "operator_question", "Providers": ["teams"] }
      ],
      "Interactions": {
        "Enabled": true,
        "Providers": [
          {
            "Provider": "teams",
            "Scheme": "botframework-jwt",
            "SigningSecretEnvVar": "CODEYBOX_TEAMS_APP_ID",
            "AllowedChannels": ["19:channel-id@thread.tacv2"],
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
| Bot App ID | `CODEYBOX_TEAMS_APP_ID` | Microsoft App ID; also the expected token audience for inbound verification |
| Bot App password | `CODEYBOX_TEAMS_APP_PASSWORD` | Client secret used for Connector token acquisition |

Connecting the integration is itself the grant (see
[`interactions.md`](interactions.md)): any member of the connected
conversation may approve from it once the bearer token verifies. Narrow
with `AllowedChannels` (exact conversation IDs) and optionally
`AllowedUsers` (exact Teams user IDs, compared by exact ordinal equality).

### Verification is a bearer token, not an HMAC

Do not assume the Slack shape here. The Bot Framework signs each activity
with a JWT bearer token (`Authorization: Bearer <RS256 JWT>` issued by
`https://api.botframework.com` for this bot's App ID), verified against
Microsoft's JWKS — the token authenticates the Bot Framework service as
the sender over TLS rather than binding the body bytes the way an HMAC
does. The `SigningSecretEnvVar` on a `botframework-jwt` provider names
the env var holding the expected App ID (audience), not a shared secret.
Replay protection comes from the token's own expiry plus the endpoint's
interaction-id dedup and question-state checks, so there is no
sender-timestamp window. See [`interactions.md`](interactions.md).

## Inbound exposure

- **Buttons mode** (default) needs the Bot Framework to reach the host:
  expose `POST /webhooks/interactions/teams` at a public HTTPS URL and set
  it as the bot's messaging endpoint.
- **Outbound-only deployments** (no inbound path) set
  `"ActionsMode": "Links"`: questions render with *Answer here* /
  *Open in Agnes* link actions only, so a prompt is never unanswerable.
  Leave `Interactions.Enabled` off entirely in that case — nothing inbound
  is exposed.

## Behaviour notes

- **Cards**: severity renders natively (card accent container,
  🚨/⚠️/ℹ️ badge), the summary/body as wrapped text, and up to 10
  structured fields as a fact set. Over-long text truncates with a marker;
  a binding that would exceed the submit-data budget degrades to the answer
  links instead of posting a control that cannot resolve.
- **Multi-step flows**: each offered option renders as its own
  `Action.Submit`, and a *Custom answer…* follow-up prompt (an
  `Action.ShowCard` with a text input) sends free text against the same
  question binding — a question with several options or a follow-up prompt
  is expressed natively rather than flattened to approve/reject. The
  follow-up appears only when every offered action answers the same
  question; mixed-question cards fall back to per-option buttons plus the
  answer links.
- **Loop-close**: after an answer lands, the originating card is refreshed
  to `Decided: <answer> — by teams:<userId> (<login>)` via an activity
  update. Card refresh after a decision is expected behaviour here, not a
  nicety. The refresh is best-effort — a delivery failure never affects the
  work item.
- **Connector tokens**: the plugin acquires a Bot Framework token via
  OAuth2 client-credentials and caches it until shortly before expiry, so
  steady-state delivery costs one Connector call per notification.
  The token and App password are never stored in config or logs.
- **Agnes links**: `AgnesBaseUrl` supplies an *Open in Agnes* action per
  work item (`{AgnesBaseUrl}/workitems/{id}`); leave it empty to omit the
  link. The *Answer here* action points at CodeyBox's own questions page
  and appears whenever the notification carries one.

## Verification

- `dotnet test --filter "FullyQualifiedName~Teams"` — outbound rendering,
  decision refresh, token caching, bearer-token verification, native
  activity parsing, and the signed end-to-end loop (answer-once, forged /
  expired token rejection, stale-submit 409, capability declaration).
- No live-tenant test exists: Teams has no sandbox API and CI cannot hold
  tenant bot credentials, so the suite runs against the recorded activity
  shape in `tests/CodeyBox.Tests/Fixtures/Teams/submit-activity.json`
  with RSA-signed bearer tokens minted in-test instead.
