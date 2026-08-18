# In-VM smoke probe

The host-side credential smoke gate (`CredentialSmokeGate`) only proves the
orchestrator *host* holds the right credential env-vars. It cannot see whether
the agent CLI actually runs **inside** the sandbox. The in-VM smoke prober
(`InVmSmokeProber`) closes that gap: it clones a sandbox from the active
baseline image and execs each agent's declared smoke sequence
(`IInVmSmokeProbe.BuildSteps`) inside it.

## What it catches — the three-stage cursor cascade

Activating cursor on 2026-05-28 produced a three-stage failure cascade on the
first dispatched work item. Each stage is now caught at smoke time:

| Stage | Real-dispatch symptom | In-VM smoke step that catches it |
|---|---|---|
| 1. Binary not on PATH | `agent: command not found` (exit 127) | `agent --version` exits non-zero → agent excluded |
| 2. Auth materialised to wrong path | exit 1, "Authentication required" | runner's `AuthMaterialiseScript` + `agent status` exits non-zero → agent excluded |
| 3. Workspace trust required | exit 1, "Workspace Trust Required" | a real `agent --print --trust --force` turn (built from `CursorAgentRunner.WorkspaceTrustInvocationPrefix`, the same prefix dispatch uses) exits non-zero → agent excluded. Engages workspace trust, which `--version`/`status` cannot. `CursorAgentRunnerTrustRegressionTests` stays as a fast argv-level guard on the runner |

The probe steps reuse the runner's **exact** binary name
(`CursorAgentRunner.DefaultBinary`) and auth-materialisation script
(`CursorAgentRunner.AuthMaterialiseScript`), so path drift like PR #138 is
exercised by the probe, not discovered at first dispatch.

Checks are **exit-code only** — never output-text matching. CLI auth wording
drifts between releases and `"Not logged in"` contains `"logged in"`, so a
substring guard both false-passes and risks false-benching a healthy agent.

## How a failure routes past the broken agent (AC#1)

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
first dispatch (AC#1). Agents with no sandbox CLI — `copilot` by default — are
exempt via `CodeyBox:Smoke:InVm:ExemptAgentsWithoutProbe` (warned, not benched).
When the prober is disabled or no probes are registered, enforcement is inactive
and the validator only warns.

`CodeyBox:Smoke:Enabled=false` is the master switch and suppresses this gate,
startup coverage benching, and router smoke exclusions. `CodeyBox:Smoke:InVm:Enabled=false`
disables only the in-VM prober; host credential smoke and router smoke
exclusions remain active while the master switch is true.

## Caching, self-healing and operator reset

- Only **passing** verdicts are cached, keyed by `(agent, baselineRef)`. A
  cache hit provisions nothing — steady-state dispatch is free (AC#2).
- A **failing** verdict is never cached, so the next background sweep (default
  every 5 min) re-execs the CLI. Once the operator fixes the binary/auth and
  rebakes (new content-hash ref) or the next sweep passes, the agent rejoins
  routing — the self-healing path.
- A baseline rebake changes the content-hash ref, so the next sweep re-probes
  against the new image (AC#3).
- `POST /admin/agent/{name}/reset` clears the registry **and** invalidates the
  in-VM cache for that agent, so a reset always forces a fresh re-probe rather
  than replaying a verdict captured before the fix.

## Operator runbook: verifying the cascade is caught at smoke time

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
