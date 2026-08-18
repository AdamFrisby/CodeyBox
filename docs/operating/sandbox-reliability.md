# Keeping the sandbox fleet healthy

Three background mechanisms stop a fleet of throwaway VMs from degrading into a
pile of orphaned guests and silently broken agents: a leak reaper that disposes
sandboxes that outlived their work item, a smoke prober that benches an agent
whose CLI does not actually run inside the sandbox, and the suspend/resume
handling that keeps an in-flight agent turn alive across an orchestrator
restart.

## Leaked sandboxes

The orchestrator runs a periodic background sweep (`SandboxLeakReaper`) that
detects — and optionally disposes — persistent managed sandboxes that outlived
their work item. These "leaked" sandboxes accumulate when the orchestrator
crashes mid-disposal and can exhaust host or remote-provider memory and disk.

### What counts as a leak

A provider-owned sandbox is classified as leaked when these conditions hold:

1. A managed lifecycle provider reports it as an ordinary provider-owned
   sandbox. Each provider enforces its own ownership metadata and configured
   name prefix; baseline images are inventoried separately.
2. The current orchestrator process does **not** report it as actively owned by
   a work item. After a normal restart, active in-memory ownership is initially
   empty, so the age threshold below guards against false positives. Durable
   suspended-sandbox mappings are also exempt while startup recovery owns them.
3. Its creation timestamp — derived from provider metadata or provider-owned
   staging metadata where available — is **older than
   `LeakAgeThreshold`** (default 30 minutes), or its creation timestamp cannot be
   determined.

A sandbox that is mid-way through the VM-launch → clone → mount → start sequence
is typically less than 10 minutes old. The 30-minute threshold is a conservative
safety margin: it is unlikely that a legitimately active sandbox would be both
untracked *and* over 30 minutes old.

Sandboxes for which the creation time still cannot be determined are declared
leaked once they are untracked by the current provider snapshot. Their age is
reported from the threshold boundary and their reason is
`untracked_sandbox_missing_creation_metadata`, so operators can distinguish
missing metadata from an ordinary age-threshold leak.

### Which providers can leak

| Provider | Leaks tracked? | Why |
|---|---|---|
| `multipass` | **Yes** | VMs persist as KVM guests; `multipass list` reports them after a crash |
| `incus` | **Yes** | VMs and provider-owned staging persist; the dedicated Incus project reports them after a crash |
| `multipass-remote` | **Yes** | Remote VMs persist and are inventoried through the remote lifecycle provider |
| `sprites` | **Yes** | Remote sprites have persistent service identities and lifecycle inventory |
| `bubblewrap` | No | Processes exit when the orchestrator dies; no persistent identity to track |
| `process` | No | Dev-only; no persistent lifecycle |

### Configuration

All options are under `CodeyBox:SandboxLeak` in `appsettings.json`.

```json
{
  "CodeyBox": {
    "SandboxLeak": {
      "Enabled": true,
      "CheckInterval": "00:15:00",
      "LeakAgeThreshold": "00:30:00",
      "AutoDispose": true
    }
  }
}
```

| Key | Default | Reload | Description |
|---|---|---|---|
| `Enabled` | `true` | startup only | Enable or disable the sweep entirely. |
| `CheckInterval` | `00:15:00` | startup only | How often the scan runs; sampled when the timer is constructed. |
| `LeakAgeThreshold` | `00:30:00` | hot | Minimum age before an untracked sandbox is declared leaked. |
| `AutoDispose` | `true` | hot | Purge each detected leak automatically. |
| `MaxConcurrentAutoDispose` | `4` | hot | Parallel disposals, capped to limit pressure on the provider during restart cleanup. |

#### AutoDispose

`AutoDispose` defaults to **true** because stale persistent sandboxes keep
consuming provider resources after their phase has ended. Set
`AutoDispose: false` for detection-only operation on a diagnostic host.

Each auto-dispose runs with a 5-minute per-sandbox timeout and is best-effort:
one failed disposal never blocks the rest of the sweep.

### Audit events

The reaper emits the following audit-tier events (filtered to the audit-only log
and any configured webhook endpoints):

| Event | When emitted |
|---|---|
| `sandbox.leak_detected` | A leaked sandbox was found (detection-only or before auto-dispose) |
| `sandbox.leak_disposed` | A leaked sandbox was successfully disposed |
| `sandbox.leak_dispose_failed` | Disposal of a leaked sandbox failed |

