# CodeyBox: Brakeman Rails SAST Auditor

Auditor plugin wrapping [Brakeman](https://brakemanscanner.org/)
(`brakeman`): it runs Brakeman's Ruby on Rails security checks over the
audited repository (`brakeman --format sarif --output /dev/stdout --path
.`), reporting each warning as an audit finding with the Brakeman rule id
(e.g. `BRAKE0018` for a SQL injection warning) and `file:line` location.

Each run is a single tool invocation: the analysis streams SARIF to stdout
for parsing. A scan failure (missing binary, no Rails application detected,
scan errors) is infrastructure — never a pass.

## What it reports

- One finding per Brakeman warning. The title carries the rule id and the
  first line of the warning message; the description carries the tool, rule,
  tool-reported severity, location, and the full message. `Location` is
  `path:startLine`; paths are relative to the repository root (the scan runs
  with the worktree as its application path, so report URIs stay
  repo-relative).
- **Gate behaviour: hybrid / severity-driven — not blocking on every
  finding.** Brakeman confidences go through a declared map, never raw:
  `High` (plus SARIF `error`) → `Error` (fails the audit); `Medium` (plus
  SARIF `warning`) → `Warning` (advisory); `Weak` (plus SARIF
  `note`/`none`) → `Info` (informational); anything unrecognised →
  `Warning`. `MinimumSeverity` can only drop findings, it never raises
  them. The auditor is therefore a merge gate for high-confidence Rails
  vulnerabilities, not a blocker on every weak hint.
- SARIF includes the warning message and location per result; findings and
  raw output contain the reported source context. That is the point of a
  SAST finding; treat audit reports accordingly.

## What it cannot see

- **Anything outside a Rails application.** Brakeman analyzes Rails
  conventions (controllers, models, views, routes). A repository with no
  Rails application exits `4` — infrastructure, not a pass. Scope this
  auditor to Rails projects.
- **Anything the default checks don't cover.** Brakeman is Rails-aware
  pattern and dataflow analysis, not a general Ruby linter: plain-Ruby
  libraries without Rails structure get thinner coverage, and checks outside
  the default set run only with `-A`/`-E` via `ExtraArguments`.
- **Ignored warnings, by default.** The scan passes `--show-ignored`, so
  entries in the repository-authored ignore file
  (`config/brakeman.ignore`) stay visible in the report (marked with SARIF
  `suppressions`) instead of silently disappearing. Operators who
  deliberately trust repo-authored suppression set
  `TrustRepositorySuppression` in scoped config.
- **Repository config files fail closed, they are not honored blindly.**
  Brakeman reads `config/brakeman.yml` and `config/brakeman.ignore` from
  the audited tree when present — files the audit subject controls. There
  is no CLI switch that disables the ignore file (hence `--show-ignored`
  above). A present `config/brakeman.yml` fails the run closed as
  infrastructure instead: its options can skip checks and paths, so
  honoring it would let the subject shape the gate. Remove the file, or set
  `TrustRepositorySuppression` to trust it. An operator-owned file passed
  with `-c`/`--config-file` or `-i`/`--ignore-config` via `ExtraArguments`
  is canonicalized in the sandbox and rejected when it resolves inside the
  audited worktree — and it does not lift the `config/brakeman.yml` gate,
  because the tool only prefers the operator file when it names an existing
  file.
- **Findings under excluded prefixes.** `ExcludePaths` is a finding filter —
  Brakeman still walks those files (except `vendor/`, which it skips at
  scan time unless `--skip-vendor` is negated), but findings under
  `vendor/`, `third_party/`, `node_modules/` are dropped. Override
  `ExcludePaths` to re-include them.
- **The exit-code overrides are rejected, not just discouraged.** The
  auditor relies on the default exit convention (exit `3` means warnings
  were found, exit `7` means the scan errored). `--no-exit-on-error` in
  `ExtraArguments` is rejected deterministically: scan errors would
  otherwise masquerade as a clean verdict instead of failing closed as
  infrastructure. `-f`/`--format` and `-o`/`--output` are rejected for the
  same reason — they would replace or divert the SARIF report the parser
  expects. Negating `-z`/`--exit-on-warn` changes nothing the parser sees
  (exit `0` is still findings-producing), so it is left alone.

## Exit codes and failure classification

Brakeman's convention (verified against the 8.0.6 source —
`lib/brakeman.rb` exit-code constants and
`lib/brakeman/commandline.rb` — do not assume the gitleaks/eslint
convention holds here):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Scan completed, no warnings | Verdict (pass when the SARIF document is empty) |
| `3` | Scan completed, warnings found (`Warnings_Found_Exit_Code`) | Verdict (`Passed = false` when any finding maps to `Error`) |
| `7` | Scan errors encountered (`Errors_Found_Exit_Code`) | Infrastructure (`AuditUnavailableException`) |
| `4` | No Rails application detected (`No_App_Found_Exit_Code`) | Infrastructure (`AuditUnavailableException`) |
| `6` | Unknown check names (`Missing_Checks_Exit_Code`) | Infrastructure (`AuditUnavailableException`) |
| `5` / `8` / `9` | Version/ignore-note bookkeeping | Infrastructure (`AuditUnavailableException`) |
| `-1` | Invalid options | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

