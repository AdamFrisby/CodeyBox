# CodeyBox: gosec Go Security Auditor

Auditor plugin wrapping [gosec](https://github.com/securego/gosec)
(`gosec`): it runs gosec's AST/SSA security rules (`G101`–`G7xx`, each
mapped to a CWE) over the audited repository
(`gosec -fmt json -out <scratch>/gosec-report.json -stdout -verbose sarif
-nosec -exclude-generated ./...`), reporting each issue as an audit
finding with the gosec rule id (e.g. `G404` for weak randomness) and
`file:line` location.

Each run produces two renderings of the same `ReportInfo`: **SARIF to
stdout** — the report of record the auditor parses — and **JSON to a
per-run file** — the completeness oracle carrying the `"Golang errors"`
section the SARIF writer drops (see *Exit codes* below). A scan failure
(missing binary, missing Go toolchain, unloadable packages) is
infrastructure — never a pass.

## What it reports

- One finding per gosec issue. The title carries the rule id and the
  first line of the issue text; the description carries the tool, rule,
  tool-reported severity, location, and the full message. `Location` is
  `path:startLine`; paths are relative to the scanned root (`./...` keeps
  them repo-relative).
- **Gate behaviour: hybrid / severity-driven — not blocking on every
  finding.** gosec's native `HIGH`/`MEDIUM`/`LOW` vocabulary is recovered
  from each rule's `properties.tags` (gosec flattens MEDIUM and HIGH both
  to SARIF `error`, so the raw `level` alone cannot tell them apart) and
  mapped declaratively: `HIGH` → `Error` (fails the audit), `MEDIUM` →
  `Warning` (advisory), `LOW` → `Info` (informational), unrecognised →
  `Warning`. `MinimumSeverity` can only drop findings, it never raises
  them. The auditor is therefore a merge gate for high-severity Go
  security issues — injection, weak crypto, hardcoded credentials — not a
  blocker on every low hint.
- SARIF results embed a code snippet of the offending lines, so findings
  and raw output can contain the reported source context. That is the
  point of a SAST finding; treat audit reports accordingly.

## What it cannot see

- **Anything outside Go.** gosec analyzes Go packages through
  `go list`/`go/packages`; a repository with no Go packages fails closed
  ("No packages found" → exit 1 with no report → infrastructure) rather
  than passing — enable this auditor only for Go projects.
- **Packages it cannot load or type-check.** gosec records per-package
  failures in its errors map and *continues* — and exit 1 cannot say
  whether it came from findings or from those errors. The auditor reads
  the `"Golang errors"` section of the JSON side-report and fails closed
  as infrastructure whenever it is non-empty, **even when findings were
  also produced**: a partial scan cannot certify the diff. A module graph
  that cannot resolve (missing vendor/, empty module cache, no network)
  lands here. Remediate by fixing the failing package, supplying
  `BuildTags`, or narrowing `Targets`.
- **Runtime-only behavior.** gosec is a static pattern/taint matcher over
  a fixed rule catalog: it does not see configuration values, deployed
  environments, or runtime input shapes, and rules not in the catalog
  (business-logic auth, for example) do not exist to it.
- **`_test.go` files by default** (`IncludeTests` opts in) and generated
  files by default (`IncludeGeneratedCode` opts in).
- **Suppression is inert — and cannot be enabled.** gosec honors
  `#nosec`/`//gosec:disable` comments, but its SARIF and JSON writers do
  not drop suppressed issues from the report, so a suppression comment
  cannot hide a finding from this auditor regardless of configuration.
  The scan also passes `-nosec` explicitly.

## Exit codes and failure classification

gosec's convention (verified against `cmd/gosec/main.go` at 2.29.0 — do
not assume the common "0 clean / 1 findings / 2 error" convention holds):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Scan completed; no unsuppressed issues **and** no processing errors | Verdict (pass) |
| `1`, JSON oracle has `"Golang errors"` entries | One or more packages failed to load/type-check — partial scan (findings may also be present) | Infrastructure |
| `1`, oracle clean, SARIF has results | Ran and found problems | Verdict (findings; `Passed = false` when any maps to `Error`) |
| `1`, oracle clean, SARIF empty | Impossible contract state (exit reason unverifiable) | Infrastructure |
| `1`, no/invalid JSON or SARIF | Startup failure: bad flags, unreadable `-conf`, no packages, report write failure | Infrastructure |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `gosec`, a missing `go` toolchain, an unreadable side-report,
and a report exceeding the capture bound are all infrastructure failures
naming the tool — never a passing audit.

