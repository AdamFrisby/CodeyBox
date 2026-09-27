# CodeyBox: Pyright Python Type Analysis

Auditor plugin wrapping [pyright](https://microsoft.github.io/pyright/):
it analyses the audited repository with `pyright --outputjson` and reports
each diagnostic as an audit finding with the pyright rule identifier (e.g.
`reportAssignmentType`, `reportUndefinedVariable`) and `file:line` location.

## What it reports

- One finding per pyright diagnostic. The title carries the rule id and the
  first line of the message (e.g. `reportAssignmentType: Type "str" is not
  assignable to declared type "int"`); the description carries the tool,
  rule, tool-reported level, location, and the full message. `Location` is
  `path:startLine` — the report's `file` is an absolute path relativized
  against the audited worktree root, and `range.start.line` is converted
  from pyright's 0-based numbering. Diagnostics that carry no `rule` (some
  syntax/config-level errors) still produce findings; diagnostics outside
  the worktree keep their absolute path so nothing is silently rewritten.
- **Gate behaviour: hybrid / severity-driven — not blocking on every
  finding.** Pyright's severities go through a declared map, never raw:
  `error` → `Error` (fails the audit); `warning` → `Warning` (advisory);
  `information` → `Info`. Which diagnostics are errors comes from the
  type-checking ruleset in force — the audited repository's
  `pyrightconfig.json` / `[tool.pyright]` section, or an operator-pinned
  config via `ProjectPath`. `MinimumSeverity` can only drop findings, so the
  intended way to harden the gate is raising rule severities in the ruleset.

## What it cannot see

- **Non-Python code.** Only `.py`/`.pyi` files pyright includes in the
  project are analyzed; other languages produce no findings.
- **Code outside pyright's include set.** With no positional arguments,
  pyright analyzes the project rooted at the worktree and honors the
  repository's `include`/`exclude`/`executionEnvironments` configuration.
- **Unresolved imports are findings, not invisible.** Without a configured
  interpreter or virtualenv, third-party imports surface as
  `reportMissingImports`/`reportMissingModuleSource` diagnostics. If the
  sandbox lacks the project's dependencies, expect those findings — silence
  them by provisioning the environment (`venv`/`venvPath` in the repo's
  config, or `PYRIGHT_PYTHON_*`/extra args) rather than by excluding rules
  blindly.
- **Inline suppressions stay honored.** `# type: ignore` and
  `# pyright: ignore` comments are authored inside the audited repository,
  and pyright has no `--no-inline-config`-style flag to disable them
  (`enableTypeIgnoreComments` is a config-file setting, and the config is
  repo-authored). A violation the diff suppresses inline stays suppressed in
  the audit — a documented limitation, shared with the Biome auditor.
- **Repo-authored config is the contract — and could weaken it.** The
  repository's own `pyrightconfig.json`/`pyproject.toml` decides
  `typeCheckingMode`, rule severities, and the include/exclude sets, so a
  diff could silence the auditor (e.g. `"typeCheckingMode": "off"` or an
  `exclude` that drops real sources). That matches the sibling linters'
  posture — the project's own analysis contract is the meaningful check —
  but a warnings-clean local run that disagrees with the audit is a signal
  to inspect the diff's config and suppression comments. An operator-owned
  ruleset can be pinned via `ProjectPath`, with pyright's caveat: the config
  location becomes the project root, so an out-of-repo config must retarget
  the repository through its own include/execution-environments entries.
- **More than `MaxFindings` diagnostics** are dropped and the truncation is
  reported in the raw output.

## Exit codes and failure classification

Pyright's convention (publicly documented upstream, verified against
v1.1.414 — not assumed from the common "0 clean / 1 findings / 2 error"
table; note in particular that **warnings alone still exit 0**):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Analysis completed, no errors reported (warnings/information may be in the report) | Verdict (pass, or advisory findings) |
| `1` with a JSON `generalDiagnostics` report on stdout | One or more errors reported | Verdict (`Passed = false` when any finding maps to `Error`) |
| `0`/`1` with no parseable JSON on stdout | Not a report — e.g. a usage error printing text | Infrastructure — the parser fails closed |
| `2` | Fatal error, no diagnostics reported | Infrastructure |
| `3` | Configuration file could not be read or parsed | Infrastructure |
| `4` | Illegal command-line parameters | Infrastructure |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `pyright` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **pyright `1.1.414`** (`ExpectedVersion` in scoped
config). A type checker's rules and diagnostic vocabulary change between
releases, so an unpinned tool would change findings under you: the auditor
probes `pyright --version` before every run and reports an infrastructure
failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`: pyright
ships via npm (the `pyright` package bundles its own Node.js runtime) and
the pip `pyright` package is a wrapper that downloads the same npm release;
no distro package carries a version pin. Provision the pinned release in
your sandbox baseline **only when this plugin is enabled**:

```sh
# baseline bake step
npm install -g pyright@1.1.414
pyright --version   # must print "pyright 1.1.414"
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates, and baseline provisioning installs `pyright` only in that state:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.pyright"],
      "Enabled": ["codeybox.pyright"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.pyright" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.pyright`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `1.1.414` | Pinned pyright release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ProjectPath` | `null` | Passed to `--project` — a `pyrightconfig.json` file or a directory containing one. Ignored when `ExtraArguments` already supplies `--project`/`-p`. Caveat: pyright derives the project root (analysis scope) from this location, so an out-of-repo config must retarget the repository via its own include/execution-environments. Positional file arguments via `ExtraArguments` are mutually exclusive with `--project`. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact pyright rule ids to keep/drop (e.g. `reportAssignmentType`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `.venv/`, `venv/`, `.tox/`, `dist/`, `build/`, `out/`, `coverage/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--pythonversion`, `--pythonplatform`, `--venvpath`, `--skipunannotated`, or positional file/directory arguments. A repeated `--outputjson` is harmless; `--warnings` additionally makes warnings exit 1 (still a verdict); `-` reads file lists from stdin. |
| `TimeoutSeconds` | `600` | Per-run bound. Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `4 MiB` / `1000` | Output/result caps; overruns are reported as truncation — a JSON report cut mid-document fails closed as infrastructure. |

## Default scope

`pyright --outputjson` over the whole audited tree, with the repository's
own pyright configuration deciding which files are analyzed. On top of that,
findings under vendored (`vendor/`, `third_party/`, `node_modules/`),
interpreter-environment (`.venv/`, `venv/`, `.tox/`), and generated
(`dist/`, `build/`, `out/`, `coverage/`) prefixes are dropped by default:
diagnostics there belong to installed packages or build output, not the
change under audit — reporting them produces noise that trains operators to
ignore the auditor. Re-include a prefix by overriding `ExcludePaths`.
