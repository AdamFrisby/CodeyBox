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
- `TemplateDirectory` — task-template files are read fresh from this directory
  on each list or queue request. Adding, editing, or removing `*.json` files
  requires no restart.
- `Projects[].Audit.PerIterationTimeoutMinutes` — the resolved `Project`
  record is captured once at work-item pickup, so a change mid-iteration
  does not move the goalposts for an item already running.
- `AgentConcurrency` — re-applied via `AgentConfigHotReload`. Reload propagates
  to `OrchestratorService` (dispatch gate) and `PipelineRunner` (rebase-resolver
  cap-aware routing) through a shared snapshot. `MaxConcurrent` values **must
  be >= 1**; `<= 0` is rejected (the prior view is retained on hot-reload).
  To leave an agent uncapped, **omit** the entry — do NOT set `MaxConcurrent: 0`.
  Keys may be either bare agent kinds (`claude`) or instance route keys
  (`claude/acct-a`) when `AgentClasses` uses multiple credentials for the same
  kind. Route-key caps are applied before bare-kind fallback caps.
  To stop dispatch to an agent, remove it from `AgentClasses[*].Members` or
  pause the queue. The resolved caps are logged at orchestrator startup and on
  every successful hot-reload so the effective value is visible to operators.
- `AgentClasses` + `AgentInstances` + `AgentScoreModifiers` — re-applied via
  `AgentConfigHotReload` to the live `AgentClassRouter` catalog. In-flight
  routing calls finish against the snapshot they started with.
- `QuotaRouter` gate fields — re-applied via `AgentConfigHotReload` to the
  shared `QuotaRouterOptions` object read by the live router. This includes
  quota floors, unknown-policy handling, observed-failure windows, cap retry
  delay, cold-start fit, and `IntraKindRoutingPolicy`. Probe cache TTL remains
  startup-captured; see the not-hot-reloadable list below.
- `AgentBurnEstimator` — re-applied via `AgentConfigHotReload` to the live
  burn-estimator (per-window token budgets, default burn percentages). Agents
  with samples but no positive `WindowTokenBudget` fail open until a budget is
  configured.
- `AgentPricing` — re-applied via `AgentConfigHotReload` to the live
  `AgentCostCalculator`. Negative-rate validation runs on the reload candidate
  before the swap; rejected reloads keep the prior pricing.
- `QuotaRouter` decision fields — re-applied via `AgentConfigHotReload` to the
  live `QuotaRouterOptions` singleton. This includes global floors,
  `MinQuotaPctByWindow`, `FloorByAgent`, per-agent ramp windows, unknown-policy
  handling, observed-failure windows, cap-retry cadence, and cold-start
  fit-in-window. `QuotaCacheTtlSeconds` is still sampled by quota probe
  constructors at startup.
- `DeadWorker.MaxRecoveryAttempts` and `DeadWorker.DeadWorkerThreshold` —
  re-read on every reaper sweep.
- `WorkerProgressWatchdog.ProgressTimeout`,
  `WorkerProgressWatchdog.AutoRecover`,
  `WorkerProgressWatchdog.MaxRecoveryAttempts`,
  `WorkerProgressWatchdog.PostAgentTransitionTimeout`,
  `WorkerProgressWatchdog.ProcessCpuProgressSignalEnabled`, and
  `WorkerProgressWatchdog.ActiveSandboxProgressSignalEnabled` — re-read on
  every watchdog sweep. `WorkerProgressWatchdog.CheckInterval` is sampled once
  at startup.
- `Shutdown.SandboxResumeMode`, `Shutdown.SandboxResumeTimeout`, and
  `Shutdown.SandboxAdoptionDeadlineSeconds` — re-read by the startup resume
  service. In the default background mode, the API listener is not held offline
  while suspended sandboxes resume.
