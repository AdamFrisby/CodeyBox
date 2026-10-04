# Mutation-testing auditor (`tests:mutation-rigor`)

The mutation-testing auditor is a deterministic, per-item **testing rigor
gate**. For the code CHANGED in a work item, it asks the un-gameable
question:

> If I deliberately break this branch, does at least one test fail?

If no test fails, the test for that branch killed no mutant — a no-assert
test, an implementation-mirroring assertion, or a pure-mock test all
satisfy line coverage while leaving the code effectively unverified.
Surviving mutants are reported as Error findings the rework loop must
address.

## What gets gated

For each item the auditor:

1. Computes the changed files via `git diff --name-only base...HEAD`,
   filtered by `FileExtensions` (default `.cs`) and `ExcludePathPrefixes`
   (defaults include `tests/`, `test/`, `.codeybox/`).
2. Hands the scoped file list to an injected `IMutationRunner`, which
   mutates that slice and re-runs the test suite per mutant. The runner
   is expected to **parallelise** per-mutant across cores and to abort
   stragglers when the wall-clock budget is exhausted.
3. Emits findings:
   - One Error per **surviving mutant** in changed code, citing
     `file:line` and the mutator kind (e.g. `ConditionalBoundary`).
   - One Error if the **changed-code mutation score** is below
     `ChangedCodeThresholdPercent` (default `80%`).
   - One Error if the **overall mutation score** has regressed beyond
     `RatchetTolerancePercent` versus the stored baseline.
4. On a green pass the baseline is raised to the current overall score.
   A failing audit never updates the baseline, so partial wins cannot
   silently lower the bar.

## Configuration

`appsettings.json`:

