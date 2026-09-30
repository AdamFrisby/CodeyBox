# CodeyBox: Vale Prose Linter Auditor

Auditor plugin wrapping [vale](https://vale.sh) (v3, `vale-cli/vale`): it
lints the audited repository's prose files with `vale --output JSON
--no-global .` and reports each alert as an audit finding with the rule id
(vale's check name) and `file:line` location the JSON report supplies.

## What it reports

- One finding per vale alert. The JSON report carries the check in `Check`
  (e.g. `Vale.Spelling`, `write-good.Weasel`), the level in `Severity`
  (`suggestion`/`warning`/`error`), the message in `Message`, and the line in
  `Line` — all preserved on the finding.
- Declared severity mapping (raw levels never reach findings):
  - `error` → `AuditSeverity.Error` (blocking)
  - `warning` → `AuditSeverity.Warning` (advisory)
  - `suggestion` → `AuditSeverity.Info` (advisory)
  - anything else → `AuditSeverity.Error` (fail-closed default)
- **Gate behaviour: hybrid / severity-driven — NOT blocking by default.**
  Only `error`-level findings fail the audit; `warning` and `suggestion`
  findings are advisory. A run reporting only style suggestions passes. To
  make more alerts block, raise their level to `error` in the vale
  configuration — the tool's level is the gate's vocabulary.
- The finding title carries the check name and the message; the description
  carries the tool, rule, tool-reported level, and location.
  `MinimumSeverity`, `IncludedRules`/`ExcludedRules` (matched against check
  names), and `ExcludePaths` are honored through the shared mechanism.

## What it cannot see

- **Prose outside the resolved configuration's sections.** Vale only checks
  files the active configuration has a section for (typically `*.md` and
  friends). Prose embedded in code comments, commit messages, or formats
  without a section is never linted.
- **Repositories without a vale configuration.** Vale cannot run config-less
  (exit `2`); such a run is an infrastructure failure, not a pass — see
  "Configuration" below.
- **Missing styles.** A configuration whose `StylesPath` (or `Packages`) is
  not provisioned fails its rules at load time (exit `2`, infrastructure).
  Run `vale sync` in CI before linting when the configuration names
  `Packages`.
- **Files outside the walked inputs.** `Inputs` replaces the whole-tree `.`
  default; files outside it are never checked.
- **Excluded inputs.** Paths listed in `ExcludePaths` are dropped at finding
  level — but, unlike crawlers with an exclude flag, vale still walks them
  (its `--glob` only narrows by inclusion). Generated or vendored prose is
  still read; only the findings are suppressed.
- **Repository-authored suppression.** The audited tree authors the `.vale.ini`
  ruleset (unless `ConfigPath` overrides it) and inline `<!-- vale off -->`
  comments, which vale honors with no opt-out flag. The auditor cannot
  distinguish a deliberate house style from a subject silencing its own
  findings — an operator-owned `ConfigPath` is the boundary when that
  matters.

## Exit codes and failure classification

Vale's convention (verified against the v3.23.0 source — **not** the common
"1 = findings" pattern, and note the trap: exit `0` still carries findings):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Ran clean of `error`-level alerts — `warning`/`suggestion` alerts may still be in the JSON report | Verdict (`Passed` iff no finding maps to `Error`) |
| `1` | Ran with at least one `error`-level alert | Verdict (blocking when an `error` finding survives filtering) |
| `2` | Vale could not run: missing configuration, a rule that failed to load, an unknown argument | Infrastructure (`AuditUnavailableException`) |
| `0`/`1` with no (or non-report) JSON on stdout | Execution failure, or an `--output`/`--counts` override that changed the report shape | Infrastructure — the JSON parser fails closed |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `vale` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **vale `3.23.0`** (`ExpectedVersion` in scoped
config). A checker's rule implementations, defaults, and report shape change
between releases, so an unpinned tool would change findings under you: the
auditor probes `vale --version` (which prints `vale version X.Y.Z`) before
every run and reports an infrastructure failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`: no distro
apt package carries a version pin. Provision the pinned release in your
sandbox baseline, e.g.:

```sh
# baseline bake step (Linux 64-bit example)
curl -sSL -o vale.tar.gz \
  https://github.com/vale-cli/vale/releases/download/v3.23.0/vale_3.23.0_Linux_64-bit.tar.gz
tar xzf vale.tar.gz -C /usr/local/bin vale
vale --version   # must print "vale version 3.23.0"
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.vale"],
      "Enabled": ["codeybox.vale"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.vale" }
```

The `vale` tool requirement is only contributed to baseline provisioning
while the plugin is enabled.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.vale`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `3.23.0` | Pinned vale release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | `null` | Operator-owned vale configuration file, passed as `--config`. Takes precedence over repository discovery (and over `VALE_CONFIG_PATH`). Ignored when `ExtraArguments` already supplies `--config`. When unset, vale discovers `.vale.ini` / `_vale.ini` / `vale.ini` / `.vale` / `_vale` walking up from the working directory — a tree with no discoverable configuration fails closed as infrastructure. |
| `Inputs` | `.` | Comma-separated vale inputs (files or directories, repo-relative, no `..`) replacing the whole-tree `.` default — e.g. `docs,README.md` to scope to one tree. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). Note this filters findings, not the scan: vale still checks everything and still exits `1` on `error` alerts even if they are filtered from the report. |
| `IncludedRules` / `ExcludedRules` | — | Exact check names to keep/drop (e.g. `Vale.Spelling`, `write-good.Weasel`). |
| `ExcludePaths` | `.git/`, `vendor/`, `third_party/`, `node_modules/`, `.venv/`, `venv/`, `dist/`, `build/`, `out/`, `coverage/`, `bin/`, `obj/`, `target/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Finding-level only: vale has no crawl-time exclusion flag, so excluded trees are still walked (narrow `Inputs` or pass `--glob` in `ExtraArguments` to skip the walk). Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--glob='*.md'` or `--minAlertLevel`. A repeated flag wins over the built-in default — take care: `--output` or `--counts` would replace the JSON report the parser expects and break the run into infrastructure failure. |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

`--no-global` is always passed: user-level configuration outside the worktree
never steers the verdict — the result is a function of the operator
configuration (`ConfigPath`/`ExtraArguments`/baseline environment) and the
repository only.

## Default scope

`vale .` — the whole worktree, with the checked file set decided by the
resolved configuration's sections, minus the ambient user-level config
(`--no-global`). The `ExcludePaths` defaults — `.git/` plus vendored
(`vendor/`, `third_party/`, `node_modules/`, `.venv/`, `venv/`) and generated
(`dist/`, `build/`, `out/`, `coverage/`, `bin/`, `obj/`, `target/`) prefixes —
drop findings that belong to upstream packages or build output rather than
the change under audit. Because vale cannot exclude trees from the walk
itself, those trees are still read during the scan: on very large trees,
narrow `Inputs` (or `--glob`) instead. Prose findings in third-party text or
build output would train operators to ignore the auditor — the defaults keep
them out of the report.
