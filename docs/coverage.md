# Coverage gate (`tests:coverage`)

The coverage auditor is a deterministic, per-item **line-coverage gate**. For
the code CHANGED in a work item it asks a simple question:

> Does at least one test execute every executable line this branch added or
> modified?

Unlike the mutation gate (which measures *assertion strength*), this gate
measures *reachability*: a changed executable line that no test ever runs is
flagged. It is intentionally scoped to the diff — unchanged uncovered code is
never reported, so the list shrinks monotonically as rework adds tests.

## Pure function of (diff, tests)

The gate is **stateless / isolated per audit iteration** — it carries no memory
across iterations and reads no baseline. The finding list is fully determined by
the current diff and the current test run, so the same `(diff, tests)` input
always yields the identical, complete list every run. Determinism is what makes
the rework loop converge.

## What gets gated

For each item the auditor:

1. Computes the changed lines via `git diff --unified=0 --no-color
   <base>...HEAD` (three-dot merge-base range — the same "changes since the
   branch diverged" semantics a PR diff uses), preferring the `origin/<base>`
   ref and falling back to the local branch. A git-diff failure **fails
   closed** — "cannot determine the diff" is never treated as "nothing
   changed".
2. Skips (passes) when the tree has no .NET project markers or the diff changed
   no lines.
3. Runs the test suite with coverage:
   `dotnet test --no-build --collect "XPlat Code Coverage" --results-directory <dir>`
   into a fresh per-iteration results directory. `--no-build` reuses the build
   the compile gate already produced in the shared tool sandbox.
4. Parses the produced Cobertura report(s) — one per test assembly, merged with
   `max` hits — into `file → (line → hits)`. Only lines the tool considered
   **executable** appear; comments, blank lines, and braces do not.
5. Intersects: a changed line gates when it appears in the coverage report with
   **zero hits**. Changed non-executable lines and unchanged lines are ignored.
6. Emits one finding per uncovered changed line, with `file:line`.

If the test run fails, produces no report, or the report cannot be read/parsed,
the gate reports **unverifiable** rather than silently passing — a false green
on "we could not measure coverage" is exactly the honesty failure the gate
exists to prevent.

## Rollout mode

`CodeyBox:Audit:Coverage:Mode` controls how uncovered changed lines are treated:

| Mode | Uncovered changed line | Blocks merge? |
|------|------------------------|---------------|
| `report-only` (default) | `Info` finding | No |
| `blocking` | `Error` finding | Yes |

The default is `report-only` so operators can measure real uncovered-line
volume before the gate blocks merges; flip to `blocking` after calibration. An
unrecognised value fails **open** to `report-only` — a config typo can never
silently start blocking merges. The knob is hot-reloaded with the rest of the
options.

## Honesty: exclusions require a justification

Genuinely untestable changed lines (e.g. generated code) must be **explicitly
listed** with a justification — never silently skipped. Each applied exclusion
is logged as an `Info` finding so the skip is visible in the audit report.

```json
{
  "CodeyBox": {
    "Audit": {
      "Coverage": {
        "Mode": "blocking",
        "Exclusions": [
          {
            "File": "src/Generated/ApiClient.g.cs",
            "Line": 1,
            "LineEnd": 400,
            "Justification": "generated OpenAPI client; regenerated, not hand-tested"
          }
        ]
      }
    }
  }
}
```

- `File` — repository-relative path (matches the git diff / coverage path).
- `Line` / `LineEnd` — inclusive 1-based range (`LineEnd` optional; defaults to a
  single line).
- `Justification` — **required**. An exclusion with a blank justification does
  NOT suppress: the line still gates and a `Warning` surfaces the missing
  justification. An exclusion that matches no uncovered changed line is reported
  as unused so stale entries can be pruned.

## Configuration

| Key | Default | Description |
|-----|---------|-------------|
| `Mode` | `report-only` | `report-only` (non-blocking `Info`) or `blocking` (`Error`). |
| `Exclusions` | `[]` | Justified exemptions for untestable changed lines. |
| `TestCommand` | `["dotnet","test","--no-build"]` | Base test command; the collector and results-directory flags are appended. |
| `Collector` | `XPlat Code Coverage` | Coverlet data-collector name passed to `--collect`. |
| `MaxCoverageReportBytes` | `67108864` | Max bytes read from a single Cobertura report. |
| `MaxCoverageReportFiles` | `256` | Max number of report files read (one per test assembly). |

The test projects must reference `coverlet.collector` for
`--collect "XPlat Code Coverage"` to emit a report; if none is produced the gate
reports unverifiable rather than passing.

## Wiring

The gate is a code auditor (`Required = None`, `Kind = "tool"`), registered as an
`IAuditor` singleton and auto-included by `ProjectAuditorComposer` when
registered — the same pattern as `tests:mutation-rigor`. It runs in the
credential-free tool sandbox after the deterministic build/test gates. A project
opts out by listing `tests:coverage` under `ExcludedAuditors`.

## Acceptance behaviour (what tests cover)

- A changed line the tests execute passes; a changed line with zero hits is
  reported (`Error` in blocking mode, `Info` in report-only).
- A changed line excluded with a justification passes and is logged; an
  exclusion without a justification still gates and warns.
- An unchanged uncovered line is ignored — only diff lines gate.
- A missing/failed coverage run is reported as unverifiable, never a false pass.
- Mode, exclusions, and the test command are all config-driven and
  hot-reloadable.

See `tests/CodeyBox.Tests/CoverageAuditorTests.cs`,
`CoverageGateEvaluatorTests.cs`, `CoberturaParserTests.cs`, and
`UnifiedDiffParserTests.cs`.