```json
{
  "CodeyBox": {
    "Mutation": {
      "Enabled": true,
      "ChangedCodeThresholdPercent": 85,
      "BudgetMinutes": 15,
      "RatchetTolerancePercent": 0.5,
      "FileExtensions": [".cs"],
      "ExcludePathPrefixes": ["tests/", "test/", ".codeybox/"]
    }
  }
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `Enabled` | `false` | Master switch. Off by default — opt in per project. |
| `ChangedCodeThresholdPercent` | `80` | Minimum kill score on changed code. Aim high; the changed slice is small. |
| `BudgetMinutes` | `15` | Wall-clock cap on the runner. The runner aborts stragglers, the auditor does not hard-kill. |
| `RatchetTolerancePercent` | `0.5` | Absolute % points of noise allowed before a regression is reported. |
| `FileExtensions` | `[".cs"]` | Extensions kept in the changed-file scope. |
| `ExcludePathPrefixes` | `["tests/", "test/", ".codeybox/"]` | Prefixes dropped from the scope. |
| `RatchetKey` | derived from `<ProjectId>:<BaseBranch>` | Override the per-baseline ratchet lookup key. Multi-project hosts must rely on the project-id prefix to keep baselines distinct (or set this explicitly per project). |
| `Stryker` | see below | Stryker.NET engine configuration. Disabled by default. |

### Stryker engine opt-in (`CodeyBox:Mutation:Stryker`)

```json
{
  "CodeyBox": {
    "Mutation": {
      "Enabled": true,
      "Stryker": {
        "Enabled": true,
        "ExpectedVersion": "4.16.0",
        "ToolCommand": ["dotnet", "stryker"],
        "Concurrency": 2,
        "MutationLevel": "Standard",
        "Configuration": "Debug",
        "MaxReportBytes": 8388608,
        "MaxProjectsPerRun": 8,
        "MaxTestProjectsPerProject": 8
      }
    }
  }
}
```

Both switches must be on: `CodeyBox:Mutation:Enabled` arms the gate and
`CodeyBox:Mutation:Stryker:Enabled` selects the real engine. Everything
else keeps working with the inert default, so enabling the gate without
the engine still warns instead of pretending to measure.

| Key | Default | Description |
|-----|---------|-------------|
| `Enabled` | `false` | Selects the real Stryker runner. Stays off until the operator opts in. |
| `ExpectedVersion` | `"4.16.0"` (pinned) | Tool version provisioned in the audit sandbox baseline. A detected mismatch fails the run with a provisioning diagnostic instead of scoring under unvalidated semantics. |
| `ToolCommand` | `["dotnet", "stryker"]` | Argv prefix used to launch Stryker inside the sandbox (array elements only, never a shell string). May be a provisioned absolute shim path. |
| `Concurrency` | `0` (Stryker default) | Stryker `--concurrency` worker count. |
| `MutationLevel` | `"Standard"` | Stryker `--mutation-level`: Basic, Standard, Advanced, Complete. |
| `Configuration` | `"Debug"` | Stryker `--configuration`, passed explicitly for determinism. |
| `MaxReportBytes` | `8388608` | Largest JSON report accepted; larger reports fail closed as unusable. |
| `MaxConsoleBytes` | `1048576` | Per-stream console capture cap (never kills the run, only bounds the retained tail). |
| `DiscoveryMaxDepth` / `MaxDiscoveredProjects` | `6` / `256` | Bounds for csproj auto-discovery. |
| `MaxChangedFiles` | `256` | Changed files accepted per audit; more fail closed. |
| `MaxProjectsPerRun` / `MaxTestProjectsPerProject` | `8` / `8` | Caps; overflow fails closed rather than silently dropping projects. |
| `ProbeTimeoutSeconds` | `60` | Timeout for control-plane probes (tool presence, discovery, file reads). |
| `Projects` | `[]` (auto-discover) | Explicit `[{ Project, TestProjects }]` selection. Non-empty skips discovery; every path is validated like a discovered one. |

## Engine: Stryker.NET (`StrykerMutationRunner`)

`StrykerMutationRunner` is the concrete `IMutationRunner` backed by the
pinned Stryker.NET tool (currently `4.16.0`, the version this integration
was built and tested against). It runs **inside the existing
credential-free audit sandbox** through the supported process abstraction
— argv arrays only, never shell strings — and reuses the sandbox's tool
provisioning: Stryker must already be provisioned into the audit sandbox
baseline image. The runner never installs tools, never executes operator
installation hints, and needs no coding-agent turn.

Per audit it:

1. Probes the pre-provisioned tool (`--help` must exit 0; exit 126/127
   fails with a provisioning diagnostic naming the binary, the expected
   version, and the baseline-image fix).
2. Selects projects deterministically: every production csproj owning a
   changed file runs with **all** test projects that reference it (sorted,
   capped; explicit `Stryker:Projects` overrides skip discovery). Changed
   test code never generates mutate patterns.
3. Runs one Stryker invocation per project group from the project-under-
   test directory (`-p`, repeated `-tp`, repeated `-m` for the validated
   changed paths, `-r Json`, `--skip-version-check`, explicit level /
   configuration / `break-at 0`), under the shared wall-clock budget with
   bounded console/report sizes. Caller cancellation stops child
   processes (via exec cancellation plus `KillActiveExecsAsync`) and
   cleans the transient output directories; budget exhaustion fails as a
   timeout, never as scores.
4. Parses **only** the machine-readable `mutation-report.json` (fresh
   per-run output directory, size-capped, staleness-guarded) into
   killed/surviving/uncovered/timeout/error buckets with changed-code
   scores and surviving-mutant file/line details. Console text and exit
   codes never establish scores: exit 0 without a report, a threshold
   signal without a parseable report, and truncated/oversized reports are
   all fail-closed `Report` failures. Build, test, tool, and timeout
   failures are distinguished so the finding names the real cause.

### Requirements and provisioning

- `.NET SDK` able to build the repository, on `PATH` in the audit sandbox.
- `dotnet-stryker` at the pinned version, baked into the audit sandbox
  baseline image (e.g. `dotnet tool install --global dotnet-stryker
  --version 4.16.0` during image bake). The runner verifies the running
  version from Stryker's banner on every invocation.
- The tree must already be restored: Stryker builds in the sandbox, and
  the credential-free sandbox has no package-registry egress beyond what
  the warm NuGet cache provides. A cold cache surfaces as a build
  failure, not as scores.

### Scoped-run honesty (read this before trusting the gate)

The runner scopes mutation to the validated changed paths, so its report
carries a changed-code score and **no overall-project score**
(`OverallMutationScorePercent: null`,
`Scope: ChangedFilesOnly`). The auditor therefore:

- never compares a scoped run against the baseline,
- never advances the baseline on a scoped run (pass or fail),
- keys baselines by engine-configuration digest, so runs under different
  tool versions/flags/selections never compare against each other.

A changed-files-only run does NOT establish an overall-project score —
the gate verdict rests on the changed-code score and surviving mutants,
plus an explicit Info finding saying the overall baseline was untouched.

Report retention: the full JSON report lives in a transient sandbox
directory that is always cleaned up (even on cancellation). What is
retained — in the audit `RawOutput` — is the identified artifact record:
tool version, exact source SHA (`git rev-parse HEAD`, or `unknown`),
project selection, configuration digest, per-report byte size and SHA-256,
counts, scores, and a bounded console tail.

### Explicit no-evidence outcomes

- No `.csproj` discovered → `UnsupportedProject` (pass, Info).
- Changes confined to test-only / unmapped / non-mutable files →
  `NoApplicableCode` (pass, Info).
- Changed production code with no covering test project →
  `NoCoveringTests` (**Error, blocks the merge** — untested production
  code is what the gate exists to catch).

Sandbox-provisioning deferrals, execution-transport loss, and caller
cancellation propagate to the pipeline; they are never converted into
findings or passes.

## Wiring a runner

The auditor ships with `NullMutationRunner`, an inert default that lets
the auditor be registered in DI without an engine. When the auditor is
enabled but only the null runner is wired it emits a non-blocking
`Warning` finding rather than silently green-lighting unknown coverage.
Set `CodeyBox:Mutation:Stryker:Enabled: true` (plus the gate's own
`Enabled`) to select the built-in Stryker engine — see above — instead of
wiring a custom runner. Custom `IMutationRunner` implementations remain
supported: register yours in DI and the gate's composition
(`MutationGateRegistration`, exercised by
`MutationGateRegistrationTests`) keeps working.

The runner returns a `MutationRunReport`:

```csharp
public sealed record MutationRunReport(
    double? ChangedCodeMutationScorePercent,
    double? OverallMutationScorePercent,
    IReadOnlyList<SurvivingMutant> SurvivingMutantsInChangedCode,
    TimeSpan Duration,
    string? RawOutput = null,
    MutationRunStatus Status = MutationRunStatus.Completed,
    /* ...provenance: ToolVersion, SourceCommitSha, ProjectSelection,
       ConfigDigest, Scope ... */);
