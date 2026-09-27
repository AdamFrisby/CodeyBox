# CodeyBox: golangci-lint Go Linter

Auditor plugin wrapping
[golangci-lint](https://golangci-lint.run) (aggregated Go analysis): it runs
`golangci-lint run --output.json.path stdout --show-stats=false ./...` in
the audited repository and reports each diagnostic as an audit finding with
the originating linter name (e.g. `errcheck`, `govet`, `staticcheck`) and
`file:line` location. One run fans out to the enabled linters — `errcheck`,
`govet`, `ineffassign`, `staticcheck`, `unused`, `typecheck` and the rest of
the default set — so a single audit covers what would otherwise be a dozen
separate linter invocations.

The native JSON report is used rather than the SARIF rendering the tool can
also emit: both carry the same diagnostics, and JSON is the default
machine-readable shape the invocation is verified against.

## What it reports

- One finding per reported issue. The title carries the linter name and the
  first line of the message (e.g.
  `` errcheck: Error return value of `f.Close` is not checked ``); the
  description carries the tool, linter, tool-reported severity, location,
  and the full message. `Location` is `Pos.Filename:Pos.Line`;
  golangci-lint emits worktree-relative paths when run from the worktree
  root (which the auditor does), so no relativization is needed.
- **Type errors** (e.g. an undefined symbol, reported by the `typecheck`
  linter) are findings too — they exit `1` with a JSON report, exactly
  like any other diagnostic.
- **Gate behaviour: advisory by default — not blocking.** An issue's
  `Severity` is empty unless a `severity` rule in the configuration
  assigns one, so ordinary findings carry no tool level and map to
  `Warning` through the declared default: reported, but the audit still
  passes. Only issues with an explicit `error`-class severity map to
  `Error` and fail the audit. `MinimumSeverity` can only drop findings, so
  the intended way to harden the gate is assigning severities in the
  golangci-lint configuration (`severity.default-severity: error`, or
  per-linter `severity.rules`).

## What it cannot see

- **Files outside Go package scope.** The scan is `./...`: files the Go
  tool does not treat as part of a package (e.g. `testdata/` trees,
  ignored build-tag combinations) are not analyzed. A repository with no
  Go files exits `7` with the typechecking error on stderr — an
  infrastructure failure, not a pass and not a finding.
- **Linters the configuration disables.** The repository's
  `.golangci.yml` (or `.golangci.yaml`/`.toml`/`.json`) decides which
  linters run through its `linters.enable`/`disable` entries; that is the
  project's own declaration of lint scope. Or pin an operator-owned
  config via `ConfigPath`.
- **Findings under excluded prefixes.** `ExcludePaths` is a finding
  filter — the tool still analyzes those files, but findings under
  `vendor/` and `third_party/` are dropped. Override `ExcludePaths` to
  re-include them.
- **File-level generated code.** `*.pb.go`, mocks, and other generated
  files usually sit next to hand-written code with no directory prefix a
  path filter can match, so they stay visible. Exclude them with the
  tool's own `issues.exclude-rules` (path + linter patterns) in the
  configuration, not with `ExcludePaths`.
- **Suppressed violations.** golangci-lint honors `//nolint` directives
  (optionally scoped to linters: `//nolint:errcheck`) authored inside the
  audited repository, and offers no flag to make those directives inert,
  so the auditor cannot switch that off: a violation the diff suppresses
  inline stays suppressed in the audit. That is a documented limitation,
  not an oversight: the base has no mechanism for it and the plugin does
  not hand-roll one. If the audit passes a tree whose local
  `golangci-lint` run disagrees only where the diff adds `//nolint`
  comments, inspect those comments. Enabling the `nolintlint` linter in
  the configuration at least surfaces unused or malformed directives as
  findings of their own. Pinning an operator-owned ruleset via
  `ConfigPath` still leaves inline directives honored.
- **More than `MaxFindings` issues.** Findings beyond `MaxFindings`
  (default 1000) are dropped and the truncation is reported in the raw
  output. The tool's own `--max-issues-per-linter` (default 50) and
  `--max-same-issues` (default 3) caps apply before that; raise them via
  `ExtraArguments` for very noisy trees.

## Exit codes and failure classification

golangci-lint's convention (verified empirically against v2.14.0 — **not**
assumed from the common "0 clean / 1 findings / 2 error" table; codes
`0`–`7` match `pkg/exitcodes` upstream):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Ran clean | Verdict (pass) |
| `1` | Ran with issues found (the default of the tool's `--issues-exit-code` flag) | Verdict (pass when every finding maps below `Error`, fail otherwise) |
| `2`–`7` | Could not run: warning-in-test (`2`), failure (`3`: bad flags, analysis errors), timeout (`4`), no-go-files (`5`), no-config-file-detected (`6`), error-was-logged (`7` — e.g. a directory with no Go files, which still prints an empty-issues JSON document with the error on stderr) | Infrastructure (`AuditUnavailableException`) — the exit code is the verdict, never the presence of JSON |
| `1` with no JSON on stdout | Crashed before the report was written | Infrastructure — the JSON parser fails closed |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `golangci-lint` is always an infrastructure failure naming the
tool — never a passing audit.

## Version pinning

The auditor is pinned to **golangci-lint `2.14.0`** (`ExpectedVersion` in
scoped config). The bundled linters and their diagnostics change between
releases, so an unpinned tool would change findings under you: the auditor
probes `golangci-lint version` before every run and reports an
infrastructure failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`:
golangci-lint ships via its install script (and as a standalone binary)
and no distro package carries a version pin. Provision the pinned release
in your sandbox baseline **only when this plugin is enabled**. The
baseline also needs a Go toolchain — the analysis type-checks packages
through it:

```sh
# baseline bake step (Go toolchain first, then the pinned binary)
# https://go.dev/dl/ for the toolchain, then:
curl -sSfL https://raw.githubusercontent.com/golangci/golangci-lint/HEAD/install.sh \
  | sh -s -- -b "$(go env GOPATH)/bin" v2.14.0
golangci-lint version   # must print 2.14.0
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates, and baseline provisioning installs `golangci-lint` only in that
state:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.golangci-lint"],
      "Enabled": ["codeybox.golangci-lint"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.golangci-lint" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.golangci-lint`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `2.14.0` | Pinned golangci-lint release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | `null` | Path passed to `--config` — an operator-pinned configuration outside the repository, or a repo file overriding golangci-lint's default config discovery. Ignored when `ExtraArguments` already supplies `--config`/`-c`. To keep the repo's own config fully out of the run, set `TrustRepositoryConfig` to `false` (which passes `--no-config`) rather than hand-rolling `--no-config` in `ExtraArguments`. |
| `TrustRepositoryConfig` | `true` | Whether the audited repo's `.golangci.*` config is loaded by the tool's default discovery. Keep `true` to lint against the project's own lint contract. Set to `false` for a fully operator-owned run: the scan passes `--no-config` (unless `ExtraArguments` already supplies it) so the repo's config — including its custom-linter plugins and report file paths — is not loaded at all; pair with `ConfigPath`. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact linter names to keep/drop (e.g. `errcheck`, `govet`, `staticcheck`). |
| `ExcludePaths` | `vendor/`, `third_party/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--max-issues-per-linter 0`, `--disable <linter>` / `-D`, `--enable-only`, `--timeout`, or `--no-config`. A repeated `--output.json.path` or an added `--output.<format>.path` replaces or corrupts the JSON report the parser expects and breaks the run into infrastructure failure; a changed `--issues-exit-code` must be mirrored in `FindingsExitCodes` or findings-producing runs will be misclassified as infrastructure. Note `--show-stats=false` is passed attached: pflag bool flags do not consume a following arg, so a separated `--show-stats false` would leak `false` into the package patterns. |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. First runs on large trees compile and cache heavily — raise this for monorepos. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

**Repository-controlled suppression is a documented limitation.** The
audit subject writes the repository, and golangci-lint lets source files
silence findings inline (`//nolint`, `//nolint:<linter>`) with no flag to
disable them. The auditor runs under `AuditCapabilities.None` — no agent
credentials, no network — so analysis itself is unprivileged. The repo's
`.golangci.*` configuration (linters, exclusions, severity rules) is
honored because the project's own lint contract is the meaningful check,
and changes to it are visible in the audited diff. For a fully
operator-owned gate, pin an out-of-repo config via `ConfigPath` (which
still leaves `//nolint` honored) and review suppression comments in the
diff.

**Repository-controlled configuration can also execute code and write
files — not just suppress findings.** The repo's `.golangci.*` config,
loaded by default discovery, is executable in two further senses: (a)
custom-linter entries under `linters.settings.custom` whose `path` points
at a Go plugin (a `.so` shipped next to the config) are loaded
in-process by the tool, so a repo-authored config runs repo-authored
native code inside the audit sandbox; (b) `output.formats.<format>.path`
entries make the tool write report files to the configured paths — the
auditor pins only `--output.json.path stdout`, so any additional
file-writing format the repo config declares still fires. The auditor
runs with no agent credentials and no network, which bounds
exfiltration but does not break this source-to-sink path: a hostile
config can still tamper with the sandbox the audit runs in. Operators
who need a fully operator-owned run set `TrustRepositoryConfig` to
`false` (the scan then passes `--no-config`, so the repo's config is not
loaded at all) and pin an out-of-repo configuration via `ConfigPath`.

## Default scope

`golangci-lint run ./...` — analysis over the whole audited tree as Go
packages, with the tool's own configuration deciding which linters run
and which files count. On top of that, findings under `vendor/` (Go's own
dependency mirror — issues there belong to upstream modules, and
`-mod=vendor` builds analyze them as third-party code) and `third_party/`
(the conventional upstream-mirror prefix) are dropped by default:
reporting them produces noise that trains operators to ignore the
auditor. Re-include a prefix by overriding `ExcludePaths`. Generated
files without a directory prefix (`*.pb.go`, mocks beside hand-written
code) are intentionally left visible — a path filter cannot match them,
and silently dropping them would hide real findings.
