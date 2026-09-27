# CodeyBox: SwiftLint Swift Auditor

Auditor plugin wrapping [SwiftLint](https://github.com/realm/SwiftLint): it
lints the audited repository with `swiftlint lint --reporter json --no-cache
.` and reports each violation as an audit finding with the SwiftLint rule id
(e.g. `trailing_whitespace`, `identifier_name`, `force_cast`) and
`file:line` location. Swift analysis only.

## What it reports

- One finding per SwiftLint violation. The title carries the rule id and the
  first line of the reason (e.g. `trailing_whitespace: Lines should not have
  trailing whitespace`); the description carries the tool, rule,
  tool-reported severity, location, and the full reason. `Location` is
  `path:startLine`; SwiftLint's absolute `file` values (the report embeds no
  working directory) are relativized against the scan root the auditor
  resolves per run.
- **Gate behaviour: hybrid / severity-driven — not blocking on every
  finding.** SwiftLint's severities go through a declared map, never raw:
  `Error` → `Error` (fails the audit); `Warning` → `Warning` (advisory);
  anything unrecognised → `Warning`. Which severity a rule reports comes from
  the configuration in force — the repo's `.swiftlint.yml` by default.
  `MinimumSeverity` can only drop findings, so the intended way to harden the
  gate is `--strict` in `ExtraArguments` (the tool then upgrades warnings to
  errors in the report itself) or raising rule severities in the ruleset.

## What it cannot see

- **Non-Swift files.** The scan lints Swift sources — everything else
  produces no findings.
- **Files the configuration excludes.** The repo's `.swiftlint.yml`
  `excluded` list decides the analysed set (plus whatever `included` narrows
  it to). SwiftLint ships with **no default vendored excludes** — a
  `vendor/`, `Pods/`, or `Carthage/` tree is linted unless the config
  excludes it — so the finding-level `ExcludePaths` backstop below is what
  keeps dependency checkouts out of the audit by default.
- **Suppressed violations.** `// swiftlint:disable …` comments authored in
  the audited tree are honored — SwiftLint offers no flag that makes them
  inert. A warnings-clean local run that disagrees with the audit is a signal
  to inspect the diff's suppression comments.
- **SourceKit-dependent rules in the sandbox.** Rules that need SourceKit
  (e.g. `statement_position`) are skipped with a stderr warning when
  SourceKit cannot load in the audit sandbox — those rules simply produce no
  findings there. The stderr note is kept in the audit raw output.
- **Findings under excluded prefixes.** `ExcludePaths` is a finding filter —
  SwiftLint still lints those files, but findings under `vendor/`,
  `third_party/`, `node_modules/`, `Pods/`, `Carthage/`, `DerivedData/`,
  `.build/`, `dist/`, `build/`, `out/`, `coverage/` are dropped. Override
  `ExcludePaths` to re-include them.
- **More than `MaxFindings` violations.** Findings beyond `MaxFindings`
  (default 1000) are dropped and the truncation is reported in the raw
  output.
- **Repositories with no Swift files.** SwiftLint exits 1 with `No lintable
  files found` and no report — that is infrastructure (the scan analyzed
  nothing), not a clean pass. A Swift project that unexpectedly reports this
  has a scope problem worth investigating, not a passing audit.

## Exit codes and failure classification

