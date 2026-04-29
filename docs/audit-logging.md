# Audit logging

CodeyBox writes two rolling JSON log files using [Serilog](https://serilog.net/):

| File | Contents |
|------|----------|
| `logs/codeybox-YYYYMMDD.json` | **All** structured events (Information and above). General operations log. |
| `logs/audit-YYYYMMDD.json` | **Audit-tier only** (`Audit=true` property). Security-relevant events for compliance review. |

Both paths are relative to the API process's working directory by default (typically the directory
you launch the binary from). Override via `appsettings.json` — see [Configuration](#configuration).

The file format is [Compact Log Event Format (CLEF / NDJSON)](https://clef-json.org/): one JSON
object per line. Every event carries the fields documented in [Common properties](#common-properties).

---

## Configuration

```json
"CodeyBox": {
  "AuditLog": {
    "Path":             "logs/codeybox-.json",
    "AuditPath":        "logs/audit-.json",
    "RetainedDays":     30,
    "MaxFileSizeBytes": 104857600
  }
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `Path` | `logs/codeybox-.json` | Path template for the main log. Serilog inserts the date before the trailing dot (e.g. `codeybox-20260429.json`). Relative paths resolve from the process working directory. |
| `AuditPath` | `logs/audit-.json` | Path template for the audit-tier log. Same date-insertion convention. |
| `RetainedDays` | 30 | Number of rolled files to keep per sink. Must be ≥ 1. |
| `MaxFileSizeBytes` | 104857600 (100 MiB) | Per-file size cap before rolling to a new file. |

Startup fails fast if either path's directory cannot be created or written to.

---

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

---

## Audit event taxonomy

### Work item lifecycle

| `EventName` | Level | Emitted by | Properties |
|-------------|-------|-----------|------------|
| `work_item.created` | Info | `WorkItemEndpoints.CreateAsync` | `WorkItemId`, `ProjectId`, `Title` |
| `work_item.picked_up` | Info | `OrchestratorService.RunWorkerAsync` | `WorkerId`, `WorkItemId` |
| `work_item.transitioned` | Info | `PipelineRunner.Transition` | `WorkItemId`, `State` (target state) |
| `work_item.cancelled` | Info | `PipelineRunner.RunAsync` (cancellation handler) | `WorkItemId` |
| `work_item.failed` | Warning | `PipelineRunner.TransitionFailed` | `WorkItemId`, `Error` |

### Agent execution

| `EventName` | Level | Emitted by | Properties |
|-------------|-------|-----------|------------|
| `agent.started` | Info | `PipelineRunner.RunAgentPhaseAsync`, `RunAgentMergePhaseAsync` | `Agent`, `Sandbox`, `Phase` (`work`, `rework`, or `merge`) |
| `agent.finished` | Info | Same as above | `Agent`, `Sandbox`, `Success`, `ExitCode`, `DurationMs` |

### Sandbox lifecycle

| `EventName` | Level | Emitted by | Properties |
|-------------|-------|-----------|------------|
| `sandbox.created` | Info | `MultipassSandboxProvider.CreateAsync` | `VmName`, `NetworkProfile` |
| `sandbox.disposed` | Info | `MultipassSandbox.DisposeAsync` | `VmName` |

### Audit phase

| `EventName` | Level | Emitted by | Properties |
|-------------|-------|-----------|------------|
| `auditor.run` | Info | `PipelineRunner.CollectFindingsAsync` | `AuditorName`, `WorseSeverity` (`none`/`Info`/`Warning`/`Error`), `DurationMs` |
| `audit.iteration_complete` | Info | `PipelineRunner.RunAuditLoopAsync` | `Iteration`, `MaxIterations`, `BlockingCount`, `NonBlockingCount` |
| `audit.passed` | Info | Same | `Iteration` |
| `audit.failed` | Warning | Same | `Iteration`, `BlockingCount` |

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

---

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

---

## Example audit event (CLEF)

```json
{"@t":"2026-04-29T12:34:56.789Z","@mt":"Agent {Agent} started in sandbox {Sandbox} for phase {Phase}","Agent":"claude","Sandbox":"codeybox-a1b2c3d4e5f","Phase":"work","Audit":true,"EventName":"agent.started","WorkItemId":"3f7e2a1b4c5d6e7f8091a2b3c4d5e6f7","ProjectId":"acme-backend","Application":"CodeyBox","MachineName":"codeybox-host","ThreadId":14}
```

---

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
