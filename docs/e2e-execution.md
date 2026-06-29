# E2E execution (replay runtime + cheap-CPU pool)

CodeyBox treats committed end-to-end test cases as **deterministic replay
artifacts**: a JSON document of steps and assertions persisted in
[`TestCase.ExecutableArtifactJson`](test-cases.md) when `AutomationKind` is
`E2eReplay`. This document describes the runtime that executes one, the
pool that runs many concurrently, and the dispatcher that ties them
together.

The brief's hard rule: **E2E load never runs on the local coding-worker
fleet.** That separation is architectural — the dispatcher only depends on
`IE2eExecutionPool`, and Program builds the E2E provider independently from
the coding pipeline's admitted `ISandboxProvider`. `PoolKind=remote-ssh`
selects the existing multipass-over-SSH cheap-CPU provider; `PoolKind=local`
exists for development and still bypasses the coding fleet's admission gate.

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
| `PoolKind` | `local` | `local` builds an independent development provider from `CodeyBox:SandboxProvider`. `remote-ssh` uses the existing multipass-over-SSH provider for the cheap CPU cloud pool. |
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
E2E execution pool clones from it for every lease. Per-test startup is
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
- **Failed** — the replay driver reports a deterministic app/assertion
  failure.
- **Error** — readiness probe never came up, the artifact JSON failed to
  parse, the artifact schema is invalid, the fixed replay driver is missing,
  the test case vanished mid-claim, the per-run timeout fired, output hit the
  capture bound, or an exec call threw. Distinguishes infra failure from real
  assertion failure on dashboards.
- **Canceled** — operator hit `POST /e2eruns/{id}/cancel`, or the
  dispatcher's process is shutting down mid-run.

The result column on a terminal row is a serialized
`E2eRunResult` — `{ passed, summary, failureKind, failedStepIndex,
stepResults[], assertionResults[], durationMs }`. Step + assertion
sub-results carry a redacted bounded tail of stdout/stderr; downstream
dashboards get enough context to investigate without re-running.

On Pass / Fail / Error the dispatcher also stamps the owning test case's
`LastRunPassed`, `LastRunAt`, and `LastRunResult` so the test-case list
reflects the most recent execution outcome.

## Replay artifact schema

Persisted as JSON in `TestCase.ExecutableArtifactJson` for cases whose
`AutomationKind` is `E2eReplay`:

```jsonc
{
  "name": "auth-login-happy-path",
  "readiness": {
    "url": "http://localhost:8080/healthz",
    "maxAttempts": 30,
    "delayMs": 1000
  },
  "steps": [
    {
      "action": "navigate",
      "target": "http://localhost:8080/login",
      "delayAfterMs": 200
    },
    {
      "action": "fill",
      "selector": "#email",
      "value": "alice@example.com"
    },
    {
      "action": "click",
      "selector": "button[type=submit]",
      "delayAfterMs": 200
    }
  ],
  "assertions": [
    {
      "kind": "selectorVisible",
      "selector": "#account-menu",
      "description": "account menu is visible after login"
    }
  ]
}
```

The runtime never executes artifact-controlled argv. It validates the JSON
schema, checks readiness with a fixed bounded `curl`, then invokes the
pre-baked image's fixed `codeybox-e2e-replay --artifact-json-stdin` driver and
passes the artifact on stdin. The driver is the trusted component that turns
recorded actions/selectors/assertions into deterministic browser/app
interaction. The brief calls out a cheap-model selector-repair fallback as a
future addition; this runtime records failure detail but does not repair.

## Out of scope (deliberate)

- **Cheap-model selector-repair on failure.** The seam exists
  (`E2eRunResult.FailedStepIndex`); the hook is intentionally not built.
- **Conformance gates / coverage scoring.** Lives in
  [`test-cases.md`](test-cases.md) and on a separate item that consumes
  `ConformanceJson` against the run history this runtime produces.
