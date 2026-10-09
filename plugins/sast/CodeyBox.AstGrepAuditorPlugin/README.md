# CodeyBox: ast-grep Structural SAST Auditor

Auditor plugin wrapping the [ast-grep CLI](https://ast-grep.github.io)
(`ast-grep`): it runs ast-grep's structural AST pattern-matching rules over
the audited repository (`ast-grep scan --format sarif`), reporting each match
as an audit finding with the rule id (e.g. `fixture-dangerous-call`) and
`file:line` location.

Each run is a single tool invocation: the analysis streams SARIF to stdout
for parsing. A scan failure (missing binary, missing or invalid project
configuration, invalid rule) is infrastructure — never a pass.

## What it reports

- One finding per ast-grep match. The title carries the rule id and the
  first line of the match message; the description carries the tool, rule,
  tool-reported severity, location, and the full message. `Location` is
  `path:startLine`; paths are relative to the repository root.
- **Gate behaviour: hybrid / severity-driven — not blocking on every
  finding.** ast-grep severities go through a declared map, never raw:
  `error` → `Error` (fails the audit); `warning` → `Warning` (advisory);
  `info` / `hint` (both surface in SARIF as `note`) → `Info`
  (informational); anything unrecognised → `Warning`.
  `MinimumSeverity` can only drop findings, it never raises them. A rule's
  severity is set by the ruleset author, so the gate blocks exactly the rules
  the chosen ruleset marks `error`.
- SARIF embeds code snippets around each location, so findings and raw
  output can contain the reported source context. That is the point of a
  SAST finding; treat audit reports accordingly.

## What it cannot see

- **Rules it was not given.** ast-grep ships no embedded rules — a scan
  checks exactly what the resolved project contains. A clean run proves the
  configured rules found nothing; it is not a clean bill of health. Choose
  the ruleset deliberately.
- **Anything the rules cannot parse.** A source file that a rule's language
  cannot parse is skipped by that rule (the scan still completes; only the
  exit code for configuration and rule errors is non-zero). Partial parse
  coverage is a normal structural-scanner limitation — treat it as reduced
  coverage, not a defect of the run.
- **Subject-authored configuration, by design.** The suppressible surface of
  a scan is the project itself — rule selection, severities, ignore globs —
  so a repository-resolved project is authored by the audit subject. Prefer
  operator `Config` pointing outside the worktree where the ruleset must not
  be subject-authored.
- **Inline `ast-grep-ignore` comments (residual subject-controlled bypass).**
  An `ast-grep-ignore` or `ast-grep-ignore: rule-id` comment in scanned
  source suppresses findings on the same or following line (or the whole
  file from the first line), including `error`-severity (blocking) rules.
  The pinned `0.45.3` `ast-grep scan` offers no flag disabling them
  (`--no-ignore` covers only ignore files; the remaining scan flags are
  target and severity selectors), so the auditor passes no such flag.
  Operator `Config` does not mitigate this surface because the comments live
  in the scanned source, not the project — treat a clean verdict as
  "no unsuppressed matches", not proof the subject suppressed nothing.
- **Findings under excluded prefixes.** `ExcludePaths` is a finding filter —
  ast-grep still scans those files (minus what ignore files exclude), but
  findings under `vendor/`, `third_party/`, `node_modules/` are dropped.
  Override `ExcludePaths` to re-include them.

## Project resolution

ast-grep exits `3` with no project configuration, so the auditor resolves
the `-c` project in order:

1. **`Config` in scoped config** — a single path to an `sgconfig.yml`
   project file (a path outside the audited worktree, mounted by the sandbox
   baseline, is the way to run a project the audit subject cannot edit).
2. **Repository convention** — when `Config` is unset the auditor probes the
   worktree root each run for `sgconfig.yml` and `sgconfig.yaml` and passes
   the first present one explicitly as `-c` (so a configuration from a
   parent of the worktree can never leak in). This is the config file the
   tool expects in the repository under audit; note the project then comes
   from the audit subject itself — use operator `Config` where the ruleset
   must not be subject-authored.

Neither resolving is a **deterministic infrastructure failure** naming both
options — ast-grep without a project is a scanner that did not run, never a
pass.

## Exit codes and failure classification

ast-grep's convention (verified against `0.45.3` — not the
gitleaks/eslint default: only error-severity matches flip the exit code).
The verdict always comes from the SARIF document, never the exit code alone:

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Scan completed: clean, or only warning/info/hint matches | Verdict (findings from the SARIF document; `Passed = false` when any finding maps to `Error`) |
| `1` | Scan completed with error-severity matches | Verdict — findings from the SARIF document |
| `2` | CLI usage error | Infrastructure (`AuditUnavailableException`) |
| `3` | No project configuration found | Infrastructure |
| `6` | Unreadable configuration file | Infrastructure |
| `8` | Invalid rule file | Infrastructure |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `ast-grep` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **ast-grep `0.45.3`** (`ExpectedVersion` in scoped
config). A scanner's rules and output shape change between releases, so an
unpinned tool would change findings under you: the auditor probes
`ast-grep --version` before every run (the CLI prints `ast-grep X.Y.Z`) and
reports an infrastructure failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`: ast-grep
ships through language channels (npm, cargo, pip, brew), not as a distro
package, and only a pinned install carries the version this auditor was
verified against. Provision the pinned release in your sandbox baseline:

```sh
# baseline bake step (pick one channel, pinned to ExpectedVersion)
AST_GREP_VERSION=0.45.3
npm install --global "@ast-grep/cli@${AST_GREP_VERSION}"
# or: cargo install ast-grep --locked --version "${AST_GREP_VERSION}"
ast-grep --version                            # must print ast-grep 0.45.3
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.ast-grep"],
      "Enabled": ["codeybox.ast-grep"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.ast-grep" }
```

The `ast-grep` tool requirement is only contributed to baseline provisioning
while the plugin is enabled.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.ast-grep`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `0.45.3` | Pinned ast-grep release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `Config` | repository `sgconfig.*` | Path to an `sgconfig.yml` project file passed as `-c` — including a path outside the worktree. Replaces repository probing entirely. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids to keep/drop. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended to `ast-grep scan` after the built-in args (never via a shell). Never pass `--json` or `--format` (would replace or divert the SARIF report the parser expects), nor `--stdin` / `--interactive` / `--update-all` (interactive or mutating modes have no place in an audit). `--filter`, `--min-severity`, `--globs`, or `--no-ignore` are reasonable additions. |
| `TimeoutSeconds` | `300` | Per-run bound — exceeding it is infrastructure, not a pass. Raise it for very large trees. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. Large repositories can exceed 1 MiB of SARIF — raise the former (up to 64 MiB) rather than wondering where findings went. |

## Default scope

Vendored and dependency trees (`vendor/`, `third_party/`, `node_modules/`)
are excluded by default: matches reported there belong to upstream packages,
not the change under audit, and the noise would teach operators to ignore
the auditor. The exclusion is a finding filter layered on ast-grep's own
ignore-file-aware target selection. Re-include them by overriding
`ExcludePaths`.
