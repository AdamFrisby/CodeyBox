# CodeyBox: DevSkim Insecure API Usage Auditor

Auditor plugin wrapping the [DevSkim CLI](https://github.com/microsoft/DevSkim)
(`devskim`): it runs DevSkim's pattern-based insecure-API rules over the
audited repository (`devskim analyze -I . -f sarif`), reporting each match as
an audit finding with the DevSkim rule id (e.g. `DS126858` for a weak/broken
hash algorithm) and `file:line` location.

Each run is a single tool invocation: the analysis streams SARIF to stdout
for parsing. A scan failure (missing binary, unreadable source path, invalid
operator-supplied rules path) is infrastructure — never a pass.

## What it reports

- One finding per DevSkim match. The title carries the rule id and the
  first line of the match message; the description carries the tool, rule,
  tool-reported severity, location, and the full message. `Location` is
  `path:startLine`; paths are relative to the repository root (DevSkim roots
  result URIs at the scanned source directory by default).
- **Gate behaviour: hybrid / severity-driven — not blocking on every
  finding.** DevSkim severities go through a declared map, never raw:
  `Critical` and `Important` (plus SARIF `error`) → `Error` (fails the
  audit); `Moderate` (plus SARIF `warning`) → `Warning` (advisory);
  `BestPractice` and `ManualReview` (plus SARIF `note`/`none`) →
  `Info` (informational); anything unrecognised → `Warning`.
  `MinimumSeverity` can only drop findings, it never raises them.
- SARIF includes code snippets around each location by default, so findings
  and raw output contain the reported source context. That is the point of a
  SAST finding; treat audit reports accordingly.

## What it cannot see

- **Anything the default rules don't cover.** DevSkim is pattern matching,
  not dataflow: it flags known-insecure API usage (weak hashes, hardcoded
  secrets, dangerous sinks) but cannot follow taint across functions the way
  a deep dataflow engine does. A clean DevSkim run is not a clean bill of
  health — pair it with a dataflow auditor (e.g. the CodeQL plugin) where
  that assurance matters.
- **Suppressed matches, by default.** The scan passes the tool's
  `--disable-supression` flag (the tool's own spelling), so suppression
  comments authored in the audited repository are inert. Operators who
  deliberately trust repo-authored suppression set
  `TrustRepositorySuppression` in scoped config.
- **No repository config file is needed or honored.** Unlike linters,
  DevSkim takes no ruleset file from the audited tree — the default rules
  are embedded in the tool. Custom rules or language definitions come only
  from operator-supplied paths outside the repository via `ExtraArguments`,
  so there is nothing for the audit subject to edit to weaken the scan.
- **Findings under excluded prefixes.** `ExcludePaths` is a finding filter —
  DevSkim still scans those files, but findings under `vendor/`,
  `third_party/`, `node_modules/` are dropped. Override `ExcludePaths` to
  re-include them.
- **The `-E` exit-count mode.** DevSkim's `-E` flag redefines the exit code
  to the issue count. The auditor never passes it, and an operator must not
  add it via `ExtraArguments`: any exit outside the declared findings set
  fails closed as infrastructure.

## Exit codes and failure classification

DevSkim's convention (verified against 1.0.90 — do not assume the
gitleaks/eslint convention holds here):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Scan completed — clean or with issues; the verdict is in the SARIF document | Verdict (`Passed = false` when any finding maps to `Error`) |
| `254` | Could not run: unreadable source path (verified) | Infrastructure (`AuditUnavailableException`) |
| `134` | Could not run: crash on an invalid operator-supplied rules path (verified) | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention (including any `-E` issue-count exit) | Infrastructure (fails loud, never a pass) |

There is deliberately no separate "found something" exit: only `0` is
findings-producing. A missing `devskim` is always an infrastructure failure
naming the tool — never a passing audit.

## Version pinning

The auditor is pinned to **DevSkim `1.0.90`** (`ExpectedVersion` in scoped
config). A scanner's rules change between releases, so an unpinned tool
would change findings under you: the auditor probes `devskim --version`
before every run and reports an infrastructure failure on any other
version.

The tool requirement is declared **verify-only** — no `AptPackage`: DevSkim
ships as a .NET global tool, not a distro package, and only a pinned
`dotnet tool install` carries the version this auditor was verified against.
Provision the pinned release in your sandbox baseline:

```sh
# baseline bake step (needs the .NET SDK on the image)
DEVSKIM_VERSION=1.0.90
dotnet tool install --global Microsoft.CST.DevSkim.CLI --version "${DEVSKIM_VERSION}"
devskim --version   # must print 1.0.90
```

Ensure the .NET global-tools directory is on the sandbox `PATH`
(`~/.dotnet/tools` for the sandbox user).

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.devskim"],
      "Enabled": ["codeybox.devskim"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.devskim" }
```

The `devskim` tool requirement is only contributed to baseline provisioning
while the plugin is enabled.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.devskim`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `1.0.90` | Pinned DevSkim release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `TrustRepositorySuppression` | `false` | When `true`, omits `--disable-supression` so repo-authored suppression comments take effect. Default keeps the audit subject from silencing the scan. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact DevSkim rule ids to keep/drop (e.g. `DS126858`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended to `devskim analyze` after the built-in args (never via a shell). Never pass `-E` (redefines the exit convention) or `-f`/`--file-format`/`-O` (would replace or divert the SARIF report the parser expects). |
| `TimeoutSeconds` | `300` | Per-run bound — exceeding it is infrastructure, not a pass. Pattern matching over a full repository is normally seconds; raise it only for very large trees. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. Large repositories can exceed 1 MiB of SARIF — raise the former (up to 64 MiB) rather than wondering where findings went. |

## Default scope

Vendored and dependency trees (`vendor/`, `third_party/`, `node_modules/`)
are excluded by default: matches reported there belong to upstream packages,
not the change under audit, and the noise would teach operators to ignore
the auditor. The exclusion is a finding filter — DevSkim still scans those
paths. Re-include them by overriding `ExcludePaths`.
