# CodeyBox: staticcheck Go Analyzer

Auditor plugin wrapping
[staticcheck](https://staticcheck.dev) (advanced Go static analysis — bug
finding, simplifications, and style checks across the `SA*`, `S1*`, `ST*`,
and `U*` families): it runs `staticcheck -f json ./...` in the audited
repository and reports each diagnostic as an audit finding with its check
identifier (e.g. `SA4006`, `S1039`) and `file:line` location.

The JSON report is a stream of one JSON object per line (not an array), each
carrying `code`, `severity`, `location.file`/`location.line`, and `message`.
The tool reports slash-absolute file names; the auditor relativizes them
onto repository-relative paths so locations and `ExcludePaths` behave like
every other auditor's.

## What it reports

- One finding per reported diagnostic. The title carries the check id and
  the first line of the message (e.g.
  `` SA4006: this value of afterIndex is never used ``); the description
  carries the tool, rule, tool-reported severity, location, and the full
  message. `Location` is `location.file:location.line`, relativized against
  the scan directory.
- **Package-load failures** (no `go.mod`, an unknown package pattern, a
  type error) are findings too: staticcheck exits `1` with a
  `code: "compile"` JSON diagnostic carrying an empty location, exactly
  like any other diagnostic. The finding keeps the `compile` rule id and
  the tool's message (which embeds the underlying loader error, e.g. the
  `file:line: message` of an undefined symbol) but has no `Location`.
- **Gate behaviour: blocking by default — and this is stated here, not
  implied.** Under staticcheck's default `-fail all` every diagnostic
  carries severity `error`, which maps to `Error`: any finding fails the
  audit. To soften the gate without dropping findings, narrow `-fail` in
  `ExtraArguments` (e.g. `-fail none` demotes every diagnostic to
  `warning`, keeping them reported but advisory). `MinimumSeverity` only
  drops findings below the threshold — it never demotes them.

## What it cannot see

- **Checks the configuration disables.** The repository's `staticcheck.conf`
  files (one per subtree, merged down the package tree — see
  <https://staticcheck.dev/docs/configuration/>) decide which checks run
  through their `checks` option; that is the project's own declaration of
  analysis scope. staticcheck offers no `--config` flag pointing at an
  operator-owned file, so to override check selection pass `-checks` in
  `ExtraArguments` (e.g. `-checks all,-ST1000`).
- **Findings under excluded prefixes.** `ExcludePaths` is a finding
  filter — the tool still analyzes those files, but findings under
  `vendor/` and `third_party/` are dropped. (`go list ./...`, which the
  package load goes through, already skips `vendor/` at scan time; the
  finding filter is the backstop, e.g. for GOPATH-mode trees.) Override
  `ExcludePaths` to re-include them.
- **Generated files without a directory prefix.** staticcheck analyzes
  files carrying generated markers like any other source (verified: a
  `// Code generated … DO NOT EDIT` file still yields diagnostics), and a
  path filter cannot match files that sit next to hand-written code. They
  stay visible by design — silently dropping them would hide real
  findings. Exclude a specific file with an exact `ExcludePaths` entry, or
  narrow the run with `-checks`.
- **Suppressed violations.** staticcheck honors `//lint:ignore <checks>`
  directives authored inside the audited repository, and offers no flag to
  make those directives inert, so the auditor cannot switch that off: a
  violation the diff suppresses inline stays suppressed in the audit. That
  is a documented limitation, not an oversight: the base has no mechanism
  for it and the plugin does not hand-roll one. If the audit passes a tree
  whose local `staticcheck` run disagrees only where the diff adds
  `//lint:ignore` comments, inspect those comments.
- **More than `MaxFindings` diagnostics.** Findings beyond `MaxFindings`
  (default 1000) are dropped and the truncation is reported in the raw
  output.

## Exit codes and failure classification

staticcheck's convention (verified empirically against 2025.1.1 — **not**
assumed from the common "0 clean / 1 findings / 2 error" table):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Ran clean (empty stdout), or ran with only below-`-fail` diagnostics (a `warning`-severity JSON stream on stdout) | Verdict (pass when every finding maps below `Error`, fail otherwise) |
| `1` | Ran with diagnostics at or above the `-fail` set — including package-load failures, which arrive as a `code: "compile"` JSON diagnostic with an empty location | Verdict (fail under the default `-fail all`; the `compile` record is a finding with no location, never infrastructure) |
| `1` with no JSON on stdout | Crashed before the report was written | Infrastructure (`AuditUnavailableException`) — the parser fails closed |
| `2` | Could not run: bad flags (usage on stderr, no JSON) | Infrastructure |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `staticcheck` — or a missing `go` toolchain, which the package
load shells out to — is always an infrastructure failure naming the
missing binary — never a passing audit.

