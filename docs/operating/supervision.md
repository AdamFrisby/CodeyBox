# Agent supervision

Agent supervision is a config-gated human-in-the-loop channel for live agent
invocations. It is off by default.

When enabled, the orchestrator exposes one multiplexed SignalR surface over the
existing `/hubs/agent-stdout` hub. A client can subscribe once and see every
live worker, rework, merge, conflict-resolution, and LLM-auditor agent session
that CodeyBox is currently driving. Each invocation remains isolated: worker
and auditor runs get separate supervision session IDs, separate stdout streams,
and separate injection queues.

## Why This Is Not An ACP Server

The current Claude ACP integration is a per-agent transport: CodeyBox starts a
small in-sandbox bridge for one Claude `--ide` turn, and that bridge owns one
underlying ACP session (`session/new` or `session/load`) until the turn exits.
It is not a long-lived multi-session ACP front door that an editor can attach
to and browse across all workers. CodeyBox therefore uses its own multiplexed
orchestrator protocol for human supervision.

If ACP later grows a stable multi-session server/client shape, this layer can
be adapted behind the same config gate. The operator-facing contract is the
orchestrator front door, not direct access to a worker's private ACP session.

## Configuration

```json
{
  "CodeyBox": {
    "AgentSupervision": {
      "Enabled": false,
      "MaxPromptChars": 16384,
      "MaxOutputBufferChars": 131072,
      "MaxInjectionChars": 8192,
      "InjectionQueueCapacity": 16,
      "CompletedSessionRetentionSeconds": 300,
      "MaxSessions": 512
    }
  }
}
```

`Enabled=false` means no supervision sessions are registered, no human
injection queue is opened, and autonomous behavior is unchanged.

## SignalR Protocol

Connect to `/hubs/agent-stdout` with the normal CodeyBox bearer token.

Client-to-server methods:

| Method | Purpose |
|---|---|
| `SubscribeAllSupervisionAsync()` | Join the fleet-wide supervision stream. |
| `SubscribeSupervisionSessionAsync(sessionId)` | Join one session stream. |
| `ListSupervisionSessionsAsync()` | Return current active/recent session snapshots. |
| `InjectSupervisionAsync(sessionId, { message, actor })` | Queue one human instruction for a session. |

Server-to-client events:

| Event | Payload |
|---|---|
| `supervisionSessionStarted` / `supervisionSessionUpdated` / `supervisionSessionCompleted` | Session snapshot. |
| `supervisionCommand` | Prompt/turn CodeyBox sent to the agent, redacted and truncated. |
| `supervisionStdoutChunk` | Redacted stdout chunk for one session. |
| `supervisionInjectionQueued` / `supervisionInjectionStarted` / `supervisionInjectionCompleted` | Human-injection lifecycle. |

## REST Helpers

`GET /agent-supervision/sessions` accepts paging query parameters and returns:

```json
{
  "enabled": true,
  "total": 12,
  "skip": 0,
  "take": 2,
  "sessions": [ /* AgentSupervisionSessionSnapshot[] */ ]
}
```

Query parameters:

| Name | Default | Description |
|---|---|---|
| `skip` | `0` | Number of sessions to skip from the start of the most-recently-started ordering. |
| `take` | `CodeyBox:AgentSupervision:DefaultListPageSize` (64) | Page size, clamped to `MaxListPageSize` (256). |
| `includeOutputTail` | `true` | When `false`, returned sessions carry an empty `outputTail`. |
| `outputTailMaxChars` | (full buffer) | Hard limit on the per-session output-tail length. |
| `recentCommandsLimit` | `RetainedCommandsPerSession` | Cap on the number of recent CodeyBox commands returned per session. |

Each session snapshot also carries a `recentCommands` array — the recent
CodeyBox prompts that drove this session (autonomous + human-injection),
each with `kind`, `injectionId` (when applicable), `sentAt`, and the
redacted/truncated `prompt`. A supervisor that joins after a session has
already started can review these to understand what CodeyBox has been
saying to the agent without having to be subscribed to the live SignalR
stream.

`POST /agent-supervision/sessions/{sessionId}/injections` accepts:

```json
{
  "actor": "alice",
  "message": "Before you finish, add the regression test for the parser case."
}
```

`actor` is a display label only. The audit trail records a server-derived
authoritative principal (`apikey:<fingerprint>@<remote-ip>` for the bearer-
token path; `user:<name>` once an authenticated-user scheme is wired) and
the client-supplied label is appended for human context. Client-supplied
actor strings are not trusted as identity because the bearer-token auth
layer does not bind a per-user identity to the request.

REST injection is useful for scripts; SignalR is the live stream.

## Injection Semantics

Most CodeyBox agent CLIs are one-shot processes. A human instruction therefore
queues while the current CodeyBox turn is running and is delivered immediately
after that turn completes, inside the same sandbox and working tree. Native
session-capable runners still receive the instruction through their normal
turn mechanism when the pipeline uses them.

The injected message is wrapped as a normal follow-up prompt, not written into
provider transcript files. This preserves Claude resumable-session and
thinking-block invariants: sanitisation remains the runner's responsibility,
and the operator instruction is just another turn.

Injection results affect the phase result. If an injected follow-up succeeds
after an earlier failed turn, the phase can continue. If the injected turn
fails, the phase observes that failure.

## Audit

Every injection is written to the audit log:

- `agent.supervision_injection_queued`
- `agent.supervision_injection_started`
- `agent.supervision_injection_completed`

The events include actor, work item, supervision session ID, phase, agent, and
a redacted/truncated instruction or summary.
