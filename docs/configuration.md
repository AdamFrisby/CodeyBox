# Configuration reference

All CodeyBox configuration lives under the `CodeyBox` key in any standard
.NET configuration provider (`appsettings.json`, environment variables,
`CODEYBOX_EXTRA_CONFIG`, etc.).

---

## Top-level keys

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `GitRootDirectory` | string | `/var/lib/codeybox/repos` | Root for bare host git repos. |
| `StateDatabasePath` | string | `/var/lib/codeybox/state.db` | SQLite database path. |
| `SandboxImageReference` | string | `codeybox/agent:latest` | OCI image reference for agent sandboxes. |
| `AgentAllowedHosts` | string[] | `["api.anthropic.com","api.openai.com","api.githubcopilot.com","generativelanguage.googleapis.com"]` | Egress allowlist inside agent sandboxes. |
| `AuditToolAllowedHosts` | string[] | public package/vulnerability registries | Egress allowlist for network-capable tool auditors such as `deps-cve-scan`; keep this separate from agent API hosts. |
| `SandboxProvider` | string | — | One of `multipass`, `bubblewrap`, `process`. Required in non-Development environments. |
| `DangerouslyAllowProcessSandbox` | bool | `false` | Allow process sandbox outside Development. Do not use in production. |
| `UpstreamPushMaxAttempts` | int | `5` | Retry count for upstream push (GitHub PR creation). |
| `UpstreamPushBackoffSeconds` | int | `15` | Seconds between upstream push retries. |

---

## `WorkerPool`

Controls worker concurrency and spawn pacing.

