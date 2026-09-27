# CodeyBox: govulncheck Go Vulnerability Auditor

Auditor plugin wrapping [govulncheck](https://pkg.go.dev/golang.org/x/vuln/cmd/govulncheck):
it runs source-level analysis of the audited repository's Go module
(`govulncheck -format sarif ./...`) and reports the vulnerabilities that are
**reachable in code** — matching the audited packages against the Go
vulnerability database and using call analysis to tell "dependency has a
vulnerability" apart from "your code actually calls the vulnerable symbol".
Every SARIF result becomes an audit finding.

## What it reports

- One finding per SARIF result on **stdout** (`-format sarif`). govulncheck
  emits one result per affected OSV entry, so the finding's rule id is the
  OSV id (`GO-2021-0113`, `GO-2024-2687`) — `IncludedRules`/`ExcludedRules`
  select by vulnerability.
- **Severity: mapped, severity-driven gate.** govulncheck's SARIF levels
  encode reachability, not CVSS: at the default `symbol` scan level,
  `error` = the code calls a vulnerable symbol, `warning` = it imports a
  vulnerable package without reaching the symbol, `note` = it depends on a
  vulnerable module only. The auditor maps `error` → Error, `warning` →
  Warning, `note` → Info, unknown → Warning. Error findings (reachable
  vulnerabilities) fail the audit; warnings are advisory.
  `MinimumSeverity` only drops findings, it never raises them.
- **Location: `go.mod`, file-level.** govulncheck attaches every source-mode
  result to the module manifest with a stub `startLine: 1` ("for now, point
  to the first line") — the vulnerable dependency is declared there, but the
  `:1` suffix does not point at a real line. The actual call-site positions
  live in the report's stacks/codeFlows, which the shared SARIF parser does
  not read; the finding message carries the matched package list instead
  ("Your code calls vulnerable functions in 1 packages
  (golang.org/x/text/language).").

## What it cannot see

- **Unreached vs reached distinctions below package level without call
  analysis at other scan levels.** `warning`/`note` results say "imported but
  not reached" / "in the module graph only" — whether a `warning`-level
  dependency is actually exploitable requires reading the code, not the
  report.
- **Calls made through reflection.** govulncheck analyses function-pointer
  and interface calls conservatively and cannot see `reflect`-driven calls
  at all; reachable-only-through-reflection vulnerable code is not reported
  (documented upstream limitation, alongside conservative false positives in
  the other direction).
- **Non-module source trees and binaries.** The scan is source mode on a Go
  module: a tree with no `go.mod` is a hard error (infrastructure, never a
  pass). `-mode binary` / `-mode extract` on compiled binaries is out of
  scope — the audit subject is the worktree's source.
- **Vulnerabilities outside the Go module.** OS packages, non-Go
  dependencies, and other ecosystems are invisible — pair with grype or a
  sibling auditor for those.
- **Suppression.** govulncheck has no mechanism to silence findings (an
  explicit upstream non-feature), so there is nothing for operators to
  configure there — and nothing the audit subject can abuse, either.
  `ExcludedRules` is the only per-finding drop valve; `ExcludePaths` can
  only ever match `go.mod` and is rejected when set to it (see the
  configuration table).

## Exit codes and failure classification

govulncheck does **not** follow the common "0 = clean, 1 = findings"
convention — verified against the v1.8.0 source (`internal/scan`): with
`-format sarif` a completed run exits `0` regardless of findings, and the
report is the verdict.

| Exit | stdout | Meaning | Classification |
|---|---|---|---|
| `0` | SARIF | ran (clean or with findings — the SARIF decides) | pass / findings |
| `1` | error text | could not run: no `go.mod`, package-load failure, unreachable vulnerability database, unusable `-db` | infrastructure |
| `2` | usage text | bad flag, no patterns, patterns matching no packages, a file passed in source mode | infrastructure |
| `3` | — | "vulnerabilities found" — **text format only**; cannot occur under `-format sarif`, so it means the format contract broke | infrastructure |
| `126`/`127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

A missing `govulncheck` binary, a missing `go` toolchain, a version
mismatch, a timeout, and unparseable output are likewise infrastructure
failures naming the tool — never a passing audit. Do not override
`-format`/`-mode`/`-show` via `ExtraArguments`: the parser reads SARIF, and
a different output format fails closed as infrastructure (exit 2 or 3, or an
unparseable stdout), it does not pass.

## Version pinning and provisioning

The auditor is pinned to **govulncheck `1.8.0`** (`ExpectedVersion` in
scoped config): the check set, reachability levels, and report shape change
between releases, so an unpinned binary would change findings under you.
`govulncheck -version` is probed before every run; because its banner prints
the Go toolchain's version first (`Go: go1.x.y` precedes `Scanner:
govulncheck@v1.8.0`), the declared pin's `VersionExtractor` anchors on the
`govulncheck@v…` token — the generic first-version-token extraction would
verify the wrong component.

Two tool requirements are declared, both **verify-only** (no `AptPackage` —
no distro package carries a pinned govulncheck):

- `govulncheck` — provision via `go install
  golang.org/x/vuln/cmd/govulncheck@v1.8.0` (or a vendored pinned binary)
  through `CodeyBox:MultipassExtraRuncmd` / `CodeyBox:Incus:ExtraRuncmd` or
  `ExecutableProvisions`.
- `go` — govulncheck shells out to `go env`/`go list` for module and
  package loading; provision a Go toolchain recent enough for the audited
  modules.

Both reach baseline provisioning only while the plugin is enabled.

## Repository-controlled files

govulncheck reads **no configuration file** from the repository under
audit — there is no `.govulncheck.yaml` or equivalent to gate on, and
upstream explicitly does not support silencing findings. The module files it
does read (`go.mod`, `go.sum`, `go.work`) are the audit subject: they define
the graph under audit and their changes are visible in the diff. The only
operator-side configuration that can hide a result is `ExcludedRules` /
`DbUrl`, both operator-owned scoped keys — an `ExcludePaths` of `go.mod`
would hide everything, so it is refused as a deterministic configuration
error rather than honored.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.govulncheck"],
      "Enabled": ["codeybox.govulncheck"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.govulncheck" }
```

Only then do the declared `govulncheck`/`go` tool requirements reach
baseline provisioning (presence-verified at bake time; nothing is
apt-installed because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.govulncheck`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `1.8.0` | Pinned govulncheck release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `DbUrl` | `https://vuln.go.dev` (tool default) | `-db` — the vulnerability database URL. Point at an operator mirror or a provisioned local database for offline deployments. The database decides the verdict: prefer `https://` or a local/loopback source — a plaintext `http://` feed offers no integrity for the evidence reported. |
| `ScanLevel` | `symbol` | `-scan` — `symbol` (reachability, default), `package` (vulnerable imports), or `module` (vulnerable modules; accepts no `Patterns`). Invalid values are a deterministic configuration failure. |
| `ModuleDirectory` | — (worktree root) | `-C` — directory govulncheck changes to before scanning; set it when the audited Go module lives in a repository subdirectory. |
| `BuildTags` | — | `-tags` — comma-separated Go build tags controlling which files participate in the analysis. |
| `IncludeTests` | `false` | `-test` — also analyze test files. Default off: a vulnerability reachable only from test code is not shipped. |
| `Patterns` | `./...` | Positional package patterns for the scan. Entries must not start with `-` (they are emitted after all flags and would be parsed as govulncheck flags); incompatible with `ScanLevel: module`. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. |
| `IncludedRules` / `ExcludedRules` | — | Exact OSV ids (`GO-…`) to keep/drop. |
| `ExcludePaths` | — | Repo-relative paths dropped from findings. **Not a real filter here**: govulncheck's SARIF locates every result at `go.mod`, so the only entry that can ever match is `go.mod` itself — an all-or-nothing suppression. Setting it is refused as a deterministic configuration error; to stand the auditor down, remove it from `Plugins:Enabled` or `Audit.Custom` instead. |
| `ExtraArguments` | — | Extra argv appended after the built-in args — which means **after the patterns**: govulncheck's flag parser stops at the first positional, so extras act as additional package patterns, not flags. Flag-shaped extras fail loudly (exit 1/2); use the scoped keys above for flags. |
| `Offline` | `false` | Declares the deployment needs no egress: drops the `Network` capability so the auditor joins the no-egress sandbox group and pins `GOPROXY=off` for the tool process (module loads come from the pre-seeded cache only). Point `DbUrl` at a provisioned local database first — a remote `-db` with `Offline` set fails loudly. |
| `TimeoutSeconds` | `300` | Per-run bound — covers the module graph load, the database query, and symbol-level call analysis; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `8 MiB` / `1000` | Output/result caps; overruns are reported as truncation. The stream default is raised from the shared 1 MiB because govulncheck's SARIF embeds call stacks and code flows per result — a clipped report fails closed as unparseable infrastructure. |

## Network egress

The auditor declares `AuditCapabilities.Network`: govulncheck queries the
configured vulnerability database (default `https://vuln.go.dev`) and the
`go`-driven package load may reach the module proxy for un-cached
dependencies, so those hosts must be in the deployment's
`AuditToolAllowedHosts` egress list or the run fails loudly as
infrastructure. Fully offline deployments set `Offline`: the `Network`
capability is dropped (the auditor then joins the no-egress sandbox group),
`GOPROXY=off` is pinned for the tool process so the package load uses only
the pre-seeded Go module cache, and `DbUrl` must point at a provisioned
local database.

## Default scope

`govulncheck -format sarif -scan symbol ./...` at the worktree root — every
package in the module, symbol-level reachability, test files excluded.
Findings are keyed on the module graph, not the file tree: vendored and
generated sources contribute their versions to the graph rather than
producing duplicate reports, so there is no vendored-code noise to exclude
by default — and nothing to exclude anyway, since every result locates at
`go.mod` (see above). Narrow or redirect the scope with `Patterns`,
`ModuleDirectory`, `BuildTags`, or `IncludeTests`.
