# E2E execution (replay runtime + cheap-CPU pool)

CodeyBox treats committed end-to-end test cases as **deterministic replay
artifacts**: a JSON document of steps and assertions persisted in
[`TestCase.ExecutableArtifactJson`](test-cases.md) when `AutomationKind` is
`E2eReplay`. This document describes the runtime that executes one, the
pool that runs many concurrently, and the dispatcher that ties them
together.

The brief's hard rule: **E2E load never runs on the local coding-worker
fleet.** That separation is architectural — the dispatcher only depends on
`IE2eExecutionPool`, the pool only depends on the configured
`ISandboxProvider`, and there is no DI path from either back to
`WorkerPool`. A unit test pins this contract via reflection so a future
refactor can't reintroduce a hidden coupling.

## Shape at a glance

```
                          enqueue          claim
TestCase (E2eReplay) ──→ /e2eruns ──→ SqliteE2eRunStore
                                            │
                                            ▼
                                   E2eRunDispatcher (hosted)
                                            │
                              lease         │ replay artifact
              IE2eExecutionPool ◄───────────┴────────────► IE2eReplayRuntime
              (cheap CPU-only VMs,                          (no model in loop)
               clone-per-test)                                   │
                                                                 ▼
                                                     UpdateStatusAsync(Passed/Failed)
                                                     + TestCase.LastRun*
```

## Configuration (`CodeyBox:E2eExecution`)

| Key | Default | Notes |
|---|---|---|
| `Enabled` | `false` | Master switch. The dispatcher does NOT drain the queue when off; the REST surface stays available so operators can enqueue ahead of enabling. |
| `MaxConcurrent` | `4` | Hard upper bound on concurrent leases. Sized for the cheap-CPU cloud quota, NOT for the coding fleet. Hot-reloadable. Floor 1, ceiling 512. |
| `PoolKind` | `local` | `local` wraps the orchestrator's `ISandboxProvider`. `remote-ssh` is the planned multi-host implementation; not implemented yet — operator config is accepted but the dispatcher will fail fast when selected. |
| `NetworkProfile` | `null` | Logical bridge profile cloned sandboxes attach to (passed through to `SandboxNetworkPolicy.ProfileName`). Use the app-under-test profile that allows only the HTTP service ports the runtime hits. |
| `SandboxImageReference` | `null` | Falls back to the orchestrator-wide `SandboxImageReference`. Set when the E2E pool runs from a separate pre-baked image carrying the app stack. |
| `BaselineImageRef` | `null` | Optional content-hashed baseline pin. Mirrors `SandboxSpec.BaselineImageRef`. |
| `PollInterval` | `00:00:01` | Idle-queue polling cadence. Hot-reloadable. |
| `PerRunTimeout` | `00:15:00` | Wall-clock cap per replay. The dispatcher cancels and records `Error`/`PerRunTimeout`. |

## Pre-baked image (the parallelism win)

The intended production shape: bake the app stack (DB / services / headless
browser) into a baseline image once via the existing
`MultipassUseBaselineImages` flow, point `E2eExecution:BaselineImageRef`
(or `E2eExecution:SandboxImageReference`) at it, and the
`LocalE2eExecutionPool` clones from it for every lease. Per-test startup is
fast (snapshot clone) and heavy setup is amortised once. Clone-per-test,
run, discard — no slot reuse.

## REST surface

| Verb | Path | Purpose |
|---|---|---|
| `POST` | `/e2eruns` | Enqueue one run against a test case. |
| `POST` | `/e2eruns/bulk` | Enqueue a batch of runs (one per test-case id); response includes the shared `BatchId`. |
| `GET`  | `/e2eruns` | List all runs, newest-first. |
| `GET`  | `/e2eruns/{id}` | Get one run. |
| `POST` | `/e2eruns/{id}/cancel` | Mark a queued or running run as `Canceled`. Conflicts (409) on terminal runs. |
| `GET`  | `/testcases/{id}/runs` | List runs for one test case. |
| `GET`  | `/e2eruns/batches/{batchId}` | Aggregate report for a batch — totals by status + `Complete: true` once nothing is `Queued` or `Running`. |

Each enqueue validates: the test case must exist, its
`AutomationKind` must be `E2eReplay`, and `ExecutableArtifactJson` must be
non-empty.

## Run lifecycle

```
Queued → Running → Passed | Failed | Error | Canceled
```

- **Passed** — every step + assertion succeeded.
- **Failed** — a step exited non-zero (when `FailOnNonZeroExit` is true, the
  default), an assertion's expected exit code / stdout substring didn't
  match, or an empty step/assertion was encountered.
- **Error** — readiness probe never came up, the artifact JSON failed to
  parse, the test case vanished mid-claim, the per-run timeout fired, or
  an exec call threw. Distinguishes infra failure from real assertion
  failure on dashboards.
- **Canceled** — operator hit `POST /e2eruns/{id}/cancel`, or the
  dispatcher's process is shutting down mid-run.

The result column on a terminal row is a serialized
`E2eRunResult` — `{ passed, summary, failureKind, failedStepIndex,
stepResults[], assertionResults[], durationMs }`. Step + assertion
sub-results carry the last 4 KiB of stdout/stderr; downstream dashboards
get enough context to investigate without re-running.

On Pass / Fail the dispatcher also stamps the owning test case's
`LastRunPassed`, `LastRunAt`, and `LastRunResult` so the test-case list
reflects the most recent execution outcome.

## Replay artifact schema

Persisted as JSON in `TestCase.ExecutableArtifactJson` for cases whose
`AutomationKind` is `E2eReplay`:

```jsonc
{
  "name": "auth-login-happy-path",
  "readiness": {
    "argv": ["curl", "-fsS", "http://localhost:8080/healthz"],
    "maxAttempts": 30,
    "delayMs": 1000
  },
  "steps": [
    {
      "argv": ["curl", "-fsS", "-X", "POST", "http://localhost:8080/login", "-d", "{...}"],
      "workingDirectory": "/work",
      "failOnNonZeroExit": true,
      "delayAfterMs": 200
    }
  ],
  "assertions": [
    {
      "argv": ["curl", "-fsS", "http://localhost:8080/me"],
      "expectExitCode": 0,
      "expectStdoutContains": "\"email\":\"alice@example.com\"",
      "description": "/me returns the freshly-authenticated session"
    }
  ]
}
```

Steps execute sequentially inside the cloned sandbox via
`ISandbox.ExecAsync`. The runtime does not reach outside the sandbox; any
HTTP traffic is the artifact's own concern (typically `curl` against the
pre-baked app's loopback port).

Richer assertion kinds (DOM selectors, JSON-path matchers) layer on top of
the same `argv + expectations` shape later without changing the runtime
contract — they reduce to "run this command and check the output". The
brief calls out a cheap-model selector-repair fallback as a future
addition; this runtime exposes the seam by recording
`failedStepIndex` on failure but does not act on it.

## Out of scope (deliberate)

- **The remote-SSH multi-host pool** (`PoolKind=remote-ssh`). The
  abstraction (`IE2eExecutionPool`) is the seam the future implementation
  will plug into; the dispatcher does not need to know whether the sandbox
  lives on the same host or a separate cheap-CPU box.
- **Cheap-model selector-repair on failure.** The seam exists
  (`E2eRunResult.FailedStepIndex`); the hook is intentionally not built.
- **Conformance gates / coverage scoring.** Lives in
  [`test-cases.md`](test-cases.md) and on a separate item that consumes
  `ConformanceJson` against the run history this runtime produces.
