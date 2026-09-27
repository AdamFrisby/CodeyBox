# CodeyBox: RuboCop Ruby Linter

Auditor plugin wrapping [RuboCop](https://github.com/rubocop/rubocop): it analyses the
audited repository with `rubocop --format json --cache false
--ignore-disable-comments .` and reports each offense as an audit finding with the
cop name (e.g. `Style/FrozenStringLiteralComment`, `Lint/UselessAssignment`) and
`file:line` location. Ruby analysis only.

## What it reports

- One finding per RuboCop offense. The title carries the cop name and the
  first line of the message (e.g. "Style/FrozenStringLiteralComment: Missing
  frozen string literal comment."); the description carries the tool, cop,
  tool-reported severity, location, and the full message. `Location` is
  `path:startLine` from the offense's `location.start_line`. RuboCop reports
  scan-relative paths, so locations are repository-relative and the
  `ExcludePaths` filter matches them directly.
- **Gate behaviour: hybrid / severity-driven — not blocking on every
  finding.** RuboCop's severities go through a declared map, never raw:
  `error` and `fatal` → `Error` (fail the audit); `warning` → `Warning`
  (advisory); `convention`, `refactor`, and `info` → `Info` (advisory);
  anything unrecognised → `Warning`. Whether an offense is error or style
  comes from the configuration in force — the repo's `.rubocop.yml` by
  default. `MinimumSeverity` can only drop findings, so the intended way to
  harden the gate is the ruleset (cop `Severity` settings); an operator
  `--fail-level` likewise cannot silence it — findings mapped to `Error`
  still fail the audit even when the tool exits 0.

## What it cannot see

- **Non-Ruby files.** RuboCop checks the `AllCops: Include` set (`**/*.rb`,
  `*.gemspec`, `Gemfile`, `Rakefile`, and friends) — everything else
  produces no findings. A repository with no Ruby files yields an empty
  report (a pass), not an error.
- **Files RuboCop excludes.** RuboCop's own default excludes
  (`node_modules/**/*`, `tmp/**/*`, `vendor/**/*`, `.git/**/*`), plus the
  repository configuration's `Exclude` list, decide the analysed set. The
  scan adds `--only`/`--except` only via operator `ExtraArguments`.
- **Suppressed offenses (by default, none).** The scan passes
  `--ignore-disable-comments`, so `# rubocop:disable` comments authored in
  the audited tree are inert — the subject cannot silence findings
  line-by-line. Set `TrustRepositorySuppression: true` to honor them.
- **Cops the configuration never enables.** Only enabled cops are checked —
  and newly introduced cops ship as `pending` (`NewCops: pending`), i.e.
  disabled with a warning, until the project opts in. A clean audit against
  defaults says nothing about cops the repo never enabled. Operators who
  want a fixed bar pass `--only`/`--except` in `ExtraArguments` or pin an
  operator-owned config (see below).
- **Autocorrection drift.** The scan never passes `-a`/`-A`: no file is
  rewritten, and correctable-but-uncorrected offenses are still findings.
  Whether an offense was auto-correctable is not reported — the finding is
  the offense itself.
- **More than `MaxFindings` offenses.** Findings beyond `MaxFindings`
  (default 1000) are dropped and the truncation is reported in the raw
  output.

## Exit codes and failure classification

RuboCop's convention (verified against the RuboCop source —
`STATUS_SUCCESS = 0`, `STATUS_OFFENSES = 1`, `STATUS_ERROR = 2` in
`lib/rubocop/cli.rb` — **not** assumed from the common table):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Scanned clean (empty `offenses`) | Verdict (pass) |
| `0` with offenses in the report | Only via operator `--fail-level`: offenses still reported | Verdict (`Passed = false` when any finding maps to `Error` — the flag cannot silence the gate) |
| `1` with JSON on stdout | Scanned, offenses found | Verdict (fails the audit only when a finding maps to `Error`; `warning` and below are advisory) |
| `1` with no offenses on stdout | Ran but emitted no report | Infrastructure — the JSON parser fails closed |
| `2` | Could not run: bad flags, unreadable `--config`, unknown cop names, crash — diagnostics go to stderr, stdout carries no report | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else (incl. the `SIGINT+128` interrupted status) | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `rubocop` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **RuboCop `1.91.0`** (`ExpectedVersion` in scoped
config). A linter's cops change between releases, so an unpinned tool would
change findings under you: the auditor probes `rubocop --version` before every
run and reports an infrastructure failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`: RuboCop
ships as a gem and no distro package carries a version pin. Provision the
pinned release (plus a Ruby interpreter) in your sandbox baseline **only
when this plugin is enabled**:

```sh
# baseline bake step
gem install rubocop -v 1.91.0
rubocop --version   # must report 1.91.0
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates, and baseline provisioning verifies `rubocop` only in that state:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.rubocop"],
      "Enabled": ["codeybox.rubocop"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.rubocop" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.rubocop`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `1.91.0` | Pinned RuboCop release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | `null` | Path passed to `--config` — an operator-pinned `.rubocop.yml` outside the repository, or a repo file overriding the default `.rubocop.yml` discovery. Ignored when `ExtraArguments` already supplies `--config`/`-c`. No config file is required: without one RuboCop runs its defaults. |
| `TrustRepositorySuppression` | `false` | When `false` (default) the scan passes `--ignore-disable-comments`, so `# rubocop:disable` comments authored in the audited tree are inert. When `true`, RuboCop honors them. The config *file* is repo-authored either way (see below). |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). Raise it to `warning` for an advisory-style gate over style findings while keeping `warning`+ visible. |
| `IncludedRules` / `ExcludedRules` | — | Exact cop names to keep/drop (e.g. `Style/FrozenStringLiteralComment`, `Lint/UselessAssignment`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/`, `tmp/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan; RuboCop reports scan-relative paths, so these match directly. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--only <cops>`, `--except <cops>`, `--force-default-config` (ignore all repo config files — pair with `--only`), or `--fail-level`. A repeated `--format`/`-f` replaces (or, as a repeatable flag, corrupts with a second formatter's output) the JSON report the parser expects and breaks the run into infrastructure failure; `--out`/`-o` and `--stderr` redirect the report away from stdout with the same effect. The built-in `--format`, `--cache`, `--ignore-disable-comments`, and `--config` defer to the operator's own setting. |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

**Repository-controlled suppression is off by default.** The audit subject
writes the repository, and RuboCop lets source files suppress cops inline
(`# rubocop:disable Style/Foo` drops the finding; `# rubocop:todo` does the
same through the generated todo file). The scan therefore passes
`--ignore-disable-comments` unless `TrustRepositorySuppression: true` is
set — findings then appear for code the comments would have suppressed.
Expect *more* findings than a local `rubocop` on repos that rely on inline
disables; that is the gate working as intended.

The larger suppression surface is the RuboCop configuration itself
(`.rubocop.yml`, `.rubocop_todo.yml`): it is repo-authored, and its cop
selection, severities, and `Exclude` list are honored because the project's
own lint contract is the meaningful check — changes to it are visible in the
audited diff. The auditor runs under `AuditCapabilities.None` (no agent
credentials, no network). For a fully operator-owned gate, pass
`--force-default-config` plus `--only` in `ExtraArguments` (RuboCop then
ignores every repository config file) or pin an out-of-repo file via
`ConfigPath`.

## Default scope

`rubocop .` — RuboCop's own default excludes plus the repository
configuration decide what gets checked; that is the project's own declaration
of checkable scope. The scan adds `--cache false` so it never records cache
entries for the audited tree (and never trusts a stale cache about it). On
top of that, the finding-level `ExcludePaths` backstop lists vendored
(`vendor/`, `third_party/`, `node_modules/`) and generated (`dist/`,
`build/`, `out/`, `coverage/`, `tmp/`) prefixes: violations there belong to
upstream packages or build output, not the change under audit — reporting
them produces noise that trains operators to ignore the auditor. Re-include
a path by overriding `ExcludePaths`, or narrow the scan with `--only` /
`--except` in `ExtraArguments`.