- `MultipassExtraRuncmd` / `MultipassExtraCloudInit` / `SandboxNetworkProfiles` /
  `MultipassUseBaselineImages` / `MultipassSandbox.CloudInitReadyRetryAttempts` /
  `MultipassSandbox.VmStartTimeout` / `MultipassSandbox.VmStopTimeout`
  — re-read on every sandbox launch. VMs already running keep the snapshot they
  booted with.
- `SandboxLeak.LeakAgeThreshold` / `PreemptRetention` / `AutoDispose` /
  `MaxConcurrentAutoDispose` — re-read on every reaper sweep. `Enabled` and
  `CheckInterval` are sampled once at startup (PeriodicTimer cadence is fixed).
- `AuditLog.RetainedDays` (database retention) — re-read on every daily
  `AuditReportRetentionService` sweep. The Serilog rolling-file sink pins
  retention at startup though, so log-file retention continues to require a
  restart.
- `Shutdown.SandboxTeardownMode` — re-read when graceful shutdown teardown
  begins. Operators can switch between `Stop`, `Suspend`, and `Dispose` before
  stopping the process, and that shutdown uses the updated mode.
- `Smoke.Enabled` — hot-reloaded through `SmokeOptionsSnapshot`; disables the
  pickup credential gate, router smoke exclusions, and in-VM smoke gate.
- `PromptPreprocessing.ProjectRulesPath` — re-read before every agent
  invocation; changes affect the next work/rework/audit/merge/check-and-act
  prompt.

Not hot-reloadable (rejected by `IValidateOptions<CodeyBoxOptions>` if changed):

- `SandboxProvider` — switching from `multipass` to `bubblewrap` mid-flight
  would orphan running sandboxes.
- `StateDatabasePath`, `GitRootDirectory`, `AgentStreams.Path` — captured by
  open file/SQLite handles at startup.

Not hot-reloadable (consumer captures the value at construction; restart required):

- `WebhookEventBus.RingBufferCapacity` — sized into the in-memory ring buffer.
- `Webhooks[*]` — `HttpWebhookDispatcher` builds its endpoint set at startup;
  rebuilding the dispatcher mid-flight would drop pending retries.
- `Changelog.*` — `ClaudeChangelogGenerator` snapshots its config at construction.
- `AuditLog.Path` / `AuditLog.AuditPath` / `AuditLog.MaxFileSizeBytes` — bound
  into Serilog rolling-file sinks at startup.
- `AgentStreams.*` (besides `Path` which is also rejected) — bound into the
  `AgentStreamStore` singleton at startup.
- `AgentStreamAnalysis.*` — bound into the `AgentStreamParserOptions` singleton
  at startup.
- `SandboxImageReference`, `AgentAllowedHosts`, `AuditToolAllowedHosts`,
  `UpstreamPushMaxAttempts`, `UpstreamPushBackoffSeconds`, `Shutdown.GraceSeconds`,
  `PhaseAbsoluteTimeoutMultiplier` — bound into
  startup services and consumed by `PipelineRunner` / `ReleaseService` /
  shutdown-service constructors.
- `WorkerPool.*`, `Concurrency`, `AutoRetryOnQuotaFailure.*` — sized into
  `OrchestratorOptions` and the worker-pool plumbing at startup.
- `QuotaRouter.QuotaCacheTtlSeconds` — captured by the per-provider quota probes
  at construction (probe caches are sized once). Other router gate fields are
  hot-reloaded.
- `Smoke.CacheTtlMinutes` / `Smoke.Availability.*` — cache and availability
  registry singletons are sized at startup.
- `BudgetAlerts.CheckInterval` — sized into the `BudgetAlertService`'s
  `PeriodicTimer` at startup.
