# Configuration reference

All CodeyBox configuration lives under the `CodeyBox` key in any standard
.NET configuration provider (`appsettings.json`, environment variables,
`CODEYBOX_EXTRA_CONFIG`, etc.).

## Hot-reload

CodeyBox uses the standard .NET configuration stack with `reloadOnChange: true`
on every JSON source (including `CODEYBOX_EXTRA_CONFIG`). An edit to a
configuration file is debounced by the framework's file watcher (~1 s) and
then bound atomically; consumers wired through `IOptionsMonitor<T>` see the
new value on the next read.

Hot-reloadable today:

- `Projects` — adding a new project takes effect on the next pickup. Removing
  a project that still has non-terminal work items is **rejected** by an
  `IValidateOptions<ProjectsOptions>` and the prior project list is retained;
  the repository keeps its own last-good snapshot because rejected options do
  not invoke its reload callback. The operator sees an `OptionsValidationException`
  naming the project, and knows to cancel / wait for the in-flight items first.
- `Projects[].Audit.PerIterationTimeoutMinutes` — the resolved `Project`
  record is captured once at work-item pickup, so a change mid-iteration
  does not move the goalposts for an item already running.
- `DeadWorker.MaxRecoveryAttempts` and `DeadWorker.DeadWorkerThreshold` —
  re-read on every reaper sweep.
- `MultipassExtraRuncmd` / `MultipassExtraCloudInit` / `SandboxNetworkProfiles` /
  `MultipassUseBaselineImages` / `MultipassSandbox.CloudInitReadyRetryAttempts`
  — re-read on every sandbox launch. VMs already running keep the snapshot they
  booted with.

Not hot-reloadable (rejected by `IValidateOptions<CodeyBoxOptions>` if changed):

- `SandboxProvider` — switching from `multipass` to `bubblewrap` mid-flight
  would orphan running sandboxes.
- `StateDatabasePath`, `GitRootDirectory`, `AgentStreams.Path` — captured by
  open file/SQLite handles at startup.

For `CodeyBoxOptions`, CodeyBox installs a last-known-good options cache around
`IOptionsMonitor<CodeyBoxOptions>`. This is CodeyBox policy, not default
Microsoft.Extensions.Options behavior: the stock monitor cache would throw on
`CurrentValue` after a rejected reload until another change token fires.

A field that is *neither* hot-reloadable nor explicitly guarded continues
to require a restart in practice (the consumer captured the value at
startup); we add explicit guards as we tighten the contract.

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
| `SandboxNetworkProfiles.graphical` | string | `cb-graphical` | Conventional bridge mapping for projects that explicitly select the `graphical` network profile; create it with `scripts/setup-host-networks.sh`. |
| `MultipassSandbox.CloudInitReadyRetryAttempts` | int | `3` | Number of `cloud-init status --wait` attempts before probing VM readiness when cloud-init returns exit 1. |
| `DangerouslyAllowProcessSandbox` | bool | `false` | Allow process sandbox outside Development. Do not use in production. |
| `UpstreamPushMaxAttempts` | int | `5` | Retry count for upstream push (GitHub PR creation). |
| `UpstreamPushBackoffSeconds` | int | `15` | Seconds between upstream push retries. |
| `PhaseAbsoluteTimeoutMultiplier` | number | `3.0` | Multiplier applied to a phase's per-attempt timeout to bound fallback chains. Work/rework attempts each get the full `WorkTimeout`; merge attempts each get the full `MergeTimeout`; the whole fallback chain is capped at this multiplier times that per-attempt timeout. |

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

## `AutoRetryOnQuotaFailure`

Opt-in automatic re-queue of Failed work items whose failure was caused by
quota exhaustion (Codex 5-hour rolling, Claude 7-day, Gemini daily, etc.),
once quota is available again. Only items with `FailureKind = "quota"` are
eligible; `Cancelled`, `AuditFailed`, and generic `Failed` items are not
auto-retried.

```json
"AutoRetryOnQuotaFailure": {
  "Enabled": false,
  "PeriodicCheckInterval": "00:05:00",
  "ClockDriftSafetyMargin": "00:02:00",
  "MaxAutoRetriesPerWorkItem": 3
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `Enabled` | `false` | Master switch. When false, the hosted service is registered but exits at startup; the manual `POST /workitems/{id}/retry` path is unaffected. |
| `PeriodicCheckInterval` | `00:05:00` (5 min) | Safety-net sweep cadence: every interval the scheduler re-checks every Failed quota item against the quota gate. Catches items whose probe didn't expose a reset timestamp, and re-arms after restarts where a targeted timer was lost. The sweep ignores `NextQuotaRetryAt` and asks the router directly — the targeted timer is just an optimisation. |
| `ClockDriftSafetyMargin` | `00:02:00` (2 min) | Padding added to the parsed `QuotaResetAt` before firing the targeted retry, to absorb clock drift between this orchestrator and the upstream provider. |
| `MaxAutoRetriesPerWorkItem` | `3` | Per-item lifetime cap on auto-retries. Prevents ping-pong if the failure was misclassified as quota. Manual retries do not count against this cap. |

Items paused at the project or global queue level are skipped — operators
pause queues for a reason. Each auto-retry emits a `work_item.auto_retry`
webhook (see [docs/webhooks.md](webhooks.md#auto_retry-details)).

---

## `ConfigValidation`

Optional startup cross-check that every `AgentClass` member's `ModelId`
actually exists on the provider's live model list. Catches typos
(`gemini-3-flash-preview` vs. `gemini-3.1-flash-lite`) that would otherwise
silently misroute and only surface hours later as cascading quota failures.

```json
"ConfigValidation": {
  "FailOnUnknownModel": false
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `FailOnUnknownModel` | `false` | When `false`, an unknown `ModelId` logs a Warning naming the typoed id and the valid alternatives, but startup succeeds. When `true`, startup fails-fast with a non-zero exit code so the misconfig never reaches production. Operators flip this on in production deployments. |

The validator probes each provider's model-list endpoint
(`api.anthropic.com/v1/models`, ChatGPT-OAuth `chatgpt.com/backend-api/wham/models`
or `api.openai.com/v1/models`, and Gemini's quota-bucket / `v1beta/models`
endpoints) and matches each declared `ModelId` against the response. The
total validation budget is 10 seconds; an unreachable endpoint or a slow
network falls through to a Warning that names the agent kind and the
reason, and the host still comes up. Members without a `ModelId` are not
validated — they fall through to the agent's own default.

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
