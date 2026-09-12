# Regression test selection (`csharp:test-pass`)

The `csharp:test-pass` audit can narrow `dotnet test` to the tests a change
may affect. Narrowing ships **advisory/shadow only**: the selector computes the
subset it *would* run, the **full suite still runs**, and a shadow record
captures whether any deselected test failed. Real skipping is gated on
accumulated shadow data showing zero unsafe skips — no ticket in this sequence
skips a test.

## Modes (`Audit:TestSelection:Mode`)

| Mode | Behaviour |
|------|-----------|
| `all` (default) | Full suite; the emitted command is byte-identical to the legacy path. Instant kill-switch: hot-reloading back to `all` disables all selection. |
| `coverage-shadow` | Coverage selector computes the would-be subset; full suite still runs; one structured `test-selection shadow` log line is emitted per run. |

The value is case-insensitive (`coverage_shadow` also parses) and hot-reloads
via `IOptionsMonitor`. An unrecognised value fails fast at load.

## Selectors

- **Project-graph** (`ProjectGraphTestSelector`): maps each changed file to
  its owning MSBuild project (via the baseline's `file → project` map, built
  by walking the project-reference graph) and selects the baseline's
  precomputed affected tests, plus tests defined in the changed files.
  A change owned by an ALWAYS-FULL project forces the full suite (see below).
- **Coverage** (`CoverageTestSelector`): selects tests whose recorded per-test
  coverage intersects the changed lines, and ALWAYS also selects tests defined
  in changed files, tests with NO coverage record (new/uninstrumented), and
  everything the project-graph selector picks. Coverage only refines WITHIN
  that superset — the result is a union, never less.

Both fall back to the full suite on ANY uncertainty: no/unknown changeset, no
baseline, stale baseline, global targets (`Directory.Build.*`,
`Directory.Solution.*`, `Directory.Packages.props`, `global.json`,
`NuGet.Config`, `appsettings*.json`, `CodeyBox.slnx`,
`.github/workflows/` — exact matches, plus `Audit:TestSelection:Coverage`
knobs), changes owned by an ALWAYS-FULL project
(`Audit:TestSelection:Coverage:AlwaysFullProjects` — defaults:
`src/CodeyBox.Core/CodeyBox.Core.csproj`, `tests/CodeyBox.Tests/CodeyBox.Tests.csproj`;
extend with source-generator projects or other shared roots), whole-file
changes, changed test files (which may define unrecorded
tests), or files no record references. Running more tests is always safe.

## Baseline artifact (per-test coverage map)

`Audit:TestSelection:Coverage:BaselineSandboxPath` (default
`/opt/codeybox/test-selection/baseline.json`) points at the sandbox-side JSON
artifact `codeybox-test-selection-baseline/1`:

```json
{
  "format": "codeybox-test-selection-baseline/1",
  "commit": "<main HEAD sha>",
  "producedAtUtc": "<timestamp>",
  "fileProject": { "src/Foo/Bar.cs": "src/Foo/Foo.csproj" },
  "projects": { "src/Foo/Foo.csproj": ["Ns.Foo.BarTests"] },
  "tests": {
    "Ns.Foo.BarTests": {
      "file": "tests/Foo.Tests/BarTests.cs",
      "covers": { "src/Foo/Bar.cs": [10, 11, 12] }
    }
  }
}
```

**Producer (operational):** the mandatory full-suite-on-`main` run — after
every merge — collects per-test XPlat/Cobertura coverage, parses it with the
same `CoberturaParser` executable-line semantics as the diff-scoped coverage
gate (`tests:coverage`), normalises paths with `ToRepositoryRelative`, joins
`dotnet sln` / project-reference graph data and `dotnet test --list-tests`
enumeration (including each test's defining file), and writes this file. This
ticket ships the CONSUMER only; it builds no new coverage tooling.

**Distribution:** bake the file into the audit baseline image at the path
above, OR fetch the CI artifact to that sandbox path at sandbox setup.

**Staleness bound:** regeneration on every merge to `main` means the map is at
most **one merge stale**. `MaxBaselineAge` (default 7 days) is an additional
age backstop for a quiet `main`; a stale/missing/unparseable baseline (or a
commit mismatch, when the current commit is known) falls back to the full
suite. Size caps (`MaxBaselineBytes`, `MaxBaselineTests`,
`MaxBaselineCoveredLines`) bound the untrusted artifact before buffering.

## Structural invariants

- **SHADOW-BEFORE-ENFORCE** — `DotnetTestAuditor` executes
  `BuildInvocation(TestSelection.All, …)` on every run; the narrowed
  `--filter` argv is computed for the shadow record only, never executed.
- **FULL-SUITE-ON-MAIN** — the merge/release path (`IRequiredBuildVerifier` /
  `process:required-build`) takes no `ITestSelector` dependency and always
  runs everything, enforced in code (see `TestSelectorTests`), not config.
- **FAIL-SAFE** — selector errors, missing/stale data, and global-target
  touches all resolve to the full run.

## Shadow records (the shared validation harness)

Each shadow run emits one `TestSelectionShadowRecord` through
`ITestSelectionShadowSink` (production: structured log; tests: in-memory)
with the verdict `safe-for-this-run` | `unsafe-skips-observed` |
`full-suite` | `unverifiable`, the deselected set, the full run's failures,
and their intersection (the unsafe skips). This is the shared harness every
selector reports through; the enforcement gate for real skipping consumes
these records and does not exist yet.

## Per-run telemetry (audit report + dashboard)

Every `csharp:test-pass` invocation — shadow AND full-suite — records a
`testSelection` block on its audit report (`audit_reports.test_selection_json`,
served as `testSelection` on each auditor in
`GET /workitems/{id}/audit-reports`, rendered on the Audit Reports and
Timeline dashboard pages):

| Field | Meaning |
|-------|---------|
| `mode` | Live selection mode (`All`, `CoverageShadow`). |
| `selector` | Selector that decided (`coverage`, or `none` when the shadow was inactive). |
| `layers` | Layers consulted, innermost first (`["project-graph","coverage"]` for the coverage selector, which refines the project-graph superset). |
| `selectedCount` / `totalCount` | WOULD-BE subset / known universe size. `0/0` means the universe was unknown (no baseline) — the dashboard shows "full suite". For a full-suite fallback with a known universe, both equal the universe size. |
| `estimatedSavedFraction` | Proportional estimate: deselected / total in [0,1]. The dashboard multiplies it by the run's `durationMs` (`est. saved 62.5% (~75s)`). Zero for full-suite runs. This is an estimate, not a measurement — the full suite always ran. |
| `assessment` | Shadow verdict (`safe-for-this-run` \| `unsafe-skips-observed` \| `full-suite` \| `unverifiable`). |
| `fallbacks` | Which fallback-ladder rungs fired (e.g. `no per-test coverage baseline is available`). Empty when the selector narrowed without falling back. |
| `detail` | Operator-facing selection detail (capped at 4000 chars). |

See `tests/CodeyBox.Tests/CoverageTestSelectionTests.cs` and
`docs/coverage.md` (aggregate gate) / `docs/quality/audit.md`
(`DotnetTestAuditor`).