- `SandboxLeak.Enabled` / `SandboxLeak.CheckInterval` — see above.
- `Otel.*` — OpenTelemetry pipelines are wired at startup.
- `Presets.*` — `PresetCatalog` is bound from configuration at startup.
- `ConfigValidation.*` — only used by the startup `AgentClassConfigValidator`.

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
| `TemplateDirectory` | string | `templates` | Directory containing task-template JSON files. Relative paths resolve under the API content root. Files are discovered and validated on demand. |
| `SandboxImageReference` | string | `codeybox/agent:latest` | OCI image reference for agent sandboxes. |
| `AgentAllowedHosts` | string[] | `["api.anthropic.com","api.openai.com","api.githubcopilot.com","generativelanguage.googleapis.com"]` | Egress allowlist inside agent sandboxes. Add third-party hosts only for deployments that intentionally route work to agents that require them. |
| `AuditToolAllowedHosts` | string[] | public package/vulnerability registries | Egress allowlist for network-capable tool auditors such as `deps-cve-scan`; keep this separate from agent API hosts. |
| `BuildScriptAudit.TimeoutSeconds` | int | `1800` | Hot-reloadable per-run timeout for the credential-free `process:build-script` auditor that executes repo-root `./build.sh`. |
| `SandboxProvider` | string | — | One of `multipass`, `bubblewrap`, `process`. Required in non-Development environments. |
| `SandboxNetworkProfiles.graphical` | string | `cb-graphical` | Conventional bridge mapping for projects that explicitly select the `graphical` network profile; create it with `scripts/setup-host-networks.sh`. |
| `MultipassSandbox.CloudInitReadyRetryAttempts` | int | `3` | Number of `cloud-init status --wait` attempts before probing VM readiness when cloud-init returns exit 1. |
| `MultipassSandbox.VmStartTimeout` | TimeSpan | `00:03:00` | Deadline for the post-launch poll that waits for the VM to reach `Running`. Bump on hosts that observe boot contention under concurrent launches. |
| `MultipassSandbox.VmStopTimeout` | TimeSpan | `00:02:00` | Deadline for the post-stop poll that waits for the VM to reach `Stopped`. |
| `CredentialFileWatchers` | bool | `true` | Enables host-side OAuth credential file watchers. Set false only in constrained test hosts; credential reads still use a stat-based freshness check on each access. |
| `DangerouslyAllowProcessSandbox` | bool | `false` | Allow process sandbox outside Development. Do not use in production. |
| `UpstreamPushMaxAttempts` | int | `5` | Retry count for upstream push (GitHub PR creation). |
| `UpstreamPushBackoffSeconds` | int | `15` | Seconds between upstream push retries. |
| `Shutdown.SandboxTeardownMode` | enum | `Stop` | Graceful-shutdown sandbox teardown mode: `Stop` cleanly stops and preserves the VM without a RAM snapshot; `Suspend` preserves RAM state via `multipass suspend` and is opt-in; `Dispose` purges the VM. |
| `PhaseAbsoluteTimeoutMultiplier` | number | `3.0` | Multiplier applied to a phase's per-attempt timeout to bound fallback chains. Work/rework attempts each get the full `WorkTimeout`; merge attempts each get the full `MergeTimeout`; the whole fallback chain is capped at this multiplier times that per-attempt timeout. |

---

## `WorkerPool`

Controls worker concurrency and spawn pacing.