```json
"WorkerPool": {
  "MaxConcurrentWorkers": 2,
  "MinSpawnIntervalMs": 0
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `MaxConcurrentWorkers` | `1` | Hard cap on simultaneously active pipelines. |
| `MinSpawnIntervalMs` | `0` | Minimum milliseconds between successive worker spawns. |

---

## `AgentClasses`

Defines named groups of interchangeable agents for quota-aware routing.
See [docs/agent-classes.md](agent-classes.md) for the full model including
`QualityScore` semantics, the floor filter, and TOD modifiers.

```json
"AgentClasses": [
  {
    "Id": "frontier-coding",
    "DisplayName": "Frontier coding agents",
    "Members": [
      { "Agent": "claude", "Billing": "Subscription", "ModelId": "claude-opus-4-7", "QualityScore": 100 },
      { "Agent": "codex",  "Billing": "Subscription", "ModelId": "gpt-5.5",         "QualityScore": 100 },
      { "Agent": "gemini", "Billing": "Subscription", "ModelId": "gemini-3-flash-preview", "QualityScore": 95, "ReasoningMode": "high" },
      { "Agent": "claude", "Billing": "PayPerApi",    "ModelId": "claude-opus-4-7", "QualityScore": 100 }
    ]
  }
]
```

Validation at startup: unique `Id`s, non-empty `Members`, valid `Billing`
values, `QualityScore` present and in 0–200, Gemini members with
`QualityScore ≥ 90` must have `ReasoningMode="high"`. A class with only
`Subscription` members emits a warning.

---

## `AgentScoreModifiers`

Small time-of-day score deltas that act as tiebreakers between near-equivalent
members. All times are UTC. See [docs/agent-classes.md](agent-classes.md#time-of-day-score-modifiers)
for the design rationale.

```json
"AgentScoreModifiers": {
  "ByTimeOfDay": [
    {
      "Agent": "claude",
      "Modifier": -1,
      "Windows": [
        {
          "Days": ["Mon", "Tue", "Wed", "Thu", "Fri"],
          "StartUtc": "14:00",
          "EndUtc": "22:00"
        }
      ]
    }
  ]
}
```

### `ByTimeOfDay` entry fields

| Field | Required | Description |
|-------|----------|-------------|
| `Agent` | yes | Agent kind to adjust: `claude`, `codex`, `gemini`, `copilot`, or a custom kind. |
| `Modifier` | yes | Signed integer added to the agent's base `QualityScore`. Bounded to ±5 at startup. |
| `Windows` | yes | One or more UTC time windows during which the modifier is active. |

### Time window fields

| Field | Required | Description |
|-------|----------|-------------|
| `Days` | yes | Array of UTC day names: `Mon`, `Tue`, `Wed`, `Thu`, `Fri`, `Sat`, `Sun`. |
| `StartUtc` | yes | Window start in `HH:mm` format (UTC, 24-hour clock). |
| `EndUtc` | yes | Window end in `HH:mm` format (UTC). If `EndUtc < StartUtc` the window wraps midnight. |

Modifiers are bounded to ±5 at startup; values outside that range are rejected
with a startup error. See [agent-classes.md](agent-classes.md) for how effective
scores interact with the `MinModelScore` floor.

---

## `QuotaRouter`

Tuning knobs for the quota probe and deferred-requeue logic.

```json
"QuotaRouter": {
  "MinQuotaPct": 10,
  "QuotaRecheckIntervalSeconds": 300,
  "QuotaCacheTtlSeconds": 60,
  "UnknownPolicy": "UseObservedFailures",
  "ObservedFailureWindowMinutes": 10,
  "ObservedFailureRetentionMinutes": 30
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `MinQuotaPct` | `10` | Minimum available-quota percentage before a Subscription member is skipped. |
| `QuotaRecheckIntervalSeconds` | `300` | Seconds to wait before re-probing when all Subscription members are exhausted. |
| `QuotaCacheTtlSeconds` | `60` | Seconds to cache a quota probe result (per probe instance). |
| `UnknownPolicy` | `UseObservedFailures` | How to treat unknown probe snapshots: `UseObservedFailures`, `FailCautious`, or opt-in `FailOpen`. |
| `ObservedFailureWindowMinutes` | `10` | Minutes a recent quota-shaped failure blocks the same agent/model across all projects. |
| `ObservedFailureRetentionMinutes` | `30` | Minutes observed quota failures remain in `state.db`. |

---

## `AuditLog`

Rolling file log configuration.

```json
"AuditLog": {
  "Path": "logs/codeybox-.json",
  "AuditPath": "logs/audit-.json",
  "RetainedDays": 30,
  "MaxFileSizeBytes": 104857600
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `Path` | `logs/codeybox-.json` | Main rolling log (all events). |
| `AuditPath` | `logs/audit-.json` | Audit-only log (`Audit=true` events). |
| `RetainedDays` | `30` | Number of rolled files to keep. Must be ≥ 1. |
| `MaxFileSizeBytes` | `104857600` | Per-file cap before rolling (100 MiB). |

---

## `AgentStreams`

Structured stdout stream capture for agent invocations. See
[docs/agent-streams.md](agent-streams.md) for file layout, CLI flags, retention,
and API endpoints.

```json
"AgentStreams": {
  "Enabled": true,
  "Path": "logs/agents",
  "MaxFileSizeMb": 32,
  "RetainedDays": 14
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `Enabled` | `true` | Request structured stream mode from supported agent CLIs and persist stdout JSONL. |
| `Path` | `logs/agents` | Root directory for per-work-item stream files. Must be writable at startup. |
| `MaxFileSizeMb` | `32` | Per-file cap. Must be ≥ 1. |
| `RetainedDays` | `14` | Daily sweep deletes older files. `0` keeps forever. |

---

## `AgentStreamAnalysis`

Read-only parser settings for agent stream analytics.

```json
"AgentStreamAnalysis": {
  "StallThreshold": "00:00:30",
  "MaxLineBytes": 67108864,
  "MaxJsonDepth": 64
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `StallThreshold` | `00:00:30` | Inter-event gap classified as a stall. Set to `00:00:00` to report every timestamped gap. |
| `MaxLineBytes` | `67108864` | Maximum JSONL event size accepted by the parser. Defaults to 64 MiB so large tool-result events fit under the default stream file cap. |
| `MaxJsonDepth` | `64` | Maximum JSON nesting depth accepted by the parser. |

---

## `Projects`

See [docs/projects.md](projects.md).

---

## `Webhooks`

See [docs/webhooks.md](webhooks.md).

---

## Environment variables used by CodeyBox

| Variable | Purpose |
|----------|---------|
| `CODEYBOX_CLAUDE_API_KEY` | Claude OAuth token (or API key). Used by the agent runner and the Claude quota probe. |
| `CODEYBOX_CODEX_API_KEY` | OpenAI API key. Used by the Codex agent runner and the Codex quota probe. |
| `CODEYBOX_COPILOT_TOKEN` | GitHub Copilot token. Used by the Copilot agent runner. |
| `CODEYBOX_API_KEY` | REST API authentication key for incoming requests. |
| `CODEYBOX_EXTRA_CONFIG` | Path to an extra JSON config file loaded last (wins over `appsettings.json`). |
| `ASPNETCORE_URLS` | Override the bind address (default `http://127.0.0.1:5000`). |
