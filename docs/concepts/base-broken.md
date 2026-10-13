# Base-broken containment (build-red and test-red)

## The gap

Merge-result verification builds the combined tree before landing, but two
changes that each passed their full audit on their own base can still combine
into a tree whose **tests** fail — and that tree lands on main. The build
check passes because the combined tree compiles.

## Design choice

Of the available closes, this codebase implements **post-merge red detection
+ test attribution**, and deliberately defers **pre-landing affected-test
runs** on the merged tree:

- **Post-merge CI watch** (`PostMergeCiWatchService`): every landed merge
  schedules a bounded poll of the merge commit's forge CI status
  (`IUpstreamRemote.GetCheckResultsAsync`). A red tip immediately records a
  test-kind condition and files one top-priority fix item naming the failing
  checks and the suspect commits. This reuses the verdict CI already
  computes — one status poll per interval per recent merge.
- **Test attribution** (`BaseBrokenTestClassifier`): an audit iteration whose
  blocking findings are test-only, with every failure genuinely reproducing
  on the base, is adopted by an active condition (main is recorded red) or
  recorded against a forge-red tip — the item parks under the hold, never
  fails, and is never blamed.
- **Deferred: pre-landing affected-test runs.** Running the affected subset
  on the merged tree before every landing duplicates CI cost on the hottest
  path, and a flaky selected test would block landings that CI would clear
  on retry. The test-selection project graph / coverage baseline stays
  available if a future pre-landing gate is wanted; the watch + attribution
  pair closes the correctness gap (red main is detected, held, and repaired)
  without the landing-latency and flake-blocking costs.

## Semantics

- Conditions have a `FailureKind`: `Build` (tip fails the required build)
  or `Test` (tip builds but its tests fail). The dispatcher hold, SHA-keyed
  dedupe, and one-fix-item-per-SHA rule are identical for both kinds.
- Clear signals differ on purpose: a build-kind condition clears when a
  moved tip builds; a test-kind condition clears only when the tip's forge
  CI passes. A green build alone must never clear a test-red hold — that is
  exactly the gap above.
- Fix items are parked for operator approval (`NeedsOperatorInput`, never
  auto-enqueued). Test names and check names are untrusted test-runner/forge
  output: they travel sanitized, bounded, and instruction-scrubbed; the full
  list lives on the operator-held note, with counts and opaque SHAs in the
  agent prompt. The agent identifies targets by running the suite on the
  base itself.
- Unknown forges (or SHAs the forge never saw, e.g. local-only squash SHAs)
  resolve CI to "unknown": the watch drops and detection falls back to the
  audit-time check. Unknown never records and never clears.

## Knobs (`CodeyBox:PipelineTuning`)

`BaseBrokenDetectionEnabled` (master switch),
`BaseBrokenFixItemPriority`, `BaseBrokenRecheckInterval`,
`PostMergeCiWatchEnabled`, `PostMergeCiWatchInterval`,
`PostMergeCiWatchMaxAttempts`, `PostMergeCiWatchMaxWatches` — all
hot-reloadable. See `docs/reference/configuration.md`.
