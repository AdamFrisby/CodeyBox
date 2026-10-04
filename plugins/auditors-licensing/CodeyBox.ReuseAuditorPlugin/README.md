# CodeyBox: REUSE Licence Compliance Auditor

Auditor plugin wrapping [reuse](https://reuse.software) (the FSFE REUSE
helper tool): it runs `reuse lint --json` over the audited repository and
reports each licence-metadata compliance problem as an audit finding with
its rule id (`reuse/<criterion>`) and file location.

## What it reports

- One finding per non-compliant entry. The JSON report carries eight
  criterion lists under `non_compliant` (`bad_licenses`,
  `deprecated_licenses`, `licenses_without_extension`, `missing_licenses`,
  `unused_licenses`, `read_errors`, `missing_copyright_info`,
  `missing_licensing_info`) plus per-file `spdx_expressions` entries with
  `is_valid: false` — each becomes a finding whose rule id is the stable
  `reuse/<criterion>` name (e.g. `reuse/missing-licensing-info`,
  `reuse/invalid-spdx-expression`).
- Path-carrying criteria keep the reported file
  (`src/bad.py`, relativized against the scan root). License-level criteria
  name the identifier in the message, because the JSON report carries only
  the identifier, not the `LICENSES/` file path. The tool reports files
  only — no line numbers — so findings carry a file location with no line.
- Declared severity mapping (the tool reports criteria, not levels, so this
  mapping *is* the severity vocabulary — raw criterion tokens never reach
  findings except in the description's tool-severity line):
  - `missing-licensing-info` → `AuditSeverity.Error` (blocking)
  - `missing-copyright-info` → `AuditSeverity.Error` (blocking)
  - `bad-licenses` → `AuditSeverity.Error` (blocking)
  - `missing-licenses` → `AuditSeverity.Error` (blocking)
  - `invalid-spdx-expression` → `AuditSeverity.Error` (blocking)
  - `read-errors` → `AuditSeverity.Error` (blocking — an unreadable file
    could hide non-compliance)
  - `deprecated-licenses` → `AuditSeverity.Warning` (advisory)
  - `licenses-without-extension` → `AuditSeverity.Warning` (advisory)
  - `unused-licenses` → `AuditSeverity.Info` (advisory)
  - anything else → `AuditSeverity.Error` (fail-closed default)
- **Gate behaviour: hybrid / severity-driven — NOT blocking by default.**
  Only `Error`-level findings fail the audit; `Warning` and `Info`
  findings are advisory. A run reporting only an unused license text
  passes.
- The finding title carries the rule id and the message; the description
  carries the tool, rule, tool-reported criterion, and location.
  `MinimumSeverity`, `IncludedRules`/`ExcludedRules` (matched against
  `reuse/<criterion>` ids), and `ExcludePaths` are honored through the
  shared mechanism.

## What it cannot see

- **Whether the licence metadata is *correct*.** The tool checks that
  copyright and licensing tags exist and parse — not that the named holder
  owns the file or that the chosen licence is the right one. A file tagged
  with the wrong licence passes.
- **Dependency licences.** Only the repository's own files and their SPDX
  tags are checked; the licences of third-party packages pulled in at
  build time need a dependency-vulnerability/SCA auditor, not this one.
- **Files outside the discovered project root.** The tool lints from its
  discovered root (the VCS repository root of the working directory, else
  the working directory). Files outside it are never checked; pass
  `--root` in `ExtraArguments` to move the root deliberately.
- **Excluded inputs.** Paths listed in `ExcludePaths` are dropped at
  finding level — but the tool still walks them (its own ignores live in
  the repository-authored `REUSE.toml`). Vendored or generated
  licence problems are still read; only the findings are suppressed.
- **Repository-authored licensing metadata.** The audited tree authors
  `REUSE.toml` (and the deprecated `.reuse/dep5`), adjacent `.license`
  files, and the SPDX tags themselves. The auditor cannot distinguish a
  deliberate project-wide licence grant from a subject silencing its own
  findings — an operator-owned `--root` plus `IncludedRules` in
  `ExtraArguments`/scoped config is the boundary when that matters.

## Exit codes and failure classification

Reuse's convention (verified against the v6.2.0 source,
`src/reuse/cli/lint.py:127` — `sys.exit(0 if report.is_compliant else 1)`):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Project is REUSE-compliant (empty `non_compliant` section) | Pass (no findings) |
| `1` | Project is not compliant (at least one criterion non-empty) | Verdict (blocking iff an `Error` finding survives filtering) |
| `0`/`1` with no (or non-report) JSON on stdout | Execution failure, or an output-flag override that changed the report shape | Infrastructure — the JSON parser fails closed |
| JSON with an unknown `non_compliant` criterion key | Report shape changed under the pin | Infrastructure — unknown criteria fail closed rather than silently shrinking the verdict |
| `2` | Click usage error (bad arguments) | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `reuse` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **reuse `6.2.0`** (`ExpectedVersion` in scoped
config). Criteria, defaults, and report shape change between releases, so
an unpinned tool would change findings under you: the auditor probes
`reuse --version` before every run and reports an infrastructure failure
on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`: no
distro apt package carries a version pin. Provision the pinned release in
your sandbox baseline, e.g.:

```sh
# baseline bake step (example)
pipx install "reuse==6.2.0"
reuse --version   # must print a 6.2.0 version string
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.reuse"],
      "Enabled": ["codeybox.reuse"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.reuse" }
```

The `reuse` tool requirement is only contributed to baseline provisioning
while the plugin is enabled.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.reuse`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `6.2.0` | Pinned reuse release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). Note this filters findings, not the scan: the tool still walks everything and still exits `1` on any non-compliance even if it is filtered from the report. |
| `IncludedRules` / `ExcludedRules` | — | Exact `reuse/<criterion>` ids to keep/drop (e.g. `reuse/missing-licensing-info`, `reuse/unused-licenses`). |
| `ExcludePaths` | `.git/`, `vendor/`, `third_party/`, `node_modules/`, `.venv/`, `venv/`, `dist/`, `build/`, `out/`, `coverage/`, `bin/`, `obj/`, `target/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Finding-level only: the tool has no crawl-time exclusion flag of its own, so excluded trees are still walked (pass `--root` in `ExtraArguments` to skip the walk). Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--root` or `--no-multiprocessing`. A repeated output flag (`--plain`, `--lines`, `--quiet`, or a second `--json`) wins over the built-in default — take care: it would replace the JSON report the parser expects and break the run into infrastructure failure. |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Default scope

`reuse lint --json` from the audit working directory — the whole project
tree the tool discovers (VCS root, else the working directory), with the
checked files limited to what the repository's own `REUSE.toml` / SPDX
tags cover, and with vendored/generated findings (`vendor/`,
`third_party/`, `node_modules/`, `.venv/`, `venv/`, `dist/`, `build/`,
`out/`, `coverage/`, `bin/`, `obj/`, `target/`, plus `.git/`)
suppressed at finding level. Those prefixes are noise by default:
licensing metadata under them belongs to upstream packages, virtual
environments, or build output — not the change under audit — and
reporting it trains operators to ignore the auditor. Operators working
in a monorepo that vendors first-party code re-include paths by
overriding `ExcludePaths`, or narrow the walk with `--root` in
`ExtraArguments`.

## Config file the tool expects

`REUSE.toml` in the audited repository root (optional). When present it
supplies project-wide copyright/licensing grants and path ignores that
the tool honors; when absent every file needs its own SPDX tags. It is
repository-authored configuration — see "What it cannot see" above.
