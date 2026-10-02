# CodeyBox: Depcruise JS/TS dependency architecture rules

Auditor plugin wrapping
[dependency-cruiser](https://github.com/sverweij/dependency-cruiser): it
cruises the audited JavaScript/TypeScript repository with
`depcruise --output-type json --progress none .` and reports each violated
dependency rule as an audit finding with the rule name as the rule id and
the violating `from` module as the location.

## What it reports

- One finding per entry in the report's `summary.violations` array. The
  title carries the rule name (the rule id) and a short message
  (`Forbidden dependency: 'src/b.js' → 'src/a.js'`, `Circular dependency:
  'src/a.js' → 'src/b.js' (cycle: …)`, `Module violation: 'src/lonely.js'`
  for orphan-style module rules); the description carries the tool, rule,
  tool-reported severity, location, and the full message. `Location` is the
  violation's `from` module — the file holding the forbidden dependency.
  dependency-cruiser reports no line numbers for violations, so findings
  carry a path without a line.
- **Gate behaviour: blocking for error-severity rule violations, advisory
  below that.** Rules with severity `error` map to `AuditSeverity.Error`
  and fail the audit; `warn` maps to `AuditSeverity.Warning` and
  `info`/`ignore` map to `AuditSeverity.Info` — advisory only.
  `MinimumSeverity` only drops findings; it never raises them.

## What it cannot see

- **Repositories with no dependency-cruiser configuration.** Rules come
  from `.dependency-cruiser.js` / `.cjs` / `.mjs` / `.json` (or the file
  named by `--config`) in the audited repo — `depcruise` exits 1 with
  "Can't open … Does it exist?" when none exists, which the auditor reports
  as **infrastructure**, never as a pass. Enabling this plugin requires a
  ruleset in the repository, or an operator-pinned file via `ConfigPath`.
- **Line numbers.** Violations name modules, not lines — a finding points
  at the file, not the offending import statement.
- **Non-JS/TS files.** dependency-cruiser analyses JavaScript, TypeScript,
  and the module systems it supports (ES6, CommonJS, AMD, TypeScript
  definitions); other languages are invisible to it.
- **Files depcruise never walks.** Its `exclude`, `doNotFollow`, and
  built-in `node_modules` handling decide the cruised set.
- **Findings under excluded prefixes.** `ExcludePaths` is a finding
  filter — depcruise may still report those modules, but findings under
  `vendor/`, `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`,
  `coverage/` are dropped. Override `ExcludePaths` to re-include them.
- **Suppression authored in the repository.** Per-rule `ignore` entries,
  `--ignore-known` baselines (`.dependency-cruiser-known-violations.json`),
  and the config file itself are repo-authored — the project's own
  dependency contract, same posture as knip's repo `knip.json`. For an
  operator-owned gate, pin an out-of-repo config via `ConfigPath`.
- **More than `MaxFindings` violations.** Findings beyond `MaxFindings`
  (default 1000) are dropped and the truncation is reported in the raw
  output.

## Exit codes and failure classification

depcruise's convention (verified against dependency-cruiser 18.4.0 source
and CLI — **not** the common "0 clean / 1 findings / 2 error" table; the
JSON reporter does not follow it):

| Exit | Meaning | Classification |
|---|---|---|
| `0` + JSON report with violations | Completed run; rules violated | Verdict (`Passed = false` when any finding maps to `Error`) |
| `0` + JSON report, empty violations | Completed run; no violations | Verdict (pass) |
| `1` + JSON report on stdout | Completed run (defensive: the JSON reporter always exits 0, but a report is a report) | Verdict |
| `1`, no JSON on stdout | Could not run: missing/unreadable config file, unreadable cruise target, usage error — all rendered as plain error text | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention (e.g. a human-facing reporter exiting with an error count, if an operator override ever selects one) | Infrastructure (fails loud, never a pass) |

A missing `depcruise` is always an infrastructure failure naming the tool —
never a passing audit.

Source anchors for the table: `src/report/json.mjs` reports `exitCode: 0`
on every completed run, while `src/report/error.mjs` (the default
human-facing `err` reporter, which this auditor never selects) exits with
the error count — which is why the exit code alone cannot classify a run
and the parser carries the classification instead.

## Version pinning

The auditor is pinned to **dependency-cruiser `18.4.0`**
(`ExpectedVersion` in scoped config). Rule implementations and the JSON
report shape change between releases, so an unpinned scanner would change
findings under you: the auditor probes `depcruise --version` before every
run and reports an infrastructure failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`:
dependency-cruiser ships via npm and no distro package carries a version
pin. Provision the pinned release in your sandbox baseline **only when this
plugin is enabled**:

```sh
# baseline bake step (needs Node.js 22+ on the image — engines: ^22||^24||>=26)
npm install -g dependency-cruiser@18.4.0
depcruise --version   # must print 18.4.0
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates, and baseline provisioning installs `depcruise` only in that state:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.depcruise"],
      "Enabled": ["codeybox.depcruise"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.depcruise" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.depcruise`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `18.4.0` | Pinned dependency-cruiser release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | `null` | Path passed to `--config` — an operator-pinned dependency-cruiser config outside the repository, or a repo file overriding the default lookup (`.dependency-cruiser.js`, `.cjs`, `.mjs`, `.json`). Ignored when `ExtraArguments` already supplies `--config`/`-c`. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact rule names to keep/drop. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--exclude`, `--include-only`, `--focus`, or `--ts-config`. A repeated `--output-type`/`-T` would replace the JSON report the parser expects and break the run into infrastructure failure. |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Default scope

`depcruise … .` — the whole audited repository is cruised, and the
repository's own dependency-cruiser config decides which modules and rules
apply. Progress reporting is pinned off (`--progress none`) so only the
JSON report lands on stdout, and the scan never passes `--no-config`:
cruising without rules reports zero violations, which would manufacture a
pass. On top of the tool's own `exclude`/`doNotFollow` handling, findings
under vendored (`vendor/`, `third_party/`, `node_modules/`) and generated
(`dist/`, `build/`, `out/`, `coverage/`) prefixes are dropped by default:
violations there belong to upstream packages or build output, not the
change under audit — reporting them produces noise that trains operators
to ignore the auditor. Re-include a prefix by overriding `ExcludePaths`.
