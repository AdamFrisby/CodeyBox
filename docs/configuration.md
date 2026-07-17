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
- `DeepAuditFailurePersistence.{MaxAttempts,RetryDelay}` — captured as one
  validated snapshot when an unexpected deep-audit failure must be persisted.
  A change applies to the next failure reconciliation; an in-progress retry
  sequence keeps the bounds it started with.
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
- `PipelineTuning.AgentSessionResumeMaxAttempts` and
  `PipelineTuning.MaxRetainedAgentTurnSandboxes` — re-read at the next resume
  claim or retained-lease publication respectively. Existing claims and leases
  are not rewritten by a reload.
- `SqliteWriteGate.{AcquisitionTimeout,MaxHoldDuration,MaxQueuedWaiters,MaxConcurrentReadConnections}`
  — sampled on each subsequent SQLite gate/read-slot acquisition. Edits do not
  alter a holder already inside the gate. Values have defensive upper bounds:
  acquisition timeout <= 30 seconds, hold diagnostic threshold <= 5 minutes,
  queued waiters <= 4096, and concurrent read connections <= 128.
- `WorkerProgressWatchdog.ProgressTimeout`,
  `WorkerProgressWatchdog.AutoRecover`,
  `WorkerProgressWatchdog.MaxRecoveryAttempts`,
  `WorkerProgressWatchdog.PostAgentTransitionTimeout`,
  `WorkerProgressWatchdog.ItemStaleTimeout`,
  `WorkerProgressWatchdog.ItemStaleMaxRecoveryAttempts`,
  `WorkerProgressWatchdog.ProcessCpuProgressSignalEnabled`, and
  `WorkerProgressWatchdog.ActiveSandboxProgressSignalEnabled` — re-read on
  every watchdog sweep. `WorkerProgressWatchdog.CheckInterval` and
  `WorkerProgressWatchdog.ItemStaleCheckInterval` are sampled once at startup.
- `Shutdown.SandboxResumeMode`, `Shutdown.SandboxResumeTimeout`, and
  `Shutdown.SandboxAdoptionDeadlineSeconds` — re-read by the startup resume
  service. In the default background mode, the API listener is not held offline
  while suspended sandboxes resume.
- `MultipassExtraRuncmd` / `MultipassExtraCloudInit` / `SandboxNetworkProfiles` /
  `MultipassUseBaselineImages` / `MultipassSandbox.CloudInitReadyRetryAttempts` /
  `MultipassSandbox.VmStartTimeout` / `MultipassSandbox.VmStopTimeout`
  — re-read on every sandbox launch. VMs already running keep the snapshot they
  booted with.
- `Incus.*` except `ProjectName` and the effective `StagingDirectory`, plus
  `SandboxNetworkProfiles` — re-read for the next Incus provider operation. A
  sandbox keeps the immutable option snapshot captured when it was created, so
  changing the pool, guest identity, limits, or bridge map does not alter that
  sandbox halfway through its lifecycle.
- `SandboxProvider` between `multipass` and `incus` — when the process started
  with either VM provider, creations already in progress continue on the
  provider they selected and subsequent creation/baseline-provisioning
  operations route to the newly selected provider. Existing sandbox handles
  retain their owning provider. This is routing, not failover: a selected
  provider failure is propagated and is never retried through the other
  backend. Changes to or from any other provider remain restart-only and are
  rejected on reload. A durable recovery lease is routed to its exact recorded
  provider rather than the current ordinary-creation selection. If that
  selection changed across a process restart, keep the lease's provider in
  `SandboxProviderCutover:RetainedInventoryProviders` until its retained
  resources have been recovered or cleaned up.
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
- `TransitionHealth.{Enabled,WindowHours,MaxTransitions}` — hot-reloaded through
  `TransitionHealthOptionsSnapshot`; controls the `/fleet/transition-health`
  endpoint's rolling window and "last N transitions" cap.
  See [`transition-health.md`](transition-health.md).
- `PromptPreprocessing.ProjectRulesPath` — re-read before every agent
  invocation; changes affect the next work/rework/audit/merge/check-and-act
  prompt.

Not hot-reloadable (rejected by `IValidateOptions<CodeyBoxOptions>` if changed):

- `SandboxProvider` changes other than the guarded `multipass`↔`incus` switch
  described above. Other providers are constructed with different startup-only
  dependencies and therefore require a restart.
