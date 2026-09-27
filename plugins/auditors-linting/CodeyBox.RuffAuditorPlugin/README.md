# CodeyBox: Ruff Python Linter

Auditor plugin wrapping [Ruff](https://docs.astral.sh/ruff/): it analyses the
audited repository with `ruff check --output-format sarif --no-cache
--ignore-noqa .` and reports each diagnostic as an audit finding with the
Ruff rule code (e.g. `F401`, `E501`, `I001`) and `file:line` location.
Python analysis only.

## What it reports

- One finding per Ruff diagnostic. The title carries the rule code and the
  first line of the message (e.g. "F401: 'os' imported but unused"); the
  description carries the tool, rule, tool-reported level, location, and the
  full message. `Location` is `path:startLine`.- **Syntax errors** (`invalid-syntax`, e.g. a `.py` file that does not parse)
  are findings too — level `error`, so they fail the audit rather than
  vanishing as tool noise.
- **Gate behaviour: blocking — every finding fails the audit.** Ruff reports
  every diagnostic at SARIF level `error` (verified against 0.14.7), which
  maps to `Error`. The declared map covers the wider level vocabulary anyway
  (see below), never passing raw levels through. `MinimumSeverity` is honored
  but inert while ruff reports a single level — there is no advisory-only
  mode for this auditor.

## What it cannot see

- **Non-Python files.** Ruff checks Python (`.py`, `.pyi`, and configured
  notebook mappings) — everything else produces no findings. A repository
  with no checkable files is a clean pass (empty SARIF `results`), not an
  error.
- **Files ruff excludes.** Ruff's own default excludes (`.venv/`, `venv/`,
  `dist/`, `build/`, `node_modules/`, `__pycache__/`, …), `.gitignore`
  integration, and the repository configuration's `exclude` list decide the
  analysed set. The scan adds `--exclude` only via operator `ExtraArguments`.
- **Suppressed violations (by default, none).** The scan passes
  `--ignore-noqa`, so `# noqa` comments authored in the audited tree are
  inert — the subject cannot silence findings line-by-line. Set
  `TrustRepositorySuppression: true` to honor them.
- **Absolute locations.** Ruff reports absolute `file://` artifact URIs and
  the shared SARIF parser preserves them (scheme-stripped), so `Location`
  carries the sandbox-absolute path (e.g. `tmp/audit-xyz/src/app.py:3`)
  rather than a repo-relative one, and repo-relative `ExcludePaths` prefix
  entries cannot match them. The `ExcludePaths` defaults stay as documented
  intent and filter any relative paths; ruff's own default excludes do the
  primary vendored/generated filtering at scan time. Operators narrow scope
  further with `--exclude <pattern>` in `ExtraArguments` or the repository
  `exclude` list (visible in the audited diff).
- **Formatter drift.** The scan is `ruff check`, not `ruff format --check`:
  formatting drift never becomes a finding. Gate formatting separately if you
  want it.
- **The repository ruleset's blind spots.** Only the rules the configuration
  selects are checked — ruff's default selection (`E4`, `E7`, `E9`, `F`) is
  narrow. A clean audit against defaults says nothing about rules the repo
  never enabled. Operators who want a fixed bar pass `--select` in
  `ExtraArguments` or pin an operator-owned config (see below).
- **More than `MaxFindings` diagnostics.** Findings beyond `MaxFindings`
  (default 1000) are dropped and the truncation is reported in the raw
  output.

## Exit codes and failure classification