SwiftLint's convention (verified against 0.65.1 — notably **not** the common
"0 clean / 1 findings / 2 error" table):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Linted clean, or warnings only (warnings do not fail the run) | Verdict (pass, or advisory findings) |
| `2` | Linted, error-level violations found | Verdict (`Passed = false` — an `Error` finding fails the audit) |
| `1` | No lintable files at the scanned paths (no report on stdout) | Infrastructure (`AuditUnavailableException`) |
| `64` | Illegal command-line parameters (`EX_USAGE`) | Infrastructure |
| `134` (`SIGABRT`) | Crash: unknown `--reporter`, unreadable `--config` file | Infrastructure — the JSON parser fails closed on the missing report |
| `0`/`2` with no JSON on stdout | Crash before the report, or an operator `--reporter`/`--output` override that moved the report off stdout | Infrastructure — the JSON parser fails closed |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `swiftlint` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **SwiftLint `0.65.1`** (`ExpectedVersion` in scoped
config). A linter's rules change between releases, so an unpinned tool would
change findings under you: the auditor probes `swiftlint --version` before
every run and reports an infrastructure failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`: SwiftLint
ships as a GitHub release archive (Linux), a `.pkg` / Homebrew formula
(macOS), and via CocoaPods — no distro apt package carries a version pin.
Provision the pinned release in your sandbox baseline **only when this plugin
is enabled**:

```sh
# baseline bake step, Linux amd64 (use swiftlint_linux_arm64.zip on arm64)
curl -sSfL -o /tmp/swiftlint.zip \
  https://github.com/realm/SwiftLint/releases/download/0.65.1/swiftlint_linux_amd64.zip
python3 -c "import zipfile; zipfile.ZipFile('/tmp/swiftlint.zip').extractall('/usr/local/bin')"
chmod +x /usr/local/bin/swiftlint
swiftlint --version   # must print 0.65.1
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.swiftlint"],
      "Enabled": ["codeybox.swiftlint"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.swiftlint" }
```

The `swiftlint` tool requirement is only contributed to baseline provisioning
while the plugin is enabled.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.swiftlint`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `0.65.1` | Pinned SwiftLint release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | `null` | Path passed to `--config` — an operator-pinned `.swiftlint.yml` outside the repository, or a repo file overriding the default discovery. Ignored when `ExtraArguments` already supplies `--config`. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact SwiftLint rule ids to keep/drop (e.g. `trailing_whitespace`, `force_cast`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `Pods/`, `Carthage/`, `DerivedData/`, `.build/`, `dist/`, `build/`, `out/`, `coverage/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. Swift-ecosystem entries (`Pods/`, `Carthage/`, `DerivedData/`, `.build/`) are included because SwiftLint lints dependency checkouts by default. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--strict` (warnings become errors — the hardened gate), `--only-rule <id>`, or `--config` to pin an operator ruleset. A repeated `--reporter` would replace the JSON report the parser expects and `--output` would redirect it off stdout — both break the run into infrastructure failure. |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

**Repository-controlled suppression stays honored.** The audit subject writes
the repository, and SwiftLint lets source files suppress the linter inline
(`// swiftlint:disable <rule>` drops the finding for the enclosed region;
`// swiftlint:disable:next` for the next line). There is no `--no-inline`
equivalent, so findings never appear for code the comments suppress — unlike
the ESLint/RuboCop auditors, which inert inline suppression by default. The
larger suppression surface is `.swiftlint.yml` itself (`only_rules`,
`disabled_rules`, severity overrides, `excluded`): it is repo-authored and
honored because the project's own lint contract is the meaningful check —
changes to it are visible in the audited diff. For a fully operator-owned
gate, pin an out-of-repo config via `ConfigPath` and treat a local run that
disagrees with the audit as a signal to inspect the diff's suppression
comments and config changes.

## Default scope

`swiftlint lint .` — the whole audited tree, minus the configuration's
`excluded` list. The scan adds `--no-cache` so it never writes a cache entry
for the audited tree. On top of that, the finding-level `ExcludePaths`
backstop drops vendored (`vendor/`, `third_party/`, `node_modules/`,
`Pods/`, `Carthage/`), build-environment (`.build/`, `DerivedData/`), and
generated (`dist/`, `build/`, `out/`, `coverage/`) prefixes: violations there
belong to upstream packages or build output, not the change under audit —
reporting them produces noise that trains operators to ignore the auditor.
Because SwiftLint has no default vendored excludes of its own, this backstop
does the primary dependency-checkout filtering at finding level (the tool
still lints those files). Re-include a path by overriding `ExcludePaths`, or
narrow the scan with `excluded` in an operator-pinned config.