- `Incus.ProjectName` and the effective `Incus.StagingDirectory` — these define
  lifecycle inventory ownership and the persistent cleanup root. Reloading
  either could make existing instances or staged files invisible to leak reap.
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
| `SandboxImageReference` | string | `""` | Optional provider-specific sandbox image override. Empty (or the legacy sentinel `ignored`) uses the selected provider's configured default. For Incus, use a VM image alias/fingerprint; OCI-style references are only meaningful to providers that consume OCI images. |
| `AgentAllowedHosts` | string[] | `["api.anthropic.com","api.openai.com","api.githubcopilot.com","generativelanguage.googleapis.com"]` | Egress allowlist inside agent sandboxes. Add third-party hosts only for deployments that intentionally route work to agents that require them. |
| `AuditToolAllowedHosts` | string[] | public package/vulnerability registries | Egress allowlist for network-capable tool auditors such as `deps-cve-scan`; keep this separate from agent API hosts. |
| `BuildScriptAudit.TimeoutSeconds` | int | `1800` | Hot-reloadable per-run timeout for the credential-free `process:build-script` auditor that executes repo-root `./build.sh`. |
| `AuthFailurePatterns.<agent>[]` | object[] | `[]` | Extra runtime auth/login-prompt substrings for one agent kind. Each entry is `{ "pattern": "...", "stream": "stderr" }` by default; set `"stream": "stdout"` or `"stderrAndStdout"` only for tightly-formed CLI transcripts because stdout can contain model text. Built-in defaults already cover common OAuth/login prompts. |
| `SandboxProvider` | string | — | One of `incus`, `multipass`, `multipass-remote`, `sprites`, `bubblewrap`, or `process`. Required in non-Development environments. Only a process started with `multipass` or `incus` can hot-switch, and only between those two. |
| `SandboxNetworkProfiles.graphical` | string | `cb-graphical` | Conventional bridge mapping for projects that explicitly select the `graphical` network profile; create it with `scripts/setup-host-networks.sh`. |
| `MultipassSandbox.CloudInitReadyRetryAttempts` | int | `3` | Number of `cloud-init status --wait` attempts before probing VM readiness when cloud-init returns exit 1. |
| `MultipassSandbox.VmStartTimeout` | TimeSpan | `00:03:00` | Deadline for the post-launch poll that waits for the VM to reach `Running`. Bump on hosts that observe boot contention under concurrent launches. |
| `MultipassSandbox.VmStopTimeout` | TimeSpan | `00:02:00` | Deadline for the post-stop poll that waits for the VM to reach `Stopped`. |
| `CredentialFileWatchers` | bool | `true` | Enables host-side OAuth credential file watchers. Set false only in constrained test hosts; credential reads still use a stat-based freshness check on each access. |
| `DangerouslyAllowProcessSandbox` | bool | `false` | Allow process sandbox outside Development. Do not use in production. |
| `UpstreamPushMaxAttempts` | int | `5` | Retry count for upstream push (GitHub PR creation). |
| `UpstreamPushBackoffSeconds` | int | `15` | Seconds between upstream push retries. |
| `DeepAuditFailurePersistence.MaxAttempts` | int | `3` | Total attempts to persist an unexpected deep-audit failure, including the initial attempt. Must be between 1 and 10; hot-reloadable for the next reconciliation. |
| `DeepAuditFailurePersistence.RetryDelay` | TimeSpan | `00:00:00.100` | Delay between terminal-failure persistence attempts. Must be non-negative and no greater than one minute; hot-reloadable for the next reconciliation. |
| `Shutdown.SandboxTeardownMode` | enum | `Stop` | Graceful-shutdown sandbox teardown mode: `Stop` cleanly stops and preserves the VM without a RAM snapshot; `Suspend` preserves RAM state via `multipass suspend` and is opt-in; `Dispose` purges the VM. |
| `PhaseAbsoluteTimeoutMultiplier` | number | `3.0` | Multiplier applied to a phase's per-attempt timeout to bound fallback chains. Work/rework attempts each get the full `WorkTimeout`; merge attempts each get the full `MergeTimeout`; the whole fallback chain is capped at this multiplier times that per-attempt timeout. |

---

## `SandboxProviderCutover`

Provider-neutral lifecycle retention for a hot-reload sandbox-provider
cutover. Providers activated in the current process remain inventoried
automatically. When a cutover spans a restart, list any previous provider whose
preserved sandboxes or baselines still need inventory and cleanup:

```json
"SandboxProviderCutover": {
  "RetainedInventoryProviders": ["multipass"]
}
```

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `RetainedInventoryProviders` | string[] | `[]` | Provider IDs retained for lifecycle and baseline inventory after restart. Values must be unique members of the registered hot-reload provider set; currently `multipass` and `incus`. Remove an ID after that provider's resources are gone. Maximum 8 entries. |

---

## `Incus`

Operational settings for `SandboxProvider=incus`. Except for `ProjectName` and
the effective `StagingDirectory`, these settings are read for the next provider
operation. A process that started with `multipass` or `incus` can switch
between those two providers on hot reload; selecting any other
provider still requires a restart. Incus consumes the shared
`SandboxNetworkProfiles` map so both VM providers use the same host-enforced
bridge policy during cutover. All Incus provisioning and lifecycle settings are
independent: no `Multipass*` value is inherited.

Durable queued Incus baseline pins remain routable after a
`BaselineNamePrefix` edit or process restart. Cutover routing recognizes the
stable Incus baseline shape (`-headless-` or `-gui-` followed by 12 lowercase
hex characters) without treating that shape as proof of ownership; Incus still
requires exact instance metadata, profile, flavor, pool, and the `ready`
snapshot before using a pin.

