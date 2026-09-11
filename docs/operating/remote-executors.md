# Remote executor hosts

An executor host is a `CodeyBox.Executor` process running on a machine other
than the orchestrator. It provisions sandboxes locally through the standard
`ISandboxProvider` abstraction and executes phases through the `IPipelineRunner`
phase-execution seam. It connects **outbound** to the orchestrator (plain HTTPS
POSTs) and never opens an inbound listening port, so it can sit behind NAT or
a host firewall.

This page covers the process, its registration and its liveness. Dispatching
work to a registered executor is a separate item: an executor that registers
and heartbeats but is never sent work is the acceptance state.

## Running the executor

```sh
export CODEYBOX_API_KEY='<orchestrator-api-key>'
dotnet run --project src/CodeyBox.Executor -- \
  --CodeyBox:Executor:HostId=exec-1 \
  --CodeyBox:Executor:OrchestratorBaseUrl=https://orchestrator:5000/
```

All operational values live under `CodeyBox:Executor` and are hot-reloadable
(except `OrchestratorBaseUrl`, which is pinned at startup):

| Config key | Type | Default | Purpose |
|---|---|---|---|
| `HostId` | `string` | `""` (executor mode disabled) | Stable host id; survives restarts so re-registration upserts one row |
| `OrchestratorBaseUrl` | `string` | `""` | Absolute `http(s)` URL of the orchestrator |
| `ApiKeyEnvVar` | `string` | `CODEYBOX_API_KEY` | Env var carrying the orchestrator API key (never stored in config, never logged) |
| `MaxConcurrentSandboxes` | `int?` | `null` (uncapped) | Host-local sandbox capacity. `0` registers but is never selected |
| `AllowedNetworkProfiles` | `string[]` | `[]` (all) | Network profiles this host accepts; `"*"` also means all |
| `DeclaredCredentials` | `string[]` | `[]` | Agent credential sets this host holds (e.g. `claude`, `codex`) |
| `Cordoned` | `bool` | `false` | Draining: registers and heartbeats but is never selected |
| `Healthy` | `bool` | `true` | Health gate: `false` routes placements away without unregistering |
| `LocalSandboxProvider` | `string` | `process` | `process` (dev runner, UNSAFE) or `bubblewrap` |
| `HeartbeatInterval` | `string` (TimeSpan) | `"00:00:15"` | Registry heartbeat cadence |
| `RequestTimeout` | `string` (TimeSpan) | `"00:00:20"` | Per-request timeout for register/heartbeat calls |
| `DisconnectPolicy` | `enum` | `RetainSandboxForResume` | Only policy in this item (see below) |

Capacity, cordoning and health reuse the per-host placement vocabulary from
the remote sandbox provider (`HostId`, `MaxConcurrentSandboxes`, `Cordoned`,
`Healthy`, `AllowedNetworkProfiles`) — one meaning on both sides of the
connection, not two.

## Registration and liveness

On connect the executor `POST`s its registration assertion to
`/executors/register` (see the [API reference](../reference/api.md)) and then
heartbeats on `HeartbeatInterval`. Rows live in the existing worker registry
(visible via `GET /workers`) and heartbeats flow through
`IWorkerRegistry.HeartbeatAsync`, so an executor that dies is reclaimed by the
existing dead-worker reaper — there is no second liveness scheme. Heartbeats
that fail are retried on the next interval; only the reaper decides death.

## Connection loss

Losing the connection must not orphan a running sandbox. The policy is
`RetainSandboxForResume`:

- **On drop**, the executor keeps every in-flight sandbox alive and keeps
  tracking its phase binding locally. A dropped connection or an orchestrator
  restart never kills running agent work.
- **On reconnect**, the executor reconciles local sandbox inventory against
  its tracked phases: still-owned phases resume, and any running sandbox with
  no tracked owner is torn down, so reconnection never leaves a sandbox
  running that nothing tracks. If the provider reports an incomplete
  inventory, nothing is reclaimed rather than risk killing a tracked sandbox
  on an uninventoried host.
- **On process exit** (graceful shutdown), tracked sandboxes are torn down —
  retention applies to connection loss, not to exit, where nothing could
  resume them.

If the outage outlasts the dead-worker threshold, the reaper reclaims the
registration exactly as it would a dead in-process worker; the executor
re-registers on reconnect.
