# codeybox-test-selection-baseline

Produces the `codeybox-test-selection-baseline/1` JSON artifact consumed by
coverage-guided regression test selection (`Audit:TestSelection:Mode =
coverage-shadow` / `coverage`). The same binary is what an orchestrator job
and a human run against a checkout.

```bash
dotnet run --project tools/CodeyBox.TestSelectionBaseline -- produce \
  --repo /path/to/checkout \
  --output /opt/codeybox/test-selection/baseline.json
```

`--output` is required. `--repo` defaults to the current directory. `--commit`
defaults to `git rev-parse HEAD`. Size caps default to the consumer knobs
(`CoverageTestSelectionOptions.MaxBaselineBytes` / `MaxBaselineTests` /
`MaxBaselineCoveredLines`); a cap miss fails the process and does **not**
write a truncated file.

## Chosen mechanism

**Isolated per-test coverlet XPlat runs** (`dotnet test --no-build --filter
FullyQualifiedName=<one test> --collect "XPlat Code Coverage"`).

Coverlet (`coverlet.collector`, MIT, already referenced by
`tests/CodeyBox.Tests`) emits one Cobertura document per test host session.
The collector has no per-test-case split, so the producer runs the existing
collector once per listed test after a single build, each into its own
`--results-directory`. Reports are parsed with `CoberturaParser` (the same
executable-line semantics as `tests:coverage`) and keyed with
`CoberturaParser.ToRepositoryRelative`. Only lines with a **positive** hit
count enter that test's `covers` map — a session report also lists uncovered
executable lines, and including those would make every test appear to cover
every instrumented line.

`--max-parallelism` (default: host CPU count) bounds how many test
**projects** are collected concurrently. Runs against one project's output
directory always serialize: coverlet writes module backup files next to the
instrumented assemblies and restores them at session end, so concurrent
sessions on the same `bin/` race (`BackupOriginalModule` throws, or a
crashed session leaves the assembly instrumented). `--list-tests` still
provides the universe, including tests that produce no coverage record
(empty `covers`).

### Measured wall-clock cost

Measured on this host (2 CPUs) against **this** checkout of CodeyBox.Tests
(15,950 listed tests via `dotnet test --no-build --list-tests`, 4.7 s).
Isolated coverlet collection is dominated by testhost + instrumentation
startup, not by the test body:

| Workload | Wall clock |
| --- | --- |
| `dotnet test --no-build --list-tests` on CodeyBox.Tests | **4.7 s** (15,950 names) |
| One isolated `--filter FullyQualifiedName=… --collect "XPlat Code Coverage"` after a Debug build (test body 47 ms / 37 ms) | **29.485 s**, then **29.318 s** on a second test |
| Tiny fixture used by `Producer_FixtureRoundTrip_ParsesWithStrictReader` (2 tests, restore + build + 2 isolated runs) | **~9 s** |
| Extrapolated full CodeyBox.Tests map | 15,950 × 29.4 s ≈ **130 h** |

`--max-parallelism` does **not** shorten the dominant cost: the audited
suite is a single test project (tests/CodeyBox.Tests), and per-test runs
inside one project serialize — coverlet races on its per-module backup
files. The solution does contain other test projects
(CodeyBox.Admin/.Admin.Model/.Cli tests); the producer discovers and
collects those too, and the knob helps only across projects.

This producer is a **post-merge orchestrator-host job**, not part of the
90-minute per-item `csharp:test-pass` budget. The isolated-run cost is the
price of staying on MIT coverlet and `CoberturaParser` without a second
coverage engine.

### Why the alternatives were rejected

**Microsoft.Testing.Extensions.CodeCoverage / `dotnet-coverage`.** The
Microsoft coverage engine is closed source (`microsoft/codecoverage`: "Microsoft
code coverage functionality is closed source") under the Microsoft .NET
Library licence — not copyleft, but not the MIT collector the coverage gate
already depends on, and it exposes **session** coverage (`--coverage`,
`--coverage-output-format cobertura`), not per-test-case maps. Switching the
producer onto a second engine would also drift from `CoberturaParser`'s
coverlet-shaped executable-line output.

**Coverlet in-process per-test hooks (`Coverlet.Core`).** `Coverage` /
`CoverageParameters` in Coverlet 6.0.4 are `internal`. There is no supported
reset-hits-between-tests API, and `GetCoverageResult()` restores original
modules (one-shot). A custom VSTest in-proc collector would either reflect
into internals or re-implement instrumentation — both are a second coverage
tool, which this producer is required not to be.

**Batched runs with batch size > 1.** A Cobertura document for a batch of
tests cannot be disaggregated into per-test `covers` maps. Hit counters
accumulate, so a delta between tests misses lines a later test also covered.
Only batch size 1 (this producer) yields per-test maps from session-scoped
coverlet.

**AltCover `--callContext`.** MIT, and it can tag sequence points with the
owning test in **one** instrumented run. Output is OpenCover-with-tracked-
methods, not the Cobertura `CoberturaParser` already understands. Using it
would mean a second coverage parser. Not one of the required candidates.

## What the artifact contains

Exactly the fields `TestSelectionBaselineParser` requires:

- `format`: `codeybox-test-selection-baseline/1`
- `commit`, `producedAtUtc`
- `fileProject`: repository-relative source → owning `.csproj`
- `projects`: project → tests affected when that project changes (the
  project plus its transitive ProjectReference dependents)
- `tests`: listed name → `{ file, covers }` (`file` from the portable PDB;
  `covers` from per-test Cobertura, possibly empty)

## Licence

coverlet.collector 6.0.4 is MIT. No commercial-tier Visual Studio coverage
and no copyleft-restricted tooling are required to run the producer.