Each event carries `{ name, ageMinutes, diskMb, reason }` in the structured log
fields. The `reason` is a stable classification code such as
`untracked_sandbox_age_threshold_exceeded` or
`untracked_sandbox_missing_creation_metadata`.

### API

#### `GET /sandboxes/leaked`

Returns the list of sandboxes detected as leaked on the **most recent sweep**
and not yet successfully disposed. An empty array means no pending leaked
sandboxes remain from the last sweep; with `AutoDispose=true`, stale VMs may
have been detected and already purged.

```json
[
  {
    "name": "codeybox-a1b2c3d4e5f6",
    "createdAt": "2026-05-04T02:00:00+00:00",
    "ageMinutes": 127.3,
    "diskMb": null,
    "reason": "untracked_sandbox_age_threshold_exceeded",
    "providerId": "incus"
  }
]
```

#### `GET /admin/sandbox-leaks`

Returns an operator summary for sandboxes detected as leaked and not yet
successfully disposed by the latest sweep.

```json
{
  "count": 1,
  "agesMinutes": [127.3],
  "leaks": [
    {
      "name": "codeybox-a1b2c3d4e5f6",
      "createdAt": "2026-05-04T02:00:00+00:00",
      "ageMinutes": 127.3,
      "diskMb": null,
      "reason": "untracked_sandbox_age_threshold_exceeded",
      "providerId": "incus"
    }
  ]
}
```

#### `POST /sandboxes/leaked/{name}/dispose`

Operator-triggered dispose of a specific leaked sandbox. Works regardless of
the `AutoDispose` configuration. The sandbox must be present in the latest leak
snapshot, and the owning provider re-verifies ownership beside its destructive
operation. If more than one provider reports the same name, first read
`providerId` from `GET /sandboxes/leaked`, then pass it as the exact
`?providerId=...` query value. A name-only request is rejected as ambiguous.

- On success: `200 { "disposed": "<name>" }`
- On unknown name (not in latest leaked list): `404`
- On duplicate name without an exact `providerId`: `409`
- On invalid `providerId`: `400`
- On timeout (5 min): `504`
- On error: `500` with a generic message; provider diagnostics remain server-side

### Manual cleanup

If the reaper is unavailable, inspect the selected provider directly. Confirm
the resource's CodeyBox ownership before deleting one exact instance; do not
bulk-delete by a default prefix when prefixes or projects are configurable.

```bash
# Local Multipass inventory and one exact delete
multipass list
multipass delete --purge codeybox-a1b2c3d4e5f67

# Incus inventory in the configured dedicated project and one exact delete
incus --project codeybox list
incus --project codeybox delete codeybox-a1b2c3d4e5f67 --force
```

Baseline instances are **not** touched by the sandbox leak reaper. They have a
separate content-addressed inventory and baseline sweep; preserve them during
manual sandbox cleanup unless intentionally invalidating the bake cache.

### Expected vs. active sandbox tracking

The reaper determines whether a sandbox is expected from the active-ownership
snapshot supplied by its managed lifecycle provider. VM providers register
ownership before exposing a new instance and clear it after disposal. During a
Multipass/Incus cutover, lifecycle inventory retains the concrete backend ID so
duplicate names can be routed without broadcasting a destructive operation.

**After an orchestrator restart**, the in-memory set starts empty. This means
pre-existing ordinary instances are initially untracked unless a durable
suspended-sandbox mapping reserves them for startup recovery. The age threshold
prevents other prior-process instances from being classified as leaked
immediately: only instances older than `LeakAgeThreshold` are declared leaked
on the first sweep.

## Agent smoke probes

The host-side credential smoke gate (`CredentialSmokeGate`) only proves the
orchestrator *host* holds the right credential env-vars. It cannot see whether
the agent CLI actually runs **inside** the sandbox. The in-VM smoke prober
(`InVmSmokeProber`) closes that gap: it clones a sandbox from the active
baseline image and execs each agent's declared smoke sequence
(`IInVmSmokeProbe.BuildSteps`) inside it.

### The three-stage failure cascade it catches

Turning on a new agent CLI can fail in three separate places before it ever
does useful work. Cursor hit all three in sequence on its first dispatched work
item; the probe catches each one before dispatch:

| Stage | Real-dispatch symptom | In-VM smoke step that catches it |
|---|---|---|
| 1. Binary not on PATH | `agent: command not found` (exit 127) | `agent --version` exits non-zero → agent excluded |
| 2. Auth materialised to wrong path | exit 1, "Authentication required" | runner's `AuthMaterialiseScript` + `agent status` exits non-zero → agent excluded |
| 3. Workspace trust required | exit 1, "Workspace Trust Required" | a real `agent --print --trust --force` turn (built from `CursorAgentRunner.WorkspaceTrustInvocationPrefix`, the same prefix dispatch uses) exits non-zero → agent excluded. Engages workspace trust, which `--version`/`status` cannot. `CursorAgentRunnerTrustRegressionTests` stays as a fast argv-level guard on the runner |

The probe steps reuse the runner's **exact** binary name
(`CursorAgentRunner.DefaultBinary`) and auth-materialisation script
(`CursorAgentRunner.AuthMaterialiseScript`), so a path change in the runner is
exercised by the probe rather than discovered at first dispatch.

Checks are **exit-code only** — never output-text matching. CLI auth wording
drifts between releases and `"Not logged in"` contains `"logged in"`, so a
substring guard both false-passes and risks false-benching a healthy agent.

### How a failing agent is routed around

A failing probe marks the agent excluded in `AgentAvailabilityRegistry` under
`SmokeExclusionSource.InVmSmoke`. `AgentClassRouter` already skips excluded
members, so the work item routes to a working alternative — no router change.

The router (and the work/audit dispatch paths) call
`IInVmSmokeGate.EnsureAvailableAsync` for any still-`Available` member **before
trusting it** — the gate owns the read→probe→re-read and returns the verdict, so
callers never bind to the availability registry alongside it. The *first*
dispatch after startup or a baseline rebake is therefore gated by a real
in-sandbox check rather than racing the background sweep. This covers the
direct-agent work path too (a project with no `AgentClass`), not just
class-routed items. A new `AgentClass` member whose agent has no
registered `IInVmSmokeProbe` is **benched at startup** by
`InVmSmokeProbeCoverageValidator` (under `SmokeExclusionSource.MissingProbe`),
so its unverified CLI is routed past at smoke time rather than discovered at
first dispatch. Agents with no sandbox CLI — `copilot` by default — are
exempt via `CodeyBox:Smoke:InVm:ExemptAgentsWithoutProbe` (warned, not benched).
When the prober is disabled or no probes are registered, enforcement is inactive
and the validator only warns.

`CodeyBox:Smoke:Enabled=false` is the master switch and suppresses this gate,
startup coverage benching, and router smoke exclusions. `CodeyBox:Smoke:InVm:Enabled=false`
disables only the in-VM prober; host credential smoke and router smoke
exclusions remain active while the master switch is true.

### Caching, self-healing and operator reset

- Only **passing** verdicts are cached, keyed by `(agent, baselineRef)`. A
  cache hit provisions nothing — steady-state dispatch is free.
- A **failing** verdict is never cached, so the next background sweep (default
  every 5 min) re-execs the CLI. Once the operator fixes the binary/auth and
  rebakes (new content-hash ref) or the next sweep passes, the agent rejoins
  routing — the self-healing path.
- A baseline rebake changes the content-hash ref, so the next sweep re-probes
  against the new image.
- `POST /admin/agent/{name}/reset` clears the registry **and** invalidates the
  in-VM cache for that agent, so a reset always forces a fresh re-probe rather
  than replaying a verdict captured before the fix.

### Verifying it works

1. Break stage 1 in the baseline image (e.g. remove the `~/.local/bin/agent`
   symlink) and rebake. Watch the logs: the next sweep logs
   `Agent cursor smoke transitioned PASS -> FAIL ... agent binary not runnable`
   and `/admin/agents/availability` shows cursor excluded. New work items route
   to the next class member instead of failing.
2. Restore the symlink but break stage 2 (materialise auth to the legacy
   `~/.cursor/credentials.json`). The probe's `agent status` step exits
   non-zero and cursor stays excluded with `agent status failed` — caught before
   any work item is dispatched.
3. Fix both and call `POST /admin/agent/cursor/reset`. The cache is invalidated;
   the next sweep/dispatch re-probes, the probe passes, and cursor returns to
   routing.

The unit suite mirrors this in `InVmSmokeProberTests`
(`ThreeStageCascade_EachStageCaughtAtSmokeTime`) using a scripted sandbox.