## Argument ordering — why ExtraArguments are patterns, not flags

gosec uses Go's stdlib `flag` package, which stops parsing at the first
positional argument. The package patterns come last in argv; operator
`ExtraArguments` therefore land **after** them and act as additional
package patterns — never as flags. Flag-shaped entries are rejected
deterministically before the scan (use the scoped keys below instead).

## Go toolchain and module graph

gosec loads and type-checks packages through `go list`, so the baseline
needs **both** `gosec` and a `go` toolchain, and the module graph must
resolve **offline** (the auditor declares no network capability): commit
`vendor/` or pre-seed the module cache in the sandbox image. The
`GOSEC_AI_PROVIDER` / `GOSEC_AI_API_KEY` / `GOSEC_AI_BASE_URL` ambient
activation path for gosec's AI-autofix feature is removed from the tool
process environment.

## Version pinning

Pinned to **gosec `2.29.0`** (`ExpectedVersion` in scoped config). A
scanner's rules and report shape change between releases, so an unpinned
tool would change findings under you: the auditor probes
`gosec -version` before every run and reports an infrastructure failure
on any other version.

The tool requirements are **verify-only** — no `AptPackage`: gosec and Go
ship as `go install` modules or release tarballs, and no distro package
carries the pin. Provision in the sandbox baseline:

```sh
# baseline bake step (needs a Go toolchain on the image)
go install github.com/securego/gosec/v2/cmd/gosec@v2.29.0
gosec -version   # must print Version: 2.29.0
```

(Or download the `gosec_2.29.0_linux_<arch>.tar.gz` release artifact and
verify it against the published checksums file before unpacking.)

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
| `ExpectedVersion` | `2.29.0` | Pinned gosec release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `Targets` | `./...` | Comma-separated gosec package patterns (positional args, after all flags). Repo-relative only; narrowing below the worktree root makes findings carry target-root-relative paths. |
| `ConfigPath` | — | Operator-owned gosec `-conf` JSON file (rule settings/globals). Canonicalized in the sandbox and rejected when it resolves inside the audited worktree — use an absolute path outside the repository. gosec reads no repo config file, so this is the only config surface. |
| `IncludeTests` | `false` | Pass `-tests` to also analyze `*_test.go` files. |
| `IncludeGeneratedCode` | `false` | Omit `-exclude-generated` to also analyze `// Code generated … DO NOT EDIT` files. |
| `BuildTags` | — | Comma-separated Go build tags (`-tags`) for files behind build constraints. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact gosec rule ids to keep/drop (e.g. `G404`). Output filters; they do not change which rules run. Scan-time rule selection needs `-include`/`-exclude` inside an operator `ConfigPath` file. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan (gosec's own default `-exclude-dir` already skips `vendor/` and `.git`). Setting it replaces the default list. |
| `ExtraArguments` | — | Additional **package patterns** appended after `Targets` — never flags (see *Argument ordering*). |
| `TimeoutSeconds` | `300` | Per-run bound — exceeding it is infrastructure, not a pass. A full-module scan on a large tree can take minutes; raise it before assuming a hang. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. Large repositories can exceed 1 MiB of SARIF — raise the former (up to 64 MiB) rather than wondering where findings went. |

## Default scope

- `vendor/`, `third_party/`, `node_modules/` findings are dropped:
  issues there belong to upstream code, not the diff under audit. gosec
  additionally skips `vendor/` and `.git/` at scan time via its built-in
  `-exclude-dir` defaults.
- Generated files (`// Code generated … DO NOT EDIT`) are skipped via
  `-exclude-generated`: they are machine-written, so findings there are
  not actionable on the diff. `IncludeGeneratedCode` opts back in.
- Test files are skipped via gosec's default (`IncludeTests` opts in):
  issues reachable only from tests are not shipped code.
