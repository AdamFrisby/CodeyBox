# Audit logging

CodeyBox writes three rolling log files using [Serilog](https://serilog.net/):

| File | Contents |
|------|----------|
| `logs/codeybox-YYYYMMDD.json` | **All** structured events (Information and above). General operations log. |
| `logs/audit-YYYYMMDD.json` | **Audit-tier only** (`Audit=true` property). Security-relevant events for compliance review. |
| `logs/codeybox-console-YYYYMMDD.log` | **Plain-text mirror of the console / stdout stream.** Same content the API writes to stdout — what an external `>>` shell redirect used to capture, except the file is rotated and size-bounded so it can't grow without limit. |

Both paths are relative to the API process's working directory by default (typically the directory
you launch the binary from). Override via `appsettings.json` — see [Configuration](#configuration).

The structured file format is [Compact Log Event Format (CLEF / NDJSON)](https://clef-json.org/):
one JSON object per line. Every event carries the fields documented in
[Common properties](#common-properties). The plain-text console mirror uses Serilog's default
output template (timestamp, level, message, properties).

The console mirror exists so stdout survives without an external `>>` redirect:
it rolls by day and by size, so no single file grows past the point where `tail`
and `grep` return weeks-old lines at multi-gigabyte scan cost.

## Configuration

```json
"CodeyBox": {
  "AuditLog": {
    "Path":             "logs/codeybox-.json",
    "AuditPath":        "logs/audit-.json",
    "RetainedDays":     30,
    "MaxFileSizeBytes": 104857600,
    "ConsoleLog": {
      "Enabled":                true,
      "Path":                   "logs/codeybox-console-.log",
      "RetainedFileCountLimit": 14,
      "MaxFileSizeBytes":       104857600
    }
  }
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `Path` | `logs/codeybox-.json` | Path template for the main log. Serilog inserts the date before the trailing dot (e.g. `codeybox-20260429.json`). Relative paths resolve from the process working directory. |
| `AuditPath` | `logs/audit-.json` | Path template for the audit-tier log. Same date-insertion convention. |
| `RetainedDays` | 30 | Number of rolled JSON files to keep per JSON sink. Must be ≥ 1. |
| `MaxFileSizeBytes` | 104857600 (100 MiB) | Per-JSON-file size cap before rolling to a new file. |
| `ConsoleLog:Enabled` | `true` | Master switch for the rolling plain-text console mirror. Set `false` if your supervisor already captures stdout out of process — disabling this only stops writing the mirror file; stdout is unaffected. |
| `ConsoleLog:Path` | `logs/codeybox-console-.log` | Path template for the plain-text console mirror. Same date-insertion convention. |
| `ConsoleLog:RetainedFileCountLimit` | 14 | Total rolled console files kept across all dates and size segments. Counted-by-file (not by day) so the cap holds when size rolling produces multiple segments per day. Must be ≥ 1. |
| `ConsoleLog:MaxFileSizeBytes` | 104857600 (100 MiB) | Per-file size cap before rolling the console mirror. Must be ≥ 1 MiB. Combined with the daily boundary, this is what keeps individual files readable with `tail` / `less`. |

Startup fails fast if any enabled path's directory cannot be created or written to.

### Sizing the console mirror

Peak disk for the console mirror is `RetainedFileCountLimit × MaxFileSizeBytes`. The shipped
defaults (14 × 100 MiB) give ≈ 1.4 GiB peak retention — enough to cover a typical week of
verbose operator activity while staying bounded. Operators running a long inspection window
should raise `RetainedFileCountLimit` rather than `MaxFileSizeBytes` so individual files stay
quick to grep / tail.

### Migrating off the shell-redirect

The relaunch wrapper used to bound nothing: `… >> codeybox-orchestrator.run.log`. With this
configuration the API rotates the same content itself, so the wrapper redirect is **redundant**
and should be removed. If you must keep an out-of-process capture for transport / shipping
reasons, set `ConsoleLog:Enabled=false` to avoid duplicating the writes — but in either case
the unbounded single-file pattern is the thing that has to stop.

## Common properties

Every event (main and audit) carries:

| Property | Type | Description |
|----------|------|-------------|
| `@t` | ISO-8601 timestamp | Event time (UTC). |
| `@mt` | string | Message template. |
| `@l` | string | Level: `Information`, `Warning`, `Error`, `Fatal`. |
| `Application` | string | Always `"CodeyBox"`. |
| `MachineName` | string | Hostname of the API process. |
| `ThreadId` | int | Thread that emitted the event. |

Audit-tier events additionally carry:

| Property | Type | Description |
|----------|------|-------------|
| `Audit` | bool | Always `true`. Used by the audit-only sink to filter events. |
| `EventName` | string | Dot-separated event identifier (e.g. `agent.started`). |
| `WorkItemId` | string (GUID, N-format) | Present on all events emitted while a work item is being processed. |
| `ProjectId` | string | Present on all events emitted after the project is resolved. |

## Audit event taxonomy

The tables below carry the events operators alert on most, with their
properties. They are not the complete set — every name is listed in
[Every event name](#every-event-name).

### Work item lifecycle

| `EventName` | Level | Emitted by | Properties |
|-------------|-------|-----------|------------|
| `work_item.created` | Info | `WorkItemEndpoints.CreateAsync` | `WorkItemId`, `ProjectId`, `Title` |
| `work_item.picked_up` | Info | `OrchestratorService.RunWorkerAsync` | `WorkerId`, `WorkItemId` |
| `work_item.transitioned` | Info | `PipelineRunner.Transition` | `WorkItemId`, `State` (target state) |
| `work_item.cancelled` | Info | `PipelineRunner.RunAsync` (cancellation handler) | `WorkItemId` |
| `work_item.failed` | Warning | `PipelineRunner.TransitionFailed` | `WorkItemId`, `Error` |
| `work_item.transient_cancel_retried` | Warning | `PipelineRunner.HandleTransientCancellationAsync` | `WorkItemId`, `Phase`, `CancellationSource`, `Attempt`, `MaxAttempts` |

### Agent execution

| `EventName` | Level | Emitted by | Properties |
|-------------|-------|-----------|------------|
| `agent.started` | Info | `PipelineRunner.RunAgentPhaseAsync`, `RunAgentMergePhaseAsync` | `Agent`, `Sandbox`, `Phase` (`work`, `rework`, or `merge`) |
| `agent.finished` | Info | Same as above | `Agent`, `Sandbox`, `Success`, `ExitCode`, `DurationMs` |
| `agent.claude_unauthorized` | Warning | `ClaudeQuotaFailureDetector.EmitAdvisoryAuditEvents` (via `PipelineRunner`) | `Phase`, `SandboxName`. Logged when the Claude CLI returns HTTP 401. Treated as transient (no quota-breaker recording) — most commonly an expired access token. |
| `agent.claude_token_pushed_to_vm` | Info | `ClaudeTokenRotationPusher.PushToAllAsync` | `SandboxName`. Emitted once per active Claude-running sandbox after `~/.claude/.credentials.json` rotates on the host and the fresh sanitised bundle was written into the VM. Pair with the absence of subsequent `agent.claude_unauthorized` to confirm the in-VM refresh closed the gap. |
| `agent.claude_token_push_failed` | Warning | `ClaudeTokenRotationPusher.PushToSandboxAsync` | `SandboxName`, `Reason`. The exec into the VM failed; the running iteration is likely to 401 on its next Anthropic call. |

### Sandbox lifecycle

| `EventName` | Level | Emitted by | Properties |
|-------------|-------|-----------|------------|
| `sandbox.created` | Info | `MultipassSandboxProvider.CreateAsync` | `VmName`, `NetworkProfile` |
| `sandbox.provisioning_transient_retry` | Info | `MultipassSandboxProvider` | `WorkItemId`, `Operation`, `Attempt`, `ErrorClass` |
| `sandbox.agent_infra_failure` | Warning | `PipelineRunner` | `WorkItemId`, `Agent`, `Sandbox`, `Phase`, `Summary`, `Reason`. Missing agent binaries and runner prerequisite materialisation failures are sandbox/provisioning signals and do not increment the agent fast-fail breaker. |
| `sandbox.disposed` | Info | `MultipassSandbox.DisposeAsync` | `VmName` |

### Audit phase

| `EventName` | Level | Emitted by | Properties |
|-------------|-------|-----------|------------|
| `auditor.run` | Info | `PipelineRunner.CollectFindingsAsync` | `AuditorName`, `WorstSeverity` (`none`/`Info`/`Warning`/`Error`), `DurationMs` |
| `audit.iteration_complete` | Info | `PipelineRunner.RunAuditLoopAsync` | `Iteration`, `MaxIterations`, `BlockingCount`, `NonBlockingCount` |
| `audit.passed` | Info | Same | `Iteration` |
| `audit.failed` | Warning | Same | `Iteration`, `BlockingCount` |

### Quota retry

| `EventName` | Level | Emitted by | Properties |
|-------------|-------|-----------|------------|
| `quota_retry_attempted` | Info | `QuotaRetryScheduler.TryRetryAsync` | `WorkItemId`, `Source` (`periodic`, `targeted`, or `rearm-overdue`), `Outcome`, `State`, `Reason` |
| `transient_retry_attempted` | Info | `TransientRetryScheduler.TryTransientRetryAsync` | `WorkItemId`, `Source` (`periodic`, `targeted`, or `rearm-overdue`), `Outcome`, `State`, `Reason` |

### Upstream remote

| `EventName` | Level | Emitted by | Properties |
|-------------|-------|-----------|------------|
| `upstream.pr_opened` | Info | `GitHubUpstreamRemote.CompleteAsync` | `PrNumber`, `PrUrl`, `WorkBranch`, `BaseBranch` |
| `upstream.pr_merged` | Info | `GitHubUpstreamRemote.CompleteAsync` | `PrNumber`, `MergeSha` |
| `upstream.push` | Info | `GitGenericUpstreamRemote.CompleteAsync` | `Branch`, `RemoteUrl` (credentials stripped) |
| `upstream.api_call_failed` | Warning | `GitHubUpstreamRemote.CreatePullRequestAsync`, `MergePullRequestAsync` | `Operation`, `StatusCode`, `Owner`, `Repo` |

### Authentication

| `EventName` | Level | Emitted by | Properties |
|-------------|-------|-----------|------------|
| `auth.token_read` | Info | `UpstreamRemoteFactory.ReadToken` | `EnvVar` (name only — **never the token value**), `ProjectId` |

### Webhook delivery

| `EventName` | Level | Emitted by | Properties |
|-------------|-------|-----------|------------|
| `webhook.delivered` | Info | `HttpWebhookDispatcher.DispatchToEndpointAsync` | `Endpoint`, `WebhookEvent`, `StatusCode`, `Attempt` |
| `webhook.delivery_failed` | Warning | Same | `Endpoint`, `WebhookEvent`, `Attempts`, `LastFailure` |

## Secret redaction

All log events pass through `SensitiveDataRedactionEnricher` before writing.
It replaces property values with `***` when:

- The **property name** contains `Token`, `Secret`, `Password`, `Authorization`, or `ApiKey`
  (case-insensitive substring match), OR
- The **property value** is a string matching a known secret pattern:
  - GitHub PAT: `gho_…`, `ghp_…`, `github_pat_…`
  - Anthropic key: `sk-ant-…`

This is defence-in-depth. Call sites are explicitly designed never to log raw
secrets (PATs, HMAC secrets, agent API keys). The enricher catches accidental
leakage; it is not a license to log credentials.

## Example audit event (CLEF)

```json
{"@t":"2026-04-29T12:34:56.789Z","@mt":"Agent {Agent} started in sandbox {Sandbox} for phase {Phase}","Agent":"claude","Sandbox":"codeybox-a1b2c3d4e5f","Phase":"work","Audit":true,"EventName":"agent.started","WorkItemId":"3f7e2a1b4c5d6e7f8091a2b3c4d5e6f7","ProjectId":"acme-backend","Application":"CodeyBox","MachineName":"codeybox-host","ThreadId":14}
```

## Log query examples

**All audit events for a work item (jq):**
```sh
jq 'select(.WorkItemId == "3f7e2a1b4c5d6e7f8091a2b3c4d5e6f7")' logs/audit-*.json
```

**All auth.token_read events today:**
```sh
jq 'select(.EventName == "auth.token_read")' logs/audit-$(date +%Y%m%d).json
```

**All failed upstream API calls:**
```sh
jq 'select(.EventName == "upstream.api_call_failed")' logs/audit-*.json
```

**All Warning-or-above audit events:**
```sh
jq 'select(.Audit == true and (.["@l"] == "Warning" or .["@l"] == "Error" or .["@l"] == "Fatal"))' logs/codeybox-*.json
```

## Every event name

`src/CodeyBox.Core/AuditLog.cs` is the authority: one method per event, each
naming its `EventName` literal. The 140 events it emits today, grouped by
prefix and shown without it:

**`agent`** — `attempt_timeout_fallback`, `claude_acp_transport_degraded`, `claude_session_close_failed`, `claude_session_suspend_failed`, `claude_token_push_failed`, `claude_token_pushed_to_vm`, `claude_transcript_sanitizer_failed`, `claude_unauthorized`, `finished`, `killed_by_stuck_probe`, `log_capture_failed`, `pause_dispatch_deferred`, `pause_expired`, `pause_waiting_item_resumed`, `paused`, `restore_requeue_item`, `restore_requeue_swept`, `resume_exhausted_fallback`, `resumed`, `session_resume_liveness_probe_failed`, `smoke_failed`, `smoke_succeeded`, `started`, `started_while_paused`, `structured_stream_probe_failed`, `stuck_detected`, `supervision_injection_completed`, `supervision_injection_queued`, `supervision_injection_started`

**`agentic_conflict_resolver`** — `attempt_failed`

**`audit`** — `auditor_timed_out`, `cross_review_active`, `failed`, `iteration_complete`, `llm_auditor_parked_quota`, `llm_panel_skipped_build_test_gate`, `passed`, `profile_selected`

**`auditor`** — `run`

**`auth`** — `token_read`

**`baseline`** — `migrated`

**`budget`** — `deferred`

**`budget_alert`** — `exceeded`, `recovered`, `startup_safe`, `warning`

**`changelog`** — `generated`, `release_requested`, `webhook_received`, `webhook_rejected`, `work_item_created`

**`concurrency`** — `gated_per_agent`, `gated_rate_aware`

**`(ungrouped)`** — `config_reloaded`, `quota_retry_attempted`, `test_failure_attribution_partial`, `test_failure_attribution_skipped`, `transient_retry_attempted`

**`disk`** — `deferred`

**`plugin`** — `initialization_failed`, `loaded`, `skipped_api_version`, `skipped_not_allowlisted`

**`project_queue`** — `paused`, `resumed`

**`queue`** — `paused`, `resumed`, `started_while_paused`

**`quota_router`** — `agent_fallback`, `all_exhausted`, `audit_agent_not_audit_capable`, `audit_fallthrough`, `deferred`, `probed`, `scored`, `waiting`

**`rebase_resolver`** — `agent_selected`, `agent_unavailable`, `all_at_cap`, `cap_rerouted`, `rerouted`

**`refactor`** — `exclusivity_deferred`

**`sandbox`** — `agent_infra_failure`, `created`, `disposed`, `disposed_on_shutdown`, `leak_detected`, `leak_dispose_failed`, `leak_disposed`, `provisioning_deferred`, `provisioning_transient_retry`, `startup_reconcile_failed`, `startup_reconciled`, `stopped_on_shutdown`, `suspended_on_shutdown`

**`store`** — `disk_full`

**`suggestion`** — `created`, `dismissed`, `promoted`, `revert_failed`, `reverted`

**`upstream`** — `api_call_failed`, `pr_merged`, `pr_opened`, `pr_stale_base`, `push`

**`webhook`** — `delivered`, `delivery_failed`

**`work_item`** — `abandoned_after_recovery`, `cancelled`, `created`, `dependencies_changed`, `dependencies_resolved`, `dependent_cancelled`, `dependent_restored`, `failed`, `item_stale_detected`, `item_stale_recovered`, `patched`, `picked_up`, `post_agent_timeout`, `priority_changed`, `recovered`, `reordered`, `resumed`, `retried`, `terminal_failure_classified`, `transient_cancel_retried`, `transitioned`, `watchdog_parked`, `watchdog_recovered`, `watchdog_stuck`, `worker_dead_failed_terminal`, `worker_dead_recovered`

**`work_prompt`** — `self_review_checklist`

**`worker`** — `deregistered`, `registered`

**`worker_pool`** — `spawn_throttled`, `worker_finished`, `worker_started`

Regenerate this list after adding an event:

```bash
grep -oE 'Audit\((logger, )?"[a-z_.]+"' src/CodeyBox.Core/AuditLog.cs \
  | grep -oE '"[a-z_.]+"' | tr -d '"' | sort -u
```
