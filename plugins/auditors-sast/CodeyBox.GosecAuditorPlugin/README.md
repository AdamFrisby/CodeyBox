# CodeyBox: Gosec Go Security Auditor

Auditor plugin wrapping [gosec](https://github.com/securego/gosec): it scans
the audited repository's Go packages with `gosec -stdout -verbose sarif
-fmt json -out /dev/stderr -log /dev/null -nosec ./...`
— the stdout report is SARIF (the findings channel) and the JSON report
is written to stderr (the analysis-error channel SARIF lacks) — and
reports each issue as an audit finding with the gosec rule id and
`file:line` location. Go only — non-Go repositories fail loudly (see
below).

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
  (missing modules, broken `go.mod`, no `go` binary, compile errors) record
  per-file errors — which gosec's SARIF report does not carry; they exist
  only in the JSON report's `"Golang errors"` map. The scan therefore
  writes that JSON report to stderr (`-fmt json -out /dev/stderr`, with
  `-log /dev/null` keeping it the only stderr writer) so the error channel
  travels in-band. The auditor fails closed — infrastructure, never a pass
  — whenever the channel records errors, *even alongside real findings*:
  a package the scan could not load must not ride a passing verdict. An
  absent or unreadable channel fails closed too.
- **Test files.** gosec's `-tests` is off by default; `_test.go` files are
  not scanned. Opt in via the `ScanTests` key.
- **Files behind build constraints.** Files the build configuration
  excludes — other `GOOS`/`GOARCH` sources, files behind `//go:build` tags
  not enabled — are never type-checked, so gosec never analyzes them.
  `BuildTags` widens coverage; the host platform's complement
  (`*_windows.go` on a Linux sandbox) is always out of reach.
- **Nested Go module roots.** `go list ./...` — the loader behind gosec —
  does not descend into a directory carrying its own `go.mod`: each is a
  separate module root, skipped with no error recorded anywhere, so code
  under `tools/`, `services/`, or any nested module would be silently
  unscanned while the audit still passed. A bounded `find` runs before the
  scan and the audit **fails closed** (deterministic infrastructure
  failure) listing every nested root beyond the root module's — trees the
  go tool never reaches (`vendor/`, `testdata/`, dot- and
  underscore-prefixed dirs) are pruned and do not trip the gate. Operators
  who accept the residual scope set `AllowNestedModules: true`, which
  downgrades the gate to a warning log naming the roots. (`./...` does not
  widen to cover nested modules even in workspace mode, so a `go.work` is
  not a substitute for acknowledging them.)
- **Vendor and dot trees.** gosec's built-in `-exclude-dir` defaults skip
  `vendor/` and `.git/` at scan time; `vendor/` is also the only default
  `ExcludePaths` entry — a finding-level backstop, since vendor semantics
  are tool-enforced. There are no other name-based exclusions: a
  subject-named directory (`third_party/`, …) is ordinary shippable code
  and dropping its findings would be a subject-controlled suppression
  channel. Go's package loader never sees `testdata/`. Override
  `ExcludePaths` to add operator-chosen paths.
- **Suppressed issues in trust mode.** With `TrustRepositorySuppression:
  true`, issues suppressed by `#nosec`/`//gosec:disable` comments are
  dropped by gosec before the report is written — they never reach the
  auditor at all.
- **Generated code only when the operator opts out.** Generated files are
  scanned by default: the `// Code generated … DO NOT EDIT` marker is
  authored inside the audited repository, so honoring it by default would
  let the subject erase a file from analysis with one comment line — the
  same suppression class `-nosec` blocks. Setting `ExcludeGenerated: true`
  passes `-exclude-generated` and skips marker files.

## Exit codes and failure classification

gosec does **not** follow the common "1 = findings, 2 = could not run"
convention (verified against the v2.28 source; live-checked against a
2.22.x binary): `computeExitCode` returns `1`
for findings *and* for per-package analysis errors, and every post-parse
operational failure also returns `1`. The SARIF report on stdout plus the
JSON `"Golang errors"` channel on stderr discriminate the cases:

| Exit | Stdout | Stderr error channel | Meaning | Classification |
|---|---|---|---|---|
| `0` | SARIF | empty map | Clean run — no unsuppressed issues, no analysis errors | Verdict (pass) |
| `1` | SARIF with ≥1 result | empty map | Ran and found issues | Verdict (`Passed = false` when any finding maps to `Error`) |
| `1` | SARIF with ≥1 result | ≥1 error | Findings *and* unscanned packages | Infrastructure — fails closed |
| `1` | SARIF with 0 results | any | Ran but recorded analysis errors (unbuildable/unparseable packages — invisible in SARIF) | Infrastructure — fails closed |
| `0`/`1` | SARIF | absent or unreadable | Error channel unverifiable — cannot prove the tree was fully analyzed | Infrastructure — fails closed |
| `1` | no SARIF | — | Could not run: bad `-conf`, "No packages found" (non-Go repo), analyzer/report failure | Infrastructure |
| `2` | text | — | Flag-parse (usage) error | Infrastructure |
| `126`/`127` | — | — | Binary not executable or not found | Infrastructure |
| anything else | — | — | Unknown convention | Infrastructure (fails loud, never a pass) |

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
| `ExcludeGenerated` | `false` | When `true`, passes `-exclude-generated` so generated files (`// Code generated … DO NOT EDIT`) are skipped. Default off **deliberately**: the marker is repo-authored, so honoring it by default would let the subject remove a file from analysis with one comment. |
| `AllowNestedModules` | `false` | A `go.mod` below the worktree root fails the run deterministically — `go list ./...` never descends into a nested module root, so code there is silently unscanned. `true` acknowledges the residual scope: the run proceeds and the uncovered roots are logged as a warning. |
| `TrustRepositorySuppression` | `false` | When `false` (default) the scan passes `-nosec`, so `#nosec` and `//gosec:disable` comments authored in the audited tree are inert — the subject cannot silence findings. When `true`, gosec honors those comments and suppressed issues never reach the report. Expect *more* findings than a local `gosec` run on repos that rely on inline suppression; that is the gate working as intended. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). It can only drop findings, never raise them — `warning` does not turn MEDIUM into a gate. |
| `IncludedRules` / `ExcludedRules` | — | Exact gosec rule ids to keep/drop (e.g. `G104`, `G204`). Post-scan finding filter — the tool-side equivalents (`-include`/`-exclude`) are flag-shaped and cannot be passed via `ExtraArguments` (see below). |
| `ExcludePaths` | `vendor/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. `vendor/` is the only default (tool-enforced semantics); setting the list replaces the default. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). **Worktree-relative package patterns only.** The positional `./...` scan target must come last — gosec relativizes SARIF artifact URIs against the positional scan roots, and omitting it (`-r`) makes every finding's location an empty URI. A consequence: Go's flag package stops parsing at the first positional, so a flag-shaped extra (`-tests`, `-severity high`, `-conf`, …) would be swallowed as a package path and silently ignored — the auditor rejects flag-shaped extras deterministically instead, and rejects absolute/`..` patterns that would scan outside the worktree. Bare patterns are normalized to `./`-relative form before forwarding — gosec hands patterns to `packages.Load`, where a non-`./` pattern (`std`, `all`, `golang.org/x/...`) resolves against GOROOT or the module cache, outside the audited tree. Use `ScanTests`, `BuildTags`, `ConfigPath`, and the severity/rule/path options for flag behavior; a path-shaped extra (`./pkg/...` or `pkg/...`) is appended as an additional scan pattern. |
| `TimeoutSeconds` | `300` | Per-run bound (max 3600). gosec has no in-tool timeout — the bound is enforced by the shared harness, and exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation (a truncated SARIF report fails closed as infrastructure). |

**Repository-controlled suppression is off by default.** The audit subject
writes the repository, and `#nosec`/`//gosec:disable` comments suppress
findings inline. The scan therefore passes `-nosec` unless
`TrustRepositorySuppression: true` is set.

gosec's AI autofix (`GOSEC_AI_PROVIDER`/`GOSEC_AI_API_KEY`/
`GOSEC_AI_BASE_URL`) ships source snippets to an external service when
enabled; the auditor removes those variables from the tool process's
environment outright — presence alone could arm the feature — so the scan
is always deterministic local analysis.

## Default scope

`gosec ./...` scans every loadable Go package under the repository root —
gated by the nested-module probe, with generated files *included* (their
marker is repo-authored; `ExcludeGenerated` opts out), repository `#nosec`
suppression inert by default, and vendored trees skipped by gosec's own
`-exclude-dir` defaults plus a finding-level `vendor/` backstop. Findings
in vendored code describe upstream packages, not the change under audit —
reporting them produces noise that trains operators to ignore the auditor.
Generated code is different in kind: it is built and shipped by this
repository, and its exclusion marker is the subject's to write, so it
stays in scope unless an operator says otherwise. A repository with no
loadable Go packages fails loudly as infrastructure ("No packages
found"), never as a pass — and so does a tree whose nested module roots
the scan cannot reach.
