# CodeyBox: Gosec Go Security Auditor

Auditor plugin wrapping [gosec](https://github.com/securego/gosec): it scans
the audited repository's Go packages with `gosec -fmt sarif -stdout
-exclude-generated -nosec ./...` and reports each issue as an audit finding
with the gosec rule id and `file:line` location. Go only — non-Go
repositories fail loudly (see below).

## What it reports

- One finding per gosec issue. The title carries the rule id (`G101`,
  `G204`, `G404`, …) and the first line of the message; the description
  carries the tool, rule, tool-reported severity, location, and the full
  message. `Location` is `path:startLine`, relative to the repository root.
- **Gate behaviour: severity-driven — only HIGH findings block by
  default.** gosec's native severities go through a declared map, never
  raw: `HIGH` → `Error` (fails the audit), `MEDIUM` → `Warning`, `LOW` →
  `Info`. The SARIF `level` field alone is *not* used: gosec flattens
  MEDIUM and HIGH both into `error`, so the auditor recovers the native
  severity from each result's rule descriptor (`properties.tags`) and falls
  back to the SARIF level only when the descriptor is unreadable. To drop
  advisory noise, raise `MinimumSeverity` — the tool-side `-severity` flag
  cannot be passed: `ExtraArguments` accepts package patterns only and
  rejects flag-shaped entries deterministically (see the config table
  below), so `MinimumSeverity` is the supported knob.

## What it cannot see

- **Files gosec's loader cannot analyze.** gosec analyzes type-checked Go
  packages through the `go` toolchain. Files in packages that fail to load
  (missing modules, broken `go.mod`, no `go` binary) record per-file errors
  — which gosec's SARIF report does not carry. The auditor fails closed:
  exit 1 with an empty report is reported as infrastructure, never a pass.
  When errors coexist with real findings the findings are still reported
  (and still fail the audit when HIGH); the error detail itself is not
  visible in SARIF — that is a gosec limitation, not a finding.
- **Test files.** gosec's `-tests` is off by default; `_test.go` files are
  not scanned. Opt in via the `ScanTests` key.
- **Generated code.** `-exclude-generated` skips files carrying the
  `// Code generated … DO NOT EDIT` marker. Set `IncludeGenerated: true`
  to analyze them.
- **Vendor and dot trees.** gosec's built-in `-exclude-dir` defaults skip
  `vendor/` and `.git/` at scan time; the auditor additionally drops
  findings under `vendor/` and `third_party/` at the finding level. Go's
  package loader never sees `testdata/`. Override `ExcludePaths` to change
  the finding-level list.
- **Suppressed issues in trust mode.** With `TrustRepositorySuppression:
  true`, issues suppressed by `#nosec`/`//gosec:disable` comments are
  dropped by gosec before the report is written — they never reach the
  auditor at all.

## Exit codes and failure classification

gosec does **not** follow the common "1 = findings, 2 = could not run"
convention (verified against v2.28 source): `computeExitCode` returns `1`
for findings *and* for per-package analysis errors, and every operational
failure also returns `1`. The SARIF report on stdout is the discriminator:

| Exit | Stdout | Meaning | Classification |
|---|---|---|---|
| `0` | SARIF | Clean run — no unsuppressed issues, no analysis errors | Verdict (pass) |
| `1` | SARIF with ≥1 result | Ran and found issues | Verdict (`Passed = false` when any finding maps to `Error`) |
| `1` | SARIF with 0 results | Ran but recorded analysis errors (unbuildable/unparseable packages — invisible in SARIF) | Infrastructure — fails closed |
| `1` | no SARIF | Could not run: bad `-conf`, "No packages found" (non-Go repo), analyzer/report failure | Infrastructure |
| `2` | text | Flag-parse (usage) error | Infrastructure |
| `126`/`127` | — | Binary not executable or not found | Infrastructure |
| anything else | — | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `gosec` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **gosec `2.28.0`** (`ExpectedVersion` in scoped
config). A scanner's rule set changes between releases, so an unpinned tool
would change findings under you: the auditor probes `gosec -version` before
every run and reports an infrastructure failure on any other version.

> **`go install` is not enough.** `gosec -version` only reports a real
> version when `main.Version` was stamped at build time — the upstream
> release tarballs do this via goreleaser ldflags, but `go install
> github.com/securego/gosec/v2/cmd/gosec@…` builds report `Version: dev`
> and fail the pin check. Provision the release binary:

```sh
# baseline bake step (amd64 example)
curl -fsSL https://github.com/securego/gosec/releases/download/v2.28.0/gosec_2.28.0_linux_amd64.tar.gz \
  | tar -xz gosec && install -m755 gosec /usr/local/bin/gosec
gosec -version   # must print "Version: 2.28.0"
```

The tool requirement is declared **verify-only** — no `AptPackage`: no
distro package carries a version pin. The auditor also declares a `go`
requirement (also verify-only): gosec loads packages through the Go
toolchain, so the baseline needs a `go` new enough for the audited module's
`go.mod` directive, plus module dependencies pre-cached (`go mod download`
at bake time) or vendored — the audit sandbox has no network.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.gosec"],
      "Enabled": ["codeybox.gosec"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.gosec" }
```

The `gosec` and `go` tool requirements are only contributed to baseline
provisioning while the plugin is enabled.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.gosec`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `2.28.0` | Pinned gosec release; a different installed version (including `dev` from `go install` builds) fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | `null` | Path passed to `-conf` — an operator-pinned gosec JSON config (rule globals, per-rule settings). gosec never reads a config from the audited repository on its own, so this is purely an operator knob. |
| `ScanTests` | `false` | When `true`, passes `-tests`: `_test.go` files are scanned too. |
| `BuildTags` | `null` | Value passed to `-tags` — comma-separated Go build tags so files behind build constraints resolve during analysis. |
| `IncludeGenerated` | `false` | When `true`, omits `-exclude-generated` so generated files (`// Code generated … DO NOT EDIT`) are analyzed. |
| `TrustRepositorySuppression` | `false` | When `false` (default) the scan passes `-nosec`, so `#nosec` and `//gosec:disable` comments authored in the audited tree are inert — the subject cannot silence findings. When `true`, gosec honors those comments and suppressed issues never reach the report. Expect *more* findings than a local `gosec` run on repos that rely on inline suppression; that is the gate working as intended. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). It can only drop findings, never raise them — `warning` does not turn MEDIUM into a gate. |
| `IncludedRules` / `ExcludedRules` | — | Exact gosec rule ids to keep/drop (e.g. `G104`, `G204`). Post-scan finding filter — the tool-side equivalents (`-include`/`-exclude`) are flag-shaped and cannot be passed via `ExtraArguments` (see below). |
| `ExcludePaths` | `vendor/`, `third_party/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). **Package patterns only.** The positional `./...` scan target must come last — gosec relativizes SARIF artifact URIs against the positional scan roots, and omitting it (`-r`) makes every finding's location an empty URI. A consequence: Go's flag package stops parsing at the first positional, so a flag-shaped extra (`-tests`, `-severity high`, `-conf`, …) would be swallowed as a package path and silently ignored — the auditor rejects flag-shaped extras deterministically instead, and rejects absolute/`..` patterns that would scan outside the worktree. Use `ScanTests`, `BuildTags`, `ConfigPath`, and the severity/rule/path options for flag behavior; a path-shaped extra (`./pkg/...`) is appended as an additional scan pattern. |
| `TimeoutSeconds` | `300` | Per-run bound (max 3600). gosec has no in-tool timeout — the bound is enforced by the shared harness, and exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation (a truncated SARIF report fails closed as infrastructure). |

**Repository-controlled suppression is off by default.** The audit subject
writes the repository, and `#nosec`/`//gosec:disable` comments suppress
findings inline. The scan therefore passes `-nosec` unless
`TrustRepositorySuppression: true` is set.

gosec's AI autofix (`GOSEC_AI_PROVIDER`/`GOSEC_AI_API_KEY`/
`GOSEC_AI_BASE_URL`) ships source snippets to an external service when
enabled; the auditor clears those variables for the tool process so the
scan is always deterministic local analysis.

## Default scope

`gosec ./...` scans every loadable Go package under the repository root —
with generated files excluded (`-exclude-generated`), repository `#nosec`
suppression inert by default, and vendored/dependency trees skipped by
gosec's own `-exclude-dir` defaults plus a finding-level
`vendor/`/`third_party/` backstop. Findings in vendored or generated code
describe upstream packages and codegen output, not the change under audit —
reporting them produces noise that trains operators to ignore the auditor.
A repository with no loadable Go packages fails loudly as infrastructure
("No packages found"), never as a pass.