```

Null scores mean "no evidence for that scope" — never a fabricated
value. `SurvivingMutantsInChangedCode` is the list the rework prompt
cites per-mutant; runners MUST only include mutants whose source file is
in the `changedFiles` argument so the gate stays scoped.

## Cost & runtime budget

Mutation testing is expensive. Three properties keep this gate viable:

- **Scoped to changed code.** The auditor only ever asks the runner to
  mutate the diff'd files; whole-repo mutation runs are not used per
  item. A typical work item touches 1-5 files, so the per-mutant set
  stays in the tens-to-hundreds.
- **Parallelised in the runner.** The runner is expected to launch
  mutants across cores. The wall-clock budget defaults to 15 minutes;
  raise it on slow test suites and lower it on hot ones.
- **Ratchet-only on green.** The baseline is only persisted on a passing
  audit, so the next item compares against a meaningful floor instead of
  a partial run.

If audits routinely hit the budget, either:

1. Raise `BudgetMinutes` to fit the actual runtime, or
2. Narrow `FileExtensions` / broaden `ExcludePathPrefixes` so the scope
   shrinks, or
3. Lower `ChangedCodeThresholdPercent` temporarily while the test suite
   is brought up to standard. The ratchet still prevents regression
   below the highest-passing score.

## Acceptance behaviour (what tests cover)

- A surviving mutant in changed code is flagged as an Error finding the
  rework loop must address.
- The same code, given a real assertion that kills the mutant, passes.
- The overall score is ratcheted — a regression beyond
  `RatchetTolerancePercent` is reported as an Error.
- A failing audit does not lower the baseline (the ratchet only updates
  on a passing audit).
- Threshold, file scope, exclude scope, budget, and tolerance are all
  config-driven.
- Stryker engine (see `StrykerMutationIntegrationTests`,
  `StrykerMutationRunnerTests`, `StrykerReportParserTests`,
  `MutationTestingAuditorStrykerTests`, `MutationGateRegistrationTests`):
  the real pinned tool in the real sandbox path proves a nonzero mutant
  set with the expected survivor on weak tests and its death on
  strengthened tests, with changed-file mapping and report provenance;
  invalid/missing/truncated/oversized/stale reports, missing tool,
  version mismatch, no applicable code, build/test failure, threshold
  exit, timeout/cancellation with process cleanup, hostile paths,
  multi-project selection, partial-vs-overall semantics (no baseline
  movement on scoped runs, digest-isolated baselines), and
  default-disabled vs enabled composition.

See `tests/CodeyBox.Tests/MutationTestingAuditorTests.cs`.