## Version pinning

The auditor is pinned to **staticcheck `2025.1.1`** (`ExpectedVersion` in
scoped config). Check implementations change between releases, so an
unpinned tool would change findings under you: the auditor probes
`staticcheck -version` before every run and reports an infrastructure
failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`:
staticcheck ships via `go install` and prebuilt binaries, and no distro
package carries a version pin. Provision the pinned release in your
sandbox baseline **only when this plugin is enabled**. The baseline also
needs a Go toolchain — package loading goes through `go list`:

```sh
# baseline bake step (Go toolchain first, then the pinned analyzer)
# https://go.dev/dl/ for the toolchain, then either:
go install honnef.co/go/tools/cmd/staticcheck@2025.1.1
# ... or the pinned prebuilt binary:
# https://github.com/dominikh/go-tools/releases/download/2025.1.1/staticcheck_linux_amd64.tar.gz
staticcheck -version   # must print 2025.1.1
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates, and baseline provisioning installs `staticcheck` (and verifies
`go`) only in that state:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.staticcheck"],
      "Enabled": ["codeybox.staticcheck"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.staticcheck" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.staticcheck`, resolved per run
(hot-reloadable). staticcheck reads its own `staticcheck.conf` files from
inside the audited repository (there is no `--config` flag), so there is
no `ConfigPath` key: operator-owned check selection goes through
`ExtraArguments`.

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `2025.1.1` | Pinned staticcheck release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact check ids to keep/drop (e.g. `SA4006`, `S1039`, `ST1000`, `compile`). |
| `ExcludePaths` | `vendor/`, `third_party/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `-checks <list>` (operator-owned check selection), `-fail <list>` (which diagnostics are `error`-severity and therefore blocking), `-tags`, or `-tests=false`. A repeated `-f` replaces the JSON report the parser expects and breaks the run into infrastructure failure. Note `-f` is matched in all Go-flag forms (`-f json`, `-f=json`, `-fjson`). |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. First runs on large trees compile and cache heavily — raise this for monorepos. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

**Repository-controlled suppression is a documented limitation.** The
audit subject writes the repository, and staticcheck lets source files
silence findings inline (`//lint:ignore <checks>`) with no flag to
disable them, while `staticcheck.conf` files in the tree select the check
set. The auditor runs under `AuditCapabilities.None` — no agent
credentials, no network — so analysis itself is unprivileged, and changes
to the configuration are visible in the audited diff. For a fully
operator-owned check set, narrow the run with `-checks` in
`ExtraArguments` (which still leaves `//lint:ignore` honored) and review
suppression comments in the diff.

## Default scope

`staticcheck -f json ./...` — analysis over the whole audited tree as Go
packages, with the repository's `staticcheck.conf` files deciding which
checks run. On top of that, findings under `vendor/` (Go's own dependency
mirror — issues there belong to upstream modules) and `third_party/` (the
conventional upstream-mirror prefix) are dropped by default: reporting
them produces noise that trains operators to ignore the auditor.
Re-include a prefix by overriding `ExcludePaths`. Generated files without
a directory prefix stay visible — a path filter cannot match them, and
silently dropping them would hide real findings.