## Surviving suspend and resume

`Shutdown.SandboxTeardownMode` defaults to `Stop`, which discards in-VM process
state. Set it to `Suspend` and the orchestrator instead freezes each VM on
shutdown and thaws it on startup — cheaper than re-running a turn, but from the
LLM provider's side every in-flight TCP connection is torn down during the
freeze. Each agent CLI reacts differently, so several layers cover the gap:

| Layer | What it does |
|---|---|
| Log-file adoption | The agent writes to `CODEYBOX_AGENT_LOG_FILE` with an `.exit` sidecar, so `SandboxResumeOnStartupService` can re-tail and adopt the in-VM agent after the VM restarts. |
| CLI-native resume | Claude and Codex capture a validated session id from structured output and resume that exact session in a live sandbox with a short continuation prompt. Bounded, and skipped for hard quota or auth failures. |
| Durable agent-turn checkpoint | The dirty tree and CLI scratchpad are persisted so the exact route can resume in a fresh sandbox — see [`agent-turn-checkpoints.md`](agent-turn-checkpoints.md). |
| Incus interrupted-exec recovery | Incus keeps `/work` on the bounded COW root disk. On an ambiguous or unavailable exec it makes one immediate bounded restart attempt of the exact owned `STOPPED` VM; configured delayed attempts run at later exec boundaries while the sandbox stays poisoned. |
| Narrow retry shim | `CliAgentRunnerBase` re-invokes the agent **once**, only for unknown failures matching a small suspend-related exit-code allowlist (`AgentSuspendResilience` in `CodeyBox.Agents`). |
| Orchestrator retry | Stranded-item recovery, transient-cancellation auto-retry, and durable transient-network auto-retry when adoption times out or the failure classifies as transient (`AgentFailureClassifier.TransientNetworkPatterns`). |

The orchestrator retry is the preferred path for recognised transport failures:
it persists state and applies backoff with jitter. The shim stays deliberately
narrow so it cannot create a synchronised second request herd ahead of durable
scheduling.

### Measuring it

`AgentSuspendResilienceMatrixTests` provisions one real VM per cell, starts a
low-token LLM call, suspends the VM 1 s in, holds for N seconds, then restarts
it. The design target is that every agent finishes or classifies as recoverable
without operator action for N ≤ 60 s, ideally to N ≤ 300 s; beyond that,
provider-side idle timeouts leave only the orchestrator recovery path.

**The matrix has not yet been run on a KVM-capable host, so there are no
measured results to report.** Running it needs Multipass on the host, agent CLIs
baked into the baseline (see
[`../reference/sandbox-baselines.md`](../reference/sandbox-baselines.md)), and
host credentials for every agent under test:

```bash
export CODEYBOX_RUN_AGENT_SUSPEND_SMOKE=1
export CODEYBOX_CLAUDE_API_KEY=…
export CODEYBOX_CODEX_API_KEY=…
export CODEYBOX_GEMINI_API_KEY=…
export CODEYBOX_CURSOR_AUTH_FILE=~/.cursor/credentials.json   # or CODEYBOX_CURSOR_AUTH_JSON
export CODEYBOX_OPENCODE_AUTH_FILE=…

dotnet test tests/CodeyBox.Tests/CodeyBox.Tests.csproj \
  --filter "Category=AgentSuspendResilience"
```

Each scenario provisions a VM, so a full 5-agent × 4-duration matrix takes
several hours on a cold host. The `agent-suspend-resilience` workflow runs the
same filter on a self-hosted runner labelled `multipass` with KVM and
credentials — not on every push, given cost and duration. Point the
`CODEYBOX_ACP_BRIDGE_VERIFY_VM` repository variable at an already-baked baseline
VM: the workflow clones it into a disposable verifier VM before running
`scripts/publish-acp-bridge.sh`, so bridge verification neither depends on
runner-local VM state nor mutates the stopped baseline.

If an agent fails at N ≤ 60 s even with durable recovery, work through it in
this order: add the novel stderr shape to
`CodeyBox:TransientNetworkFailurePatterns` if it should count as retryable;
add the CLI's own `--retry` flag in that agent's `BuildInvocation`, work phase
only; and only as a last resort bundle a per-CLI wrapper in the baseline image
or use `LD_PRELOAD` to tune `TCP_USER_TIMEOUT` for that one process. Measure
first — the matrix is the evidence, not intuition.