Ruff's convention (verified against 0.14.7 — **not** assumed from the common
"0 clean / 1 findings / 2 error" table):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Checked clean (empty SARIF `results`) | Verdict (pass) |
| `0` with SARIF results | Only via operator `--exit-zero`: violations still reported | Verdict (`Passed = false` — findings still fail; the flag cannot silence the gate) |
| `1` with SARIF on stdout | Checked, violations found | Verdict (`Passed = false` — every finding maps to `Error`) |
| `1` with no SARIF on stdout | Ran but emitted no report | Infrastructure — the SARIF parser fails closed |
| `2` | Could not run: bad flags (including a repeated flag), unreadable `--config`, config errors — stdout is empty | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `ruff` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **Ruff `0.14.7`** (`ExpectedVersion` in scoped
config). A linter's rules change between releases, so an unpinned tool would
change findings under you: the auditor probes `ruff --version` before every
run and reports an infrastructure failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`: ruff
ships via pip and as a standalone binary, and no distro package carries a
version pin. Provision the pinned release in your sandbox baseline **only
when this plugin is enabled**:

```sh
# baseline bake step (either distribution; standalone needs no Python)
pip install ruff==0.14.7
ruff --version   # must print ruff 0.14.7
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates, and baseline provisioning verifies `ruff` only in that state:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.ruff"],
      "Enabled": ["codeybox.ruff"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.ruff" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.ruff`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `0.14.7` | Pinned ruff release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | `null` | Path passed to `--config` — an operator-pinned `ruff.toml` outside the repository, or a repo file overriding ruff's default discovery. Ignored when `ExtraArguments` already supplies `--config`. |
| `TrustRepositorySuppression` | `false` | When `false` (default) the scan passes `--ignore-noqa`, so `# noqa` comments authored in the audited tree are inert. When `true`, ruff honors them. The config *file* is repo-authored either way (see below). |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). Inert while ruff reports a single level — kept for a future ruff emitting more levels. |
| `IncludedRules` / `ExcludedRules` | — | Exact ruff rule codes to keep/drop (e.g. `F401`, `E501`, `I001`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/`, `.venv/`, `venv/`, `__pycache__/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan; see the absolute-location limit above. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--select <rules>`, `--ignore <rules>`, `--exclude <pattern>`, `--isolated` (ignore all repo config files — pair with `--select`), or `--target-version`. A repeated `--output-format` replaces the SARIF report the parser expects and breaks the run into infrastructure failure; `--output-file` redirects the report away from stdout with the same effect. Ruff rejects repeated single-occurrence flags (exit 2), so the built-in `--output-format`, `--no-cache`, `--ignore-noqa`, and `--config` defer to the operator's own setting. |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

**Repository-controlled suppression is off by default.** The audit subject
writes the repository, and ruff lets source files suppress the linter inline
(`# noqa: F401` drops the finding; bare `# noqa` drops all findings on the
line). The scan therefore passes `--ignore-noqa` unless
`TrustRepositorySuppression: true` is set — findings then appear for code the
comments would have suppressed. Expect *more* findings than a local
`ruff check` on repos that rely on `noqa`; that is the gate working as
intended.

The larger suppression surface is the ruff configuration itself
(`ruff.toml`, `.ruff.toml`, `pyproject.toml [tool.ruff]`): it is
repo-authored, and its `select`/`ignore`/`exclude`/`per-file-ignores` are
honored because the project's own lint contract is the meaningful check —
changes to it are visible in the audited diff. The auditor runs under
`AuditCapabilities.None` (no agent credentials, no network). For a fully
operator-owned gate, pass `--isolated` plus `--select` in `ExtraArguments`
(ruff then ignores every repository config file) or pin an out-of-repo file
via `ConfigPath`.

## Default scope

`ruff check .` — ruff's own default excludes plus the repository
configuration decide what gets checked; that is the project's own declaration
of checkable scope. The scan adds `--no-cache` so it never writes
`.ruff_cache` into the audited tree. On top of that, the finding-level
`ExcludePaths` backstop lists vendored (`vendor/`, `third_party/`,
`node_modules/`, `.venv/`, `venv/`) and generated (`dist/`, `build/`,
`out/`, `coverage/`, `__pycache__/`) prefixes: violations there belong to
upstream packages or build output, not the change under audit — reporting
them produces noise that trains operators to ignore the auditor. Because ruff
reports absolute artifact URIs (see above), the backstop cannot match those
prefixes in practice; the effective default filtering is ruff's own excludes
at scan time. Re-include a path by overriding `ExcludePaths`, or narrow the
scan with `--exclude` in `ExtraArguments`.
