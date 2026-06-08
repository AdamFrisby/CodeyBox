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
| `RatchetKey` | derived from `BaseBranch` | Override the per-baseline ratchet lookup key. |

## Wiring a runner

The auditor ships with `NullMutationRunner`, an inert default that lets
the auditor be registered in DI without an engine. When the auditor is
enabled but only the null runner is wired it emits a non-blocking
`Warning` finding rather than silently green-lighting unknown coverage —
register your real `IMutationRunner` (Stryker / Mutmut / mull / …) in DI:

```csharp
builder.Services.AddSingleton<IMutationRunner, MyStrykerMutationRunner>();
builder.Services.AddSingleton<IMutationRatchetStore, InMemoryMutationRatchetStore>();
builder.Services.AddSingleton<IAuditor>(sp =>
    new MutationTestingAuditor(
        sp.GetRequiredService<IOptions<MutationTestingAuditorOptions>>().Value,
        sp.GetRequiredService<IMutationRunner>(),
        sp.GetRequiredService<IMutationRatchetStore>()));
```

The runner returns a `MutationRunReport`:

```csharp
public sealed record MutationRunReport(
    double ChangedCodeMutationScorePercent,
    double OverallMutationScorePercent,
    IReadOnlyList<SurvivingMutant> SurvivingMutantsInChangedCode,
    TimeSpan Duration,
    string? RawOutput = null);
```

`SurvivingMutantsInChangedCode` is the list the rework prompt cites
per-mutant; runners SHOULD only include mutants whose source file is in
the `changedFiles` argument so the gate stays scoped.

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

See `tests/CodeyBox.Tests/MutationTestingAuditorTests.cs`.