Only `0` and `3` are findings-producing. A missing `brakeman` is always an
infrastructure failure naming the tool — never a passing audit.

## Version pinning

The auditor is pinned to **Brakeman `8.0.6`** (`ExpectedVersion` in scoped
config). A scanner's checks change between releases, so an unpinned tool
would change findings under you: the auditor probes `brakeman --version`
before every run and reports an infrastructure failure on any other
version.

The tool requirement is declared **verify-only** — no `AptPackage`:
Brakeman ships as a Ruby gem, not a distro package, and only a pinned
`gem install` carries the version this auditor was verified against.
Provision the pinned release in your sandbox baseline:

```sh
# baseline bake step (needs Ruby with rubygems on the image)
BRAKEMAN_VERSION=8.0.6
gem install brakeman --version "${BRAKEMAN_VERSION}" --no-document
brakeman --version   # must print 8.0.6
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.brakeman"],
      "Enabled": ["codeybox.brakeman"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.brakeman" }
```

The `brakeman` tool requirement is only contributed to baseline provisioning
while the plugin is enabled.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.brakeman`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `8.0.6` | Pinned Brakeman release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `TrustRepositorySuppression` | `false` | When `true`, omits `--show-ignored` so repo-authored `config/brakeman.ignore` entries silently suppress findings, and lifts the fail-closed gate on a repo-authored `config/brakeman.yml`. Default keeps ignored warnings visible and fails closed on the config file. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact Brakeman rule ids to keep/drop (e.g. `BRAKE0018`). Output filters; they do not change which checks run. To run or skip checks by name (e.g. `-t SQL`, `-x Redirect`), use `ExtraArguments`. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended to `brakeman` after the built-in args (never via a shell). `--no-exit-on-error` (scan errors would masquerade as a clean verdict) and `-f`/`--format`/`-o`/`--output` (would replace or divert the SARIF report the parser expects) are rejected deterministically. `-c`/`--config-file` and `-i`/`--ignore-config` values are canonicalized in the sandbox and rejected when they resolve inside the audited worktree. |
| `TimeoutSeconds` | `300` | Per-run bound — exceeding it is infrastructure, not a pass. A Rails-app scan is normally seconds; raise it only for very large trees. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. Large applications can exceed 1 MiB of SARIF — raise the former (up to 64 MiB) rather than wondering where findings went. |

## Default scope

Vendored and dependency trees (`vendor/`, `third_party/`, `node_modules/`)
are excluded by default: warnings reported there belong to upstream
packages, not the change under audit, and the noise would teach operators
to ignore the auditor. The exclusion is a finding filter — Brakeman still
walks those paths (except `vendor/`, which it skips at scan time by
default). Re-include them by overriding `ExcludePaths`.