```json
"WorkerPool": {
  "MaxConcurrentWorkers": 2,
  "MaxConcurrentSandboxes": 3,
  "MinSpawnIntervalMs": 0
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `MaxConcurrentWorkers` | `1` | Hard cap on simultaneously active pipelines. |
| `MaxConcurrentSandboxes` | `ceil(MaxConcurrentWorkers * 1.5)` | Global cap on concurrently live sandboxes/VMs across work, audit, merge, smoke, and verifier phases. Every `ISandboxProvider.CreateAsync` path shares this budget. |
| `MinSpawnIntervalMs` | `0` | Minimum milliseconds between successive worker spawns. |

`MaxConcurrentWorkers` limits concurrent work items. It does not include
additional sandboxes created inside an item for audit, merge/rebase, security
review, smoke, or required-build verification. `MaxConcurrentSandboxes` is the
host-capacity ceiling underneath those per-phase policies, so a burst of LLM
auditors from several items queues at sandbox creation instead of multiplying
into unbounded VMs. The value is captured at startup; restart CodeyBox to resize
the live admission gate.

---

## `PipelineTuning`

Hot-reloadable retry and recovery bounds used by pipeline execution.

```json
"PipelineTuning": {
  "AgentSuspendMaxRetries": 1,
  "AgentSessionResumeMaxAttempts": 2
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `AgentSuspendMaxRetries` | `1` | Legacy same-command retry count after suspend-related transient exits. |
| `AgentSessionResumeMaxAttempts` | `2` | CLI-native same-session resume attempts after a transient non-zero agent crash with a captured session id and a live sandbox including `/repo`. Set to `0` to disable session resume. |

---

## `WorkerPoolHealthWatchdog`

Detects a dispatcher/pool stall where worker slots are free, dependency-ready
work exists, an eligible agent is available, and no new worker has spawned for
the configured window. Settings are read on each sweep, so edits hot-reload.

```json
"WorkerPoolHealthWatchdog": {
  "StallTimeout": "00:10:00",
  "CheckInterval": "00:01:00",
  "MaxRecoveryAttempts": 2
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `StallTimeout` | `00:10:00` | Under-filled runnable-pool window before a critical alert and recovery attempt. Set `00:00:00` to disable. |
| `CheckInterval` | `00:01:00` | Sweep cadence. |
| `MaxRecoveryAttempts` | `2` | Bounded self-recovery attempts before `worker_pool.restart_required`. |
| `MaxRecoveryEnqueueBatchSize` | `32` | Max runnable work IDs re-kicked per recovery attempt. |
| `RecoveryVerificationDelay` | `00:00:05` | Delay before checking whether recovery cleared the stall. |

---

## `WorkerProgressWatchdog`

Detects a bound worker that has stopped making lifecycle progress while its
heartbeat may still be fresh. Heartbeat alone is ignored. The watchdog recycles
only when the work item timestamp, agent stream timestamp, item-owned process
CPU signal, and active sandbox ownership signal are all stale for
`ProgressTimeout`.

```json
"WorkerProgressWatchdog": {
  "ProgressTimeout": "01:00:00",
  "CheckInterval": "00:01:00",
  "AutoRecover": true,
  "ProcessCpuProgressSignalEnabled": true,
  "ActiveSandboxProgressSignalEnabled": true,
  "PostAgentTransitionTimeout": "00:10:00",
  "MaxRecoveryAttempts": 10
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `ProgressTimeout` | `01:00:00` | Window without item, stream, process CPU, or active sandbox progress before a worker is considered wedged. Set `00:00:00` to disable the watchdog. |
| `CheckInterval` | `00:01:00` | Sweep cadence. Sampled at startup; restart to change. |
| `AutoRecover` | `true` | Recycle the worker and requeue the item from the nearest recoverable state. When false, park the item at `NeedsOperatorInput`. |
| `ProcessCpuProgressSignalEnabled` | `true` | Count item-owned host processes whose CPU ticks advance between observations as progress. Sandbox providers derive `CODEYBOX_WORK_ITEM_ID` from `TimingWorkItemId` so the probe is scoped to the work item. |
| `ActiveSandboxProgressSignalEnabled` | `true` | Count provider-tracked active sandbox ownership as progress. This covers VM-backed providers whose guest CPU is not visible from host `/proc`; providers should omit sandboxes no longer actively owned by a work item. |
| `PostAgentTransitionTimeout` | `00:10:00` | Bound the post-agent commit, push, and state-transition step. |
| `MaxRecoveryAttempts` | `10` | Bounded automatic recoveries before transitioning the item to `Failed`; `0` means unlimited. |

---

## `Shutdown`

Controls graceful shutdown drains and the startup resume path for sandboxes
that were suspended by the previous process.

```json
"Shutdown": {
  "GraceSeconds": 60,
  "SandboxResumeMode": "Background",
  "SandboxResumeTimeout": "00:10:00",
  "SandboxAdoptionDeadlineSeconds": 1800,
  "SandboxTeardownMode": "Stop"
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `GraceSeconds` | `60` | Host shutdown drain for in-flight phases. |
| `SandboxResumeMode` | `Background` | `Background` starts the HTTP listener before resuming suspended sandboxes. `Blocking` preserves startup-blocking behavior but still applies `SandboxResumeTimeout` per VM. |
| `SandboxResumeTimeout` | `00:10:00` | Caller-side cap for each persisted VM resume call. On timeout, suspend bookkeeping is cleared and normal recovery/leak handling proceeds. |
| `SandboxAdoptionDeadlineSeconds` | `1800` | Max wait for an adopted in-VM agent to finish after its VM resumes. |
| `SandboxTeardownMode` | `Stop` | `Stop`, `Suspend`, or `Dispose` for in-flight worker sandboxes during graceful shutdown. `Suspend` is opt-in because it writes a RAM snapshot. |

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
      { "Agent": "claude", "Billing": "Subscription", "ModelId": "claude-opus-4-7", "QualityScore": 100, "Capabilities": ["sensitive"] },
      { "Agent": "codex",  "Billing": "Subscription", "ModelId": "gpt-5.5",         "QualityScore": 100, "Capabilities": ["sensitive"] },
      { "Agent": "gemini", "Billing": "Subscription", "ModelId": "gemini-3-flash-preview", "QualityScore": 95, "ReasoningMode": "high" },
      { "Agent": "claude", "Billing": "PayPerApi",    "ModelId": "claude-opus-4-7", "QualityScore": 100, "Capabilities": ["sensitive"] }
    ]
  }
]
```

Validation at startup: unique `Id`s, non-empty `Members`, valid `Billing`
values, `QualityScore` present and in 0–200, Gemini members with
`QualityScore ≥ 90` must have `ReasoningMode="high"`. A class with only
`Subscription` members emits a warning. `Capabilities` is optional; the
builder de-dupes case-insensitively and trims whitespace.

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
  "StartFloorPct": 25,
  "EndFloorPct": 3,
  "RampWindowSeconds": 604800,
  "FloorByAgent": {
    "codex": {
      "StartFloorPct": 1,
      "EndFloorPct": 0,
      "MinQuotaPct": 1
    }
  },
  "QuotaRecheckIntervalSeconds": 300,
  "QuotaCacheTtlSeconds": 60,
  "UnknownPolicy": "UseObservedFailures",
  "IntraKindRoutingPolicy": "MostQuotaFirst",
  "ObservedFailureWindowMinutes": 10,
  "ObservedFailureRetentionMinutes": 30,
  "ProbeMaxRetries": 2,
  "ProbeRetryInitialDelayMs": 250,
  "ProbeMaxConsecutiveFailures": 3,
  "ProbeMaxStalenessSeconds": 300
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `MinQuotaPct` | `10` | Global fallback minimum available-quota percentage when a ramp cannot be computed. |
| `StartFloorPct` | `25` | Global early-window ramp floor just after a quota reset. |
| `EndFloorPct` | `3` | Global late-window ramp floor as reset approaches. |
| `RampWindowSeconds` | `604800` | Global quota-window length used for the ramp calculation. |
| `FloorByAgent` | `{}` | Optional per-agent overrides keyed by agent kind, e.g. `codex` or `claude`. Each entry may set `StartFloorPct`, `EndFloorPct`, `MinQuotaPct`, and `RampWindowSeconds`; omitted fields inherit global values, and omitted agents use the global ramp. |
| `QuotaRecheckIntervalSeconds` | `300` | Seconds to wait before re-probing when all Subscription members are exhausted. |
| `QuotaCacheTtlSeconds` | `60` | Seconds to cache a quota probe result (per probe instance). |
| `UnknownPolicy` | `UseObservedFailures` | How to treat unknown probe snapshots: `UseObservedFailures`, `FailCautious`, or opt-in `FailOpen`. |
| `IntraKindRoutingPolicy` | `MostQuotaFirst` | Same-kind instance ordering policy: `MostQuotaFirst` maximizes runway, `RoundRobin` spreads wear evenly, and `Sticky` keeps a work item on its existing instance when usable. Hot-reloadable. |
| `ObservedFailureWindowMinutes` | `10` | Minutes a recent quota-shaped failure blocks the same agent/model across all projects. |
| `ObservedFailureRetentionMinutes` | `30` | Minutes observed quota failures remain in `state.db`. |
| `ProbeMaxRetries` | `2` | Additional retries on a transient probe failure (network error / timeout / 5xx) before recording the failure. Hot-reloadable; currently honoured by the Claude probe. |
| `ProbeRetryInitialDelayMs` | `250` | Base retry backoff in milliseconds; doubles each attempt. Hot-reloadable. |
| `ProbeMaxConsecutiveFailures` | `3` | Consecutive probe failures tolerated before the probe stops returning the retained last-known-good snapshot. A single transient blip cannot silently disable the `MinQuotaPct` floor. Hot-reloadable. |
| `ProbeMaxStalenessSeconds` | `300` | Maximum age of a retained last-known-good snapshot before it is dropped in favour of `AvailablePct=-1` (unknown). Hot-reloadable. |

Use `FloorByAgent` to keep reserve on the operator's oversight model while
burning work-only agents close to empty. For example,
`FloorByAgent:codex:{StartFloorPct:1,EndFloorPct:0,MinQuotaPct:1}` lets codex
dispatch at about 1% quota, while claude remains on the global 25% to 3% ramp
if omitted. When an agent override sets `MinQuotaPct`, that value also replaces
the global per-window fallback floor for that agent.

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
| `PeriodicCheckInterval` | `00:05:00` (5 min) | Safety-net sweep cadence: every interval the scheduler re-checks every Failed quota item against the quota gate. Catches items whose probe didn't expose a reset timestamp, and re-arms after restarts where a targeted timer was lost. The sweep ignores `NextQuotaRetryAt` and asks the router directly — the targeted timer is just an optimisation. On startup, overdue `NextQuotaRetryAt` rows are retried immediately as well as being re-armed with a zero-delay targeted timer. |
| `ClockDriftSafetyMargin` | `00:02:00` (2 min) | Padding added to the parsed `QuotaResetAt` before firing the targeted retry, to absorb clock drift between this orchestrator and the upstream provider. |
| `MaxAutoRetriesPerWorkItem` | `3` | Per-item lifetime cap on auto-retries. Prevents ping-pong if the failure was misclassified as quota. Manual retries do not count against this cap. |

Items paused at the project or global queue level are skipped — operators
pause queues for a reason. Each scheduler evaluation emits a
`quota_retry_attempted` audit-log event with `Source` and `Outcome`, including
no-op skips. Each successful auto-retry emits a `work_item.auto_retry` webhook
(see [docs/webhooks.md](webhooks.md#auto_retry-details)).

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

## `PromptPreprocessing`

Agent prompt preprocessing configuration. CodeyBox ships a built-in
preprocessor that prepends the project rules file to every agent prompt. This
is the reliable delivery path for house rules across Codex, Claude, Cursor,
opencode, and any future runner; root-level agent file discovery is a
compatibility aid, not the enforcement mechanism.

```json
"PromptPreprocessing": {
  "ProjectRulesPath": "AGENTS.md"
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `ProjectRulesPath` | `AGENTS.md` | Repo-relative rules file to prepend when present. Re-read before each agent invocation. Missing files leave the prompt unchanged. |

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
| `CODEYBOX_CREDENTIAL_FILE_WATCHERS` | Set to `false` to disable host-side OAuth credential file watchers while retaining stat-based reload on reads. |
| `ASPNETCORE_URLS` | Override the bind address (default `http://127.0.0.1:5000`). |