```json
"Incus": {
  "BinaryPath": "incus",
  "ProjectName": "codeybox",
  "StoragePoolName": "codeybox-zfs",
  "DefaultImage": "images:ubuntu/24.04/cloud",
  "InstanceNamePrefix": "codeybox-",
  "BaselineNamePrefix": "cb-incus-baseline-",
  "UseBaselineImages": true,
  "InterruptedExecRecoveryRetryAttempts": 3,
  "InterruptedExecRecoveryRetryDelay": "00:00:01",
  "PackageCacheSeeds": [],
  "ExecutableProvisions": []
}
```

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `BinaryPath` | string | `incus` | Incus 6.3-or-newer CLI executable name or absolute path. The installed release's upstream requirements also apply: the recommended Incus 7.0 LTS requires Linux 6.12 or newer and QEMU 8.2 or newer. Independently, the provider requires Linux 5.6 or newer for restricted-path `openat2` confinement. |
| `ProjectName` | string | `codeybox` | Restart-only dedicated non-default project containing CodeyBox-owned instances. When absent, the provider creates it with exact ownership/schema markers `user.codeybox.managed=true` and `user.codeybox.project-schema=1`, `features.images=false`, the Incus-required `features.profiles=true`, and all required restrictions. Every VM is nevertheless created with `--no-profiles` and exact topology read-back. It refuses to adopt or mutate an existing project unless both markers and both feature flags match exactly. Disk paths, NIC/snapshot access, and `restricted.virtual-machines.lowlevel=block` are applied atomically and verified before VM creation; per-instance validation separately rejects nesting. |
| `StoragePoolName` | string | `codeybox-zfs` | Existing ZFS or Btrfs pool used for VM roots and COW clones. CodeyBox never creates or reformats it; ZFS is strongly recommended for VM workloads. |
| `DefaultImage` | string | `images:ubuntu/24.04/cloud` | VM image used when a sandbox specification has no explicit image. |
| `InstanceNamePrefix` | string | `codeybox-` | Prefix identifying provider-owned ordinary VM instances. |
| `BaselineNamePrefix` | string | `cb-incus-baseline-` | Prefix for content-addressed baked baseline instances. |
| `UseBaselineImages` | bool | `true` | Lazily bake baselines and create sandboxes with COW `incus copy` clones. Set false to use the full-launch path. |
| `ExtraRuncmd` | string[] | `[]` | Incus-specific first-boot/baseline provisioning commands. At most 256 commands are accepted; each command is limited to 64 KiB of UTF-8 and the aggregate to 1 MiB. Multipass provisioning settings are never inherited. |
| `PackageCacheSeeds` | object[] | `[]` | Incus-specific host package-cache files or directories copied while provisioning a baked baseline or a full-launch VM. Each entry has `HostSourcePath`, a normalized absolute non-root canonical guest destination directory in `VmDestPath`, and optional finite positive `MaxSizeMB` in MiB (1,048,576 bytes). Directory contents land beneath that destination; a file lands at `VmDestPath/<source basename>`. Guest aliases and paths under `/dev`, `/proc`, `/run`, or `/sys` are rejected. Maximum 32 entries. Multipass seeds are never inherited. |
| `ExecutableProvisions` | object[] | `[]` | Incus-specific host executables installed while provisioning a baked baseline or a full-launch VM. Each entry has `HostSourcePath`, normalized absolute non-root canonical guest `VmDestPath`, optional `VmSymlinks` (at most 32 normalized absolute canonical guest paths), and optional `Label`. Guest aliases and paths under `/dev`, `/proc`, `/run`, or `/sys` are rejected. Maximum 64 provisions. Multipass provisions are never inherited. |
| `ExtraCloudInit` | string or null | null | Incus-specific additional top-level cloud-init YAML sent through `user.user-data`. Multipass cloud-init settings are never inherited. Do not put secrets here. |
| `StagingDirectory` | string or null | `<StateDatabasePath directory>/incus-staging` | Restart-only persistent absolute host directory for isolation snapshots and mount staging. Its canonical, non-symlink parent must already exist; normally leave the root absent so CodeyBox creates it with mode `0700` and its ownership marker. An existing root is accepted only when owned by the service UID/GID with exact mode `0700` and an exact provider-owned `.codeybox-incus-staging-v1` marker (mode `0600`). Set an explicit path to place staging on a separate filesystem. The filesystem root, commas, and control characters are rejected because this path is included in the project's restricted disk-path list. |
| `AllowedHostMountRoots` | string[] | `[]` plus managed Git roots | Up to 64 additional canonical host-directory roots that may be attached directly through virtiofs. `GitRootDirectory` and the effective shared upstream mirror directory (when enabled) are included automatically, for at most 66 effective roots. The exact canonical roots plus staging form the dedicated project's nonempty `restricted.devices.disk.paths` value; filesystem root, commas, controls, and paths outside the bounded list are rejected. Mounts outside these roots fail closed; add only narrowly scoped trusted directories. |
| `GuestUserId` | uint | `1000` | Non-root guest user ID used for untrusted sandbox commands. Incus VM virtiofs does not shift IDs; when any host-backed mount is present, this must exactly match the CodeyBox process's effective host UID. |
| `GuestGroupId` | uint | `1000` | Non-root guest group ID used for untrusted sandbox commands. When any host-backed mount is present, this must exactly match the CodeyBox process's effective host GID. |
| `GuestHome` | string | `/home/ubuntu` | Normalized absolute home directory for the configured guest identity. |
| `OperationTimeout` | TimeSpan | `00:02:00` | General deadline for one Incus CLI lifecycle operation. |
| `ExecTimeout` | TimeSpan | `06:00:00` | Provider-side upper bound for one guest command. A sandbox wall-clock limit or caller cancellation can end it sooner. |
| `ImageProvisioningTimeout` | TimeSpan | `00:30:00` | Deadline applied to a cold Incus image/root initialization operation and, separately, to executable staging/install, verification, and package-cache seeding (including host input capture). This is deliberately longer than the general operation timeout. |
| `VmStartTimeout` | TimeSpan | `00:05:00` | Deadline for VM boot and guest-agent readiness. |
| `VmStopTimeout` | TimeSpan | `00:02:00` | Deadline for graceful VM stop. |
| `CloudInitTimeout` | TimeSpan | `00:05:00` | Deadline for cloud-init completion. |
| `MountReadyTimeout` | TimeSpan | `00:05:00` | Deadline for a configured virtiofs/tmpfs mount or the Incus-persistent `/work` root-disk directory to pass its guest readiness checks. |
| `ReadinessPollInterval` | TimeSpan | `00:00:01` | Delay between bounded readiness probes. |
| `MaxReadinessPollInterval` | TimeSpan | `00:00:05` | Upper bound for the guest-agent readiness poll interval. The wait starts at `ReadinessPollInterval` and backs off exponentially up to this cap, so a concurrent boot storm does not hammer incusd with a probe every `ReadinessPollInterval` across every booting VM. Must be positive and at least `ReadinessPollInterval`. |
| `ProvisioningRetryRecheckIn` | TimeSpan | `00:00:30` | Delay before the recovery stack re-attempts a sandbox creation that was deferred because an Incus liveness deadline (guest-agent readiness or a CLI operation) tripped under concurrent boot load. Kept short so the item retries soon after the boot storm clears rather than being parked. Must be positive. |
| `CliProcessCleanupTimeout` | TimeSpan | `00:00:05` | Independent deadline for terminating an Incus CLI process tree and draining its redirected streams after cancellation or failure; valid through 5 minutes. Read live for each CLI invocation. |
| `CliProcessGroupExitPollInterval` | TimeSpan | `00:00:00.010` | Delay between Linux process-group absence probes during Incus CLI cleanup; must be positive, at most 1 second, and no greater than `CliProcessCleanupTimeout`. Read live for each CLI invocation. |
| `ExecPidPollAttempts` | int | `5` | Attempts to obtain an active guest exec process-group ID before forced cleanup; valid range 1–100 and read live. |
| `ExecControlFileCleanupAttempts` | int | `3` | Attempts to delete and verify absence of each transient guest exec control file; valid range 1–100 and read live. |
| `ExecCompletionProbeAttempts` | int | `3` | Attempts to read and validate a guest exec completion sentinel; valid range 1–100 and read live. |
| `InterruptedExecRecoveryRetryAttempts` | int | `3` | Delayed follow-up attempts in one recovery window at the next exec boundary after the immediate interrupted-exec recovery attempt failed. Valid range 0–10 and read live. A later exec boundary may open another bounded window for the same pending run after infrastructure is repaired; `0` disables delayed follow-up windows without disabling the immediate attempt. |
| `InterruptedExecRecoveryRetryDelay` | TimeSpan | `00:00:01` | Cancellation-aware delay before each delayed interrupted-exec recovery attempt. Must be positive and no greater than 30 seconds; read live. |
| `MaxConcurrentOperations` | int | `2` | Concurrent heavy Incus lifecycle/device operations; valid range 1–64. |
| `MaxConcurrentBoots` | int | `2` | Maximum VM boots — an `incus start` plus its guest-agent readiness wait (`VmStartTimeout`) — allowed in flight at once; valid range 1–64. Booting many qemu VMs simultaneously starves the incus daemon/host so guest agents miss their readiness window and the start fails; staggering boots keeps each within its timeout. Independent of `MaxConcurrentOperations`, which bounds individual brief CLI invocations but not the boot-and-readiness window (the boot happens inside qemu, after the `incus start` call returns). Raise cautiously when scaling worker concurrency. |
| `BootLaunchDelay` | TimeSpan | `00:00:02` | Inter-boot stagger applied after a boot slot under `MaxConcurrentBoots` is acquired, spacing out qemu spin-up. Valid range zero (disables the delay) through five minutes. |
| `MaxCliStdoutBytes` | int | `4194304` | Maximum retained stdout from one Incus CLI invocation. |
| `MaxCliStderrBytes` | int | `4194304` | Maximum retained stderr from one Incus CLI invocation. |
| `CaptureResourceMetrics` | bool | `false` | Capture best-effort guest resource metrics before teardown. |
| `ResourceMetricsCaptureTimeout` | TimeSpan | `00:00:05` | Deadline for the best-effort metrics read during teardown. |
| `ResourceMetricsSampleInterval` | TimeSpan | `00:00:10` | Interval used by the guest peak-memory sampler. |
| `BaselineCpus` | int | `6` | Default vCPU allocation for baked baselines. |
| `BaselineMemoryBytes` | long | `17179869184` | Default baseline memory allocation (16 GiB). |
| `BaselineDiskBytes` | long | `8589934592` | Default baseline root disk size (8 GiB logical allocation, matching the default sandbox limit). |
| `MaxExecutableProvisionBytes` | long | `536870912` | Maximum size of one host-staged executable (512 MiB by default; valid range 1 byte–4 GiB). |
| `MaxAggregateExecutableProvisionBytes` | long | `1073741824` | Maximum aggregate size of executable provisions in one baseline or full-launch provisioning operation (1 GiB); must be at least the per-file limit and at most 64 GiB. |
| `MaxPackageCacheSeedBytes` | long | `4294967296` | Maximum bytes copied from one package-cache seed (4 GiB by default; valid through 1 TiB). An entry's `MaxSizeMB` may narrow this bound but cannot enlarge it. |
| `MaxAggregatePackageCacheSeedBytes` | long | `8589934592` | Maximum aggregate bytes copied across package-cache seeds in one baseline or full-launch provisioning operation (8 GiB); must be at least the per-seed limit and at most 4 TiB. |
| `MaxPackageCacheSeedEntries` | int | `100000` | Maximum filesystem entries (files, directories, and links) traversed while copying one package-cache seed; valid range 1–1000000. |
| `MaxSnapshotBytes` | long | `17179869184` | Maximum aggregate bytes copied into private staging across all `SnapshotForIsolation` and individual-file mounts in one sandbox. |
| `MaxSnapshotEntries` | int | `100000` | Maximum aggregate number of files, directories, and links copied into private staging for one sandbox. |
| `MaxReadinessProbeEntries` | int | `4096` | Maximum direct-mount entries inspected while selecting a bounded host/guest identity probe file. |
| `MaxTmpfsDeviceBytes` | long | `17179869184` | Maximum logical size of one memory-backed guest tmpfs mount (16 GiB). Incus maps the conventional `/work` request to its bounded VM root disk instead, so this setting still governs credentials and other real tmpfs mounts but not `/work`. |
| `MaxAggregateTmpfsBytes` | long | `34359738368` | Maximum aggregate logical size of all memory-backed guest tmpfs mounts in one sandbox (32 GiB), excluding Incus's root-disk-backed `/work`. |

