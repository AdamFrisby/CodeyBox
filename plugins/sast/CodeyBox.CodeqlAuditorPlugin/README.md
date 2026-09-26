# CodeyBox: CodeQL Deep Dataflow SAST Auditor

Auditor plugin wrapping the [CodeQL CLI](https://codeql.github.com/docs/codeql-cli/)
(`codeql`): it builds a CodeQL database for the audited repository
(`codeql database create`) and runs CodeQL's dataflow security queries
against it (`codeql database analyze --format sarifv2.1.0`), reporting each
alert as an audit finding with the CodeQL rule id (e.g.
`py/command-line-injection`, `js/sql-injection`, `csharp/sql-injection`)
and `file:line` location.

Each run is two tool invocations from one auditor: database creation runs
as a pre-scan step through the same bounded, classified exec path as the
scan, and the analysis streams SARIF to stdout for parsing. A creation
failure (missing toolchain, unbuildable source) is infrastructure —
without a database there is nothing to analyze — never a pass.

## What it reports

- One finding per CodeQL alert. The title carries the rule id and the
  first line of the alert message; the description carries the tool, rule,
  tool-reported severity, location, and the full message. `Location` is
  `path:startLine`; paths are relative to the repository root
  (`--source-root=.`).
- **Gate behaviour: hybrid / severity-driven — not blocking on every
  finding.** CodeQL query severities go through a declared map, never raw:
  `error` → `Error` (fails the audit); `warning` → `Warning` (advisory);
  `note`, `none`, and `recommendation` → `Info` (informational); anything
  unrecognised → `Warning`. CodeQL results carry no per-result level — the
  severity is recovered from each query's rule metadata
  (`defaultConfiguration.level`, falling back to `problem.severity`) — see
  `CodeqlSarifOutputParser`. `MinimumSeverity` can only drop findings, it
  never raises them.
- SARIF includes two-line code snippets around each location by default, so
  findings and raw output contain the reported source context. That is the
  point of a SAST finding; treat audit reports accordingly.

## What it cannot see

- **Languages outside the configured `Language`.** Each run analyzes exactly
  one CodeQL language (default `csharp`); `database create` rejects a
  multi-language database without `--db-cluster`, so there is no
  scan-everything mode. A Python-only finding in a run configured for
  `csharp` is out of scope by construction.
- **Compiled code without its toolchain.** Interpreted languages
  (Python, JavaScript/TypeScript, Ruby) extract directly, but C/C++, C#,
  Go, Java/Kotlin, and Swift need their build toolchain in the audit
  sandbox for autobuild tracing. Without it, database creation fails and
  the run is infrastructure, not a pass.
- **Anything the configured queries don't cover.** With no `QuerySuites`
  configured the analysis runs CodeQL's default queries for the language
  (security plus quality); custom suites/packs go in `QuerySuites`.
- **No repository config file is needed or honored.** Unlike linters,
  CodeQL takes no ruleset file from the audited tree — language and
  queries come from the operator's scoped config (`Language`,
  `QuerySuites`), and suppression-style config files cannot silence the
  scan. There is nothing for the audit subject to edit to weaken it.
- **Findings under excluded prefixes.** `ExcludePaths` is a finding filter —
  CodeQL still extracts those files, but findings under `vendor/`,
  `third_party/`, `node_modules/` are dropped. Override `ExcludePaths` to
  re-include them.
- **The database itself.** It is built per run under a GUID-suffixed
  directory in the system temp area, outside the audited worktree, so the
  scan never pollutes the diff or trips sibling auditors (e.g. file-size
  limits). Sandbox temp areas are discarded with the sandbox; on persistent
  hosts purge stale `codeybox-codeql-*/` directories on a schedule.
- **Non-Linux sandboxes.** The analysis streams SARIF via
  `--output /dev/stdout` (`--output -` does not stream — it creates a
  literal file named `-`, verified against 2.27.1), which needs a
  Linux-style `/dev/stdout`.