See [Sandbox providers](sandbox-providers.md#incus--cow-vms-with-virtiofs) for
Incus installation, project, ZFS/Btrfs pool, and security prerequisites. Incus
also derives its pool and host-volume preflight from the shared
`CodeyBox:DiskGuard:Enabled`, `MinFreeBytes`, `RecheckIn`, and `AdditionalPaths`
settings. The effective Incus staging path is added automatically.
`CodeyBox:DiskGuard:MultipassDataPath` remains Multipass-only and is never read
by Incus.

If Incus cannot execute the commands required to create the ordinary durable
agent-turn checkpoint, it may retain the exact stopped VM and publish a
provider-bound internal lease. The token is never exposed by the public API;
Incus stores only its hash and an immutable manifest hash on the VM. A retry
must match the creation-time project, pool, guest identity, sandbox
specification, network/topology, inode-pinned host sources, and guest links.
The retry first converts the VM to the ordinary Git/private-state checkpoint
under an exclusive preparation claim, then deletes it and automatically queues
the immutable resumed dispatch. See [Recovery](recovery.md#retained-incus-adoption-and-conversion)
for the lifecycle and failure behavior.

When `UseBaselineImages=true`, the API derives Incus post-bake verification
commands from the provider-neutral `IInVmSmokeProbe` catalog and the configured
agent set. These commands are not operator-authored configuration: every
configured CLI-backed agent must have a credential-independent probe (or an
explicit no-CLI exemption), and the baseline is published only after those
commands pass. Disabling Incus baselines leaves this bake-only list empty.

| Shared disk-guard key | Default | Incus behavior |
|-----------------------|---------|----------------|
| `CodeyBox:DiskGuard:Enabled` | `true` | Enables/disables both the Incus pool-space probe and host-path probes. |
| `CodeyBox:DiskGuard:MinFreeBytes` | `10737418240` | Defers sandbox creation when the pool or any monitored host filesystem has less than 10 GiB free. |
| `CodeyBox:DiskGuard:RecheckIn` | `00:05:00` | Delay reported for deferred work. |
| `CodeyBox:DiskGuard:AdditionalPaths` | `[]` | Up to 64 extra host paths to probe; the state-database directory and effective Incus staging directory are included automatically, for at most 66 effective Incus host paths. |

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
  "AgentSessionResumeMaxAttempts": 2,
  "MaxRetainedAgentTurnSandboxes": 16,
  "EmptyReworkEscalationRetries": 1,
  "BlockRedundantDotnetBuildTestInAuditSandbox": true
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `AgentSuspendMaxRetries` | `1` | Legacy same-command retry count for unknown failures with suspend-related exit codes. Classified transient-network failures use the durable scheduler instead. |
| `AgentSessionResumeMaxAttempts` | `2` | Bound used independently for CLI-native resumes in a live sandbox and atomically claimed durable agent-turn re-dispatches. An exact route receives its host-private SQLite scratchpad even without a native id; Claude/Codex reuse the exact validated id when captured. Set to `0` to disable these resume paths. `AgentSuspendMaxRetries`, sandbox adoption, and legacy Git-only preempt records remain separate. |
| `MaxRetainedAgentTurnSandboxes` | `16` | Global database-enforced cap on Incus VMs retained because infrastructure prevented creation of the normal immutable agent-turn checkpoint. Valid range 1–256 and read at lease publication. The compare-and-set publication and cap check are one SQLite statement, so concurrent workers and processes cannot exceed the configured count. This is independent of the resumed-dispatch attempt limit. |
| `EmptyReworkEscalationRetries` | `1` | Extra rework dispatches after a genuine no-diff audit rework when audit history shows convergence. Set to `0` to park immediately for operator review. |
| `BlockRedundantDotnetBuildTestInAuditSandbox` | `true` | Prepends an audit-sandbox-only `dotnet` shim that immediately succeeds `dotnet build` and `dotnet test` with a notice because the deterministic build/test gate already ran. Other `dotnet` subcommands pass through unchanged; work, merge, and conflict-resolution sandboxes are unaffected. |
| `CSharpTestPassAuditorIdleTimeout` | unset | Test-runner-specific idle guard for the `csharp:test-pass` (dotnet test) auditor, applied in place of `AuditorIdleTimeout`. Sourced through `DotnetTestAuditor` (an `ITestRunnerAuditor`). Unset means the generic `AuditorIdleTimeout` applies. |
| `CSharpTestPassBlameHangTimeout` | unset | Per-test hang-dump timeout injected into the `csharp:test-pass` command as `--blame-hang --blame-hang-timeout`. Unset omits blame-hang, keeping the command byte-identical to the legacy path. |

Durable agent-turn scratchpad archives have a non-configurable 32 MiB safety
cap. They are stored as content-verified, host-private SQLite BLOBs and are
never included in the content-addressed Git checkpoint. Clearing checkpoint
metadata deletes the paired BLOBs, and startup reconciliation removes orphaned
or no-longer-referenced rows left by an interrupted capture.

The retained-sandbox cap applies only to the Incus fallback used when the VM
cannot execute the commands needed to publish that Git/private-state
checkpoint. A retained lease is provider-bound internal metadata, not an
additional retry attempt. Cancellation or any lifecycle transition that clears
the lease makes the VM eligible for the normal sandbox leak reaper.

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
  "MaxRecoveryAttempts": 10,
  "ItemStaleTimeout": "01:15:00",
  "ItemStaleCheckInterval": "00:05:00",
  "ItemStaleMaxRecoveryAttempts": 3
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `ProgressTimeout` | `01:00:00` | Window without item, stream, process CPU, or active sandbox progress before a worker is considered wedged. Set `00:00:00` to disable the watchdog. |
| `CheckInterval` | `00:01:00` | Sweep cadence. Sampled at startup; restart to change. |
| `AutoRecover` | `true` | Recycle the worker and requeue the item from the nearest recoverable state. When false, park the item at `NeedsOperatorInput`. |
| `ProcessCpuProgressSignalEnabled` | `true` | Count item-owned host processes whose CPU ticks advance between observations as progress. Sandbox providers derive `CODEYBOX_WORK_ITEM_ID` from `TimingWorkItemId` so the probe is scoped to the work item. |
| `ActiveSandboxProgressSignalEnabled` | `true` | Count provider-tracked active sandbox ownership as progress. This covers VM-backed providers whose guest CPU is not visible from host `/proc`; providers should omit sandboxes no longer actively owned by a work item. |
| `PostAgentTransitionTimeout` | `00:10:00` | Bound the post-agent commit, push, and state-transition step. The item-stale watchdog also uses this bound while waiting for a recovery-cancelled local owner of a claimed durable checkpoint to quiesce. |
| `MaxRecoveryAttempts` | `10` | Bounded automatic recoveries before transitioning the item to `AbandonedAfterRecoveryAttempts`; `0` means unlimited. |
| `ItemStaleTimeout` | `01:15:00` | Window in which an active item's `UpdatedAt` must advance before the item-centric watchdog considers it wedged. Set `00:00:00` to disable this detector. |
| `ItemStaleCheckInterval` | `00:05:00` | Item-centric stale sweep cadence. Sampled at startup; restart to change. |
| `ItemStaleMaxRecoveryAttempts` | `3` | Bounded item-stale recoveries before parking at `NeedsOperatorInput`; `0` means unlimited. |

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

`ClaudeSession.Enabled` may be set on an agent class or on an individual
member. For class-routed Claude work items, this class/member opt-in composes
with the global `CodeyBox:ClaudeSession:Enabled` switch and the per-project
`ClaudeSession.Enabled` flag; member settings override the class setting.

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
  "DrainAggressiveness": 1.0,
  "ExpectedResets": {
    "codex": {
      "Timestamps": ["2030-01-01T00:20:00Z"],
      "CadenceSeconds": 604800,
      "CadenceAnchor": "2030-01-01T00:20:00Z"
    }
  },
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
| `IntraKindRoutingPolicy` | `MostQuotaFirst` | Routing policy for eligible class members: `MostQuotaFirst` maximizes runway within same-kind ties, `RoundRobin` spreads wear, `Sticky` keeps a work item on its existing instance, and `DeadlineAwareDrain` orders quality-eligible members by quota headroom at risk before the nearest known or expected reset. Hot-reloadable. |
| `DrainAggressiveness` | `1.0` | Multiplier used only by `DeadlineAwareDrain`. Values above `1.0` run ahead of the even per-rate-window pace; invalid or non-positive values are treated as `1.0`. Hot-reloadable. |
| `ExpectedResets` | `{}` | Optional per-agent expected reset declarations, keyed by agent kind. Each entry may set explicit `Timestamps` and/or a recurring `CadenceSeconds` with `CadenceAnchor`; the policy paces to the sooner of the live probe reset and the next expected reset. Hot-reloadable. |
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

## `AutoRetryOnTransientFailure`

Automatic re-queue of Failed work items whose agent failure was classified as
a transient transport/network failure. This is distinct from quota retry:
quota still waits for quota reset, auth failures stay excluded, and normal
build/test/quality failures are not retried.

```json
"AutoRetryOnTransientFailure": {
  "Enabled": true,
  "PeriodicCheckInterval": "00:01:00",
  "BaseDelay": "00:00:30",
  "Multiplier": 2.0,
  "MaxDelay": "00:15:00",
  "MaxAutoRetriesPerWorkItem": 5,
  "MaxElapsedTime": "01:00:00",
  "JitterMode": "Full"
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `Enabled` | `true` | Master switch for durable transient-network retry. Manual retry is unaffected. |
| `PeriodicCheckInterval` | `00:01:00` (1 min) | Safety-net sweep cadence for missed timers and restart recovery. |
| `BaseDelay` | `00:00:30` (30 s) | First retry delay before jitter. |
| `Multiplier` | `2.0` | Exponential backoff multiplier applied per retry attempt. |
| `MaxDelay` | `00:15:00` (15 min) | Per-attempt backoff cap before jitter. |
| `MaxAutoRetriesPerWorkItem` | `5` | Per-item cap. After this, the item remains `Failed` with `FailureKind="transient-exhausted"`. |
| `MaxElapsedTime` | `01:00:00` (1 h) | Total elapsed cap for one transient retry series. A retry that would fire after this window is not scheduled. |
| `JitterMode` | `Full` | `None`, `Full`, or `Decorrelated`. Use jitter to spread retries during provider or ISP incidents. |

`TransientRetryScheduler` persists `FailureKind="transient"`,
`NextTransientRetryAt`, `TransientRetryAttempts`, and
`TransientRetryFirstFailedAt` on the work item. When the timer fires, it calls
the shared `WorkItemRetrier` with auto-pick enabled, so items with prior work
commits resume at audit instead of discarding the work branch. Quota retry
remains owned by `QuotaRetryScheduler`; `IWorkItemAutoRetryScheduler` is only a
small notification facade that delegates to the two policy schedulers.

`CodeyBox:TransientNetworkFailurePatterns` appends extra classifier substrings
without a rebuild. Built-in patterns deliberately avoid bare `timeout` so
genuine build/test timeouts are not misclassified as retryable transport
incidents. A parsed stream-json `turn.failed` event whose `error.message` is
exactly `timeout` is the exception because that is provider transport metadata,
not free-form build output.

---

## `ConfigValidation`

Optional startup cross-check that every `AgentClass` member's `ModelId`
actually exists on the provider's live model list. Catches typos
(`gemini-3-flash-preview` vs. `gemini-3.1-flash-lite`) that would otherwise
silently misroute and only surface hours later as cascading quota failures.

```json
"ConfigValidation": {
  "FailOnUnknownModel": false,
  "UnboundKeys": {
    "Enabled": true,
    "Mode": "strict",
    "AdditionalExemptPaths": []
  }
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `FailOnUnknownModel` | `false` | When `false`, an unknown `ModelId` logs a Warning naming the typoed id and the valid alternatives, but startup succeeds. When `true`, startup fails-fast with a non-zero exit code so the misconfig never reaches production. Operators flip this on in production deployments. |
| `UnboundKeys.Enabled` | `true` | Master switch for the unbound-key startup check. See below. |
| `UnboundKeys.Mode` | `"strict"` | `"strict"` throws at startup when any unbound key is found. `"warn"` logs a single Warning naming each unbound key and lets startup proceed. Any other value is treated as `"strict"`. |
| `UnboundKeys.AdditionalExemptPaths` | `[]` | Full configuration paths under `CodeyBox:*` whose subtrees the inspector skips entirely (exact, case-insensitive match — not a prefix). Use for operator extension namespaces bound outside `CodeyBoxOptions` / `ProjectsOptions`. |

The model validator probes each provider's model-list endpoint
(`api.anthropic.com/v1/models`, ChatGPT-OAuth `chatgpt.com/backend-api/wham/models`
or `api.openai.com/v1/models`, and Gemini's quota-bucket / `v1beta/models`
endpoints) and matches each declared `ModelId` against the response. The
total validation budget is 10 seconds; an unreachable endpoint or a slow
network falls through to a Warning that names the agent kind and the
reason, and the host still comes up. Members without a `ModelId` are not
validated — they fall through to the agent's own default.

### Unbound CodeyBox configuration keys

`.NET`'s configuration binder silently drops any key that does not match a
property on the bound options class. A misspelled, renamed, or stale key
under `CodeyBox:*` is a no-op the operator never notices — the typed
property keeps its default while the operator believes they reconfigured
it. The unbound-key inspector walks the operator-provided `CodeyBox:*`
section at startup and surfaces every key that fails to bind to a property
on `CodeyBoxOptions` or `ProjectsOptions`.

Canonical case: `CodeyBox:AgentStreams:RootDirectory` looks like a sensible
configuration knob, but the bound property is `Path`. Before this check,
the value silently disappeared into the binder; now startup fails with:

```
Unbound CodeyBox configuration keys detected (no matching CodeyBoxOptions / ProjectsOptions property). …
  CodeyBox:AgentStreams:RootDirectory — no matching option
```

Recursion respects the option-graph shape:

- **POCO properties** — each child key must match a public property
  (case-insensitive). The
  [`ConfigurationKeyName`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.configuration.configurationkeynameattribute)
  attribute is honoured, so a property whose JSON key is aliased binds
  under its alias.
- **`Dictionary<,>` values** — dictionary keys are operator-defined and
  skipped. The inspector still recurses into the value type, so a typo
  inside (e.g.) `CodeyBox:AgentNetworkTolerance:codex:NotARealField`
  is still flagged.
- **Lists / arrays / enumerables** — the configuration provider keys list
  elements by numeric index; the index itself is not validated, but the
  element's property graph is.
- **Leaf types** (primitives, strings, enums, `TimeSpan`, `DateTimeOffset`,
  `Guid`, `Uri`) — any child of a leaf key is junk.

#### Default handling for separately-bound sections

A handful of `CodeyBox:*` sections bind to typed option classes outside
`CodeyBoxOptions` / `ProjectsOptions`. The inspector knows about these
roots and walks each sub-tree against its own typed POCO so typos like
`CodeyBox:BuildScriptAudit:TimoutSeconds` or
`CodeyBox:PromptPreprocessing:ProjectRulesPth` still surface — the wholesale
subtree skip is **not** applied:

| Section | Typed root |
|---------|------------|
| `CodeyBox:BuildScriptAudit` | `BuildScriptAuditorOptions` |
| `CodeyBox:PromptPreprocessing` | `AgentPromptPreprocessingOptions` |
| `CodeyBox:Presets` | `PresetCatalogOptions` |
| `CodeyBox:Mutation` | `MutationTestingAuditorOptions` |
| `CodeyBox:CheckAndActCompletion` | `CheckAndActCompletionOptions` |
| `CodeyBox:Plugins` | `PluginOptions` (plus operator-defined `<plugin-id>` sub-trees are opaque) |

`CodeyBox:Plugins` is the only section that mixes typed properties
(`AssemblyPaths`/`PackageDirectories`/`Allowlist`) with operator-defined
extension keys (per-plugin sub-trees read via `IPluginHost.ScopedConfig`).
The inspector treats any non-typed key at `CodeyBox:Plugins` level as an
opaque plugin id, so a typo of a typed property name there cannot be
distinguished from a plugin id and stays silent. Typos *inside*
`AssemblyPaths` / `PackageDirectories` / `Allowlist` are still validated.

A small set of leaf-shaped keys is read directly via `IConfiguration` with
no matching typed property; these are exempted by exact path:

- `CodeyBox:DangerouslyDisableAuth` — read directly via
  `IConfiguration.GetValue<bool>` for the bearer-token middleware.
- `CodeyBox:CredentialFileWatchers` — read directly to gate the host-side
  OAuth credential file watchers.
- Per-agent credential-file leaf keys read directly via
  `builder.Configuration["CodeyBox:…"]` when the matching `CODEYBOX_…_FILE`
  env var is unset: `CodeyBox:ClaudeOAuthFile`, `CodeyBox:CodexOAuthFile`,
  `CodeyBox:GeminiOAuthFile`, `CodeyBox:GeminiSettingsFile`,
  `CodeyBox:CursorAuthFile`, `CodeyBox:OpencodeAuthFile`,
  `CodeyBox:OpencodeAuthDestPath`, `CodeyBox:GeminiOauthClientId`,
  `CodeyBox:GeminiOauthClientSecret`.

The inspector also understands the two operator-keyed map shapes that
`ProjectsOptionsBinder.ApplyCustomMaps` reads:

- `Audit:Languages:Overrides:<lang-id>:…` — the per-language override map
  documented in [`docs/languages.md`](languages.md). Operator-defined
  language ids are accepted as dictionary keys; typos *inside* the
  override value (e.g. `Replce` instead of `Replace`) are still flagged.
- `Audit:AuditTypes:<id>:…` — the per-audit-type override map documented
  in [`docs/audit-types.md`](audit-types.md). The inspector detects the
  shape automatically (all-numeric keys → list form, any non-numeric key
  → map form) and walks the override POCO under each id so unknown
  sub-fields still surface.

These two exemptions cascade through `Defaults:Audit`, every
`Projects:<n>:Audit`, and every `…Audit:Profiles:<profile-id>:…` subtree
because the binder applies the same custom-map logic at every depth.

To exempt an operator extension namespace, add it to
`ConfigValidation.UnboundKeys.AdditionalExemptPaths`. The whole subtree
under the exact named path is skipped (case-insensitive equality — not a
prefix; an entry of `CodeyBox:MyExt` does not also match `CodeyBox:MyExtra`):

```json
"ConfigValidation": {
  "UnboundKeys": {
    "AdditionalExemptPaths": [
      "CodeyBox:MyCustomExtension"
    ]
  }
}
```

The validator runs as a hosted service at host start, so a strict-mode
failure surfaces before any pipeline component reads its options. Switch
to `"warn"` mode temporarily if you need to ship a config edit before the
matching code rename lands; flip back to `"strict"` once both are in.

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

## `Attachments`

Work-item attachment storage and lifecycle configuration. These values are
hot-reloadable; uploads, blob-root resolution, and cleanup sweeps read the
current `CodeyBox:Attachments` options at use time.

```json
"Attachments": {
  "RootDirectory": "~/.codeybox/attachments",
  "MaxFileSizeBytes": 104857600,
  "MaxAttachmentsPerWorkItem": 32,
  "MaxTotalBytesPerWorkItem": 536870912,
  "MaxCaptionChars": 2000,
  "MaxFileNameChars": 255,
  "MaxContentTypeChars": 255,
  "MultipartHeadersCountLimit": 256,
  "MultipartHeadersLengthLimitBytes": 8192,
  "MaxMultipartErrorMessageChars": 240,
  "DeliverToSandbox": true,
  "DeliverToPhases": [ "work", "rework", "audit" ],
  "TerminalCleanupTtl": "7.00:00:00",
  "CleanupSweepInterval": "01:00:00",
  "OrphanSweepInterval": "06:00:00",
  "OrphanGracePeriod": "00:10:00"
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `RootDirectory` | `~/.codeybox/attachments` | Host content-addressed blob root. |
| `MaxFileSizeBytes` | `104857600` | Per-file streaming cap (100 MiB). |
| `MaxAttachmentsPerWorkItem` | `32` | Max attachments per work item. |
| `MaxTotalBytesPerWorkItem` | `536870912` | Max total attachment bytes per work item (512 MiB). |
| `MaxCaptionChars` | `2000` | Max UTF-16 code units for one caption field. |
| `MaxFileNameChars` | `255` | Max UTF-16 code units for a sanitized display filename. |
| `MaxContentTypeChars` | `255` | Max characters for a stored multipart file `Content-Type`. |
| `MultipartHeadersCountLimit` | `256` | Max headers per multipart section. |
| `MultipartHeadersLengthLimitBytes` | `8192` | Max aggregate header bytes per multipart section. |
| `MaxMultipartErrorMessageChars` | `240` | Max parser-error text included in 400 responses. |
| `DeliverToSandbox` | `true` | When true, a work item's attachments are staged into its sandbox VM and announced to the agent (via the injected `## Attachments` manifest) for the phases in `DeliverToPhases`. When false, attachments stay host-only (upload/download API) and nothing is staged into any sandbox. |
| `DeliverToPhases` | `["work","rework","audit"]` | Agent prompt phases whose invocations stage attachments and inject the manifest. Compared case-insensitively; a phase not listed behaves as if the item had no attachments. |
| `TerminalCleanupTtl` | `7.00:00:00` | TTL cutoff for non-terminal stale attachment cleanup. Terminal work items are cleaned on the next sweep. |
| `CleanupSweepInterval` | `01:00:00` | Period between terminal/TTL cleanup sweeps. |
| `OrphanSweepInterval` | `06:00:00` | Period between orphan-blob sweeps. |
| `OrphanGracePeriod` | `00:10:00` | Grace window before unreferenced blobs/temp files are removed. |

---

## `PromptPreprocessing`

Agent prompt preprocessing configuration. CodeyBox ships built-in
preprocessors that run in order before every agent invocation:

1. **`ProjectRulesPromptPreprocessor`** — prepends the project rules file
   (`ProjectRulesPath`, default `AGENTS.md`). This is the reliable delivery
   path for house rules across Codex, Claude, Cursor, opencode, and any
   future runner; root-level agent file discovery is a compatibility aid,
   not the enforcement mechanism.
2. **`AttachmentManifestPromptPreprocessor`** — for the delivery phases
   configured in `Attachments:DeliverToPhases` (default work / rework / audit),
   stages a work item's attachment blobs into the sandbox (under
   `/work/.codeybox/attachments`, added to `.git/info/exclude` so a stray
   `git add -A` can never commit them) and prepends an `## Attachments` manifest
   listing each staged file's in-VM path, filename, content-type, size, and
   caption. Filenames and captions are fenced as an untrusted-data section. A
   no-op when `Attachments:DeliverToSandbox` is false, the phase is not a
   delivery phase, or the item has no attachments.
3. **`CrossAgentHandoffPromptPreprocessor`** — injects a `## Cross-agent
   handoff` brief whenever the current invocation runs under a different
   `AgentKind` than the most recent agent-involvement entry (the orchestrator
   spilled from one agent to another mid-work-item). The brief itself is
   built by `ICrossAgentHandoffBriefBuilder`; the preprocessor only detects
   the cross-agent transition and injects whatever text the builder returns.
   No-op until both `IAgentInvolvementStore` and the brief builder are wired.

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

## `AgentSupervision`

Config-gated live human supervision and injection for all active agent
invocations. See [docs/agent-supervision.md](agent-supervision.md) for the
SignalR protocol and injection semantics.

```json
"AgentSupervision": {
  "Enabled": false,
  "MaxPromptChars": 16384,
  "MaxOutputBufferChars": 131072,
  "MaxInjectionChars": 8192,
  "InjectionQueueCapacity": 16,
  "CompletedSessionRetentionSeconds": 300,
  "MaxSessions": 512
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `Enabled` | `false` | Master switch. When false, no supervision sessions or injection queues are opened. |
| `MaxPromptChars` | `16384` | Redacted prompt/turn text cap sent to clients. Must be >= 1024. |
| `MaxOutputBufferChars` | `131072` | Per-session redacted stdout tail kept for late joiners. Must be >= 4096. |
| `MaxInjectionChars` | `8192` | Maximum human instruction length. Must be >= 128. |
| `InjectionQueueCapacity` | `16` | Pending human instructions accepted per live session. |
| `CompletedSessionRetentionSeconds` | `300` | How long completed session snapshots remain listable. `0` prunes immediately. |
| `MaxSessions` | `512` | Maximum active/recent sessions tracked in memory. |

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