## Exit codes and failure classification

CodeQL's convention (verified against 2.27.1 — do not assume the
gitleaks/eslint convention holds here):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Analysis completed — clean or with alerts; the verdict is in the SARIF document | Verdict (`Passed = false` when any finding maps to `Error`) |
| `2` | Could not run: unknown language, missing database, analysis failure (verified) | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

There is deliberately no separate "found something" exit: only `0` is
findings-producing. A missing `codeql` is always an infrastructure failure
naming the tool — never a passing audit.

## Version pinning

The auditor is pinned to **CodeQL `2.27.1`** (`ExpectedVersion` in scoped
config). A scanner's queries change between releases, so an unpinned tool
would change findings under you: the auditor probes `codeql version`
before every run and reports an infrastructure failure on any other
version.

The tool requirement is declared **verify-only** — no `AptPackage`: CodeQL
ships as a release bundle, not a distro package, and the bundle (CLI plus
the query packs a bare CLI download lacks) is the only supported
provisioning source. Provision the pinned release in your sandbox baseline:

```sh
# baseline bake step (adjust arch; verify against the published checksum file)
CODEQL_VERSION=2.27.1
curl -fsSL "https://github.com/github/codeql-action/releases/download/codeql-bundle-v${CODEQL_VERSION}/codeql-bundle-linux64.tar.zst" -o /tmp/codeql-bundle.tar.zst
curl -fsSL "https://github.com/github/codeql-action/releases/download/codeql-bundle-v${CODEQL_VERSION}/codeql-bundle-linux64.tar.zst.checksum.txt" -o /tmp/codeql-bundle.sha256
(cd /tmp && sha256sum -c codeql-bundle.sha256)
mkdir -p /opt/codeql && tar --use-compress-program=unzstd -xf /tmp/codeql-bundle.tar.zst -C /opt/codeql
ln -sf /opt/codeql/codeql/codeql /usr/local/bin/codeql
codeql version   # must print 2.27.1
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.codeql"],
      "Enabled": ["codeybox.codeql"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.codeql" }
```

The `codeql` tool requirement is only contributed to baseline provisioning
while the plugin is enabled.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.codeql`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `2.27.1` | Pinned CodeQL release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `Language` | `csharp` | CodeQL language identifier for database creation (`c-cpp`, `csharp`, `actions`, `go`, `java-kotlin`, `javascript-typescript`, `python`, `ruby`, `rust`, `swift`, plus CodeQL's alternative identifiers `c`, `cpp`, `java`, `kotlin`, `javascript`, `typescript`). Anything else is a deterministic infrastructure failure before anything executes. |
| `QuerySuites` | — | Comma-separated query suites/packs/directories appended to `database analyze`, replacing CodeQL's default queries for the language when set (e.g. `csharp-security-extended.qls`). Paths resolve against the CLI's QL pack search path (the bundle). |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact CodeQL rule ids to keep/drop (e.g. `py/command-line-injection`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended to `database analyze` after the built-in args (never via a shell). They do **not** apply to `database create`. A repeated flag breaks the run into infrastructure failure — take care: `--format` would replace the SARIF report the parser expects, and `--output` would divert it off stdout. |
| `TimeoutSeconds` | `1200` | Per-phase bound, applied separately to database creation and to analysis — a full run may take up to twice this. Exceeding it is infrastructure, not a pass. Deep dataflow over a full repository routinely takes minutes; 300 seconds would fail healthy scans. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. Large repositories can exceed 1 MiB of SARIF — raise the former (up to 64 MiB) rather than wondering where findings went. |

## Default scope

Vendored and dependency trees (`vendor/`, `third_party/`, `node_modules/`)
are excluded by default: alerts reported there belong to upstream packages,
not the change under audit, and the noise would teach operators to ignore
the auditor. The exclusion is a finding filter — CodeQL still extracts
those paths (scan-time exclusion belongs to an operator-pinned query
configuration). Re-include them by overriding `ExcludePaths`.
