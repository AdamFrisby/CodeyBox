# CodeyBox: Mypy Python Type Checker Auditor

Auditor plugin wrapping [mypy](https://mypy-lang.org): it type-checks the
audited repository with `mypy --output json --no-color-output
--no-error-summary --cache-dir /dev/null .` and reports each diagnostic as an
audit finding with the mypy error code (e.g. `assignment`, `arg-type`,
`name-defined`) and `file:line` location.

## What it reports

- One finding per mypy diagnostic line. The title carries the error code and
  the first line of the message; the description carries the tool, code,
  tool-reported severity, location, the full message, and any `hint` text
  mypy attached. `Location` is `path:line` from the diagnostic's `file`/`line`
  fields; mypy reports repo-relative paths when the target is `.`.
- **Gate behaviour: blocking by default.** mypy's severities go through a
  declared map, never raw: `"error"` → `Error` (fails the audit);
  `"note"` → `Info` (advisory — e.g. `reveal_type` output or supplementary
  context attached to an error); anything unrecognised → `Warning`. Which
  checks produce errors comes from the mypy configuration in force — the
  repo's `mypy.ini`/`[tool.mypy]` by default. `MinimumSeverity` can only
  drop findings, so the intended way to harden the gate is the config itself
  (`strict = True`, `enable_error_code`, `disable_error_code`).

## What it cannot see

- **Type errors that need the project's dependencies.** mypy resolves
  imports against the environment it runs in. A repo whose third-party
  packages are not installed in the audit sandbox produces
  `import-not-found`/`import-untyped` findings for those imports and
  silently *loses* type information downstream of them — the analysis is
  incomplete, not clean. Provision the project's dependencies (e.g. `pip
  install -r requirements.txt`) into the sandbox baseline for full coverage,
  or accept the degraded analysis.
- **Code paths guarded by runtime conditions.** mypy is static analysis: it
  sees types, not values. Monkey-patching, dynamic imports, and
  `getattr`/`setattr` tricks are invisible to it.
- **Repository-controlled suppression.** mypy honors `# type: ignore`
  comments, `# mypy:` file-level directives, and its own config files —
  all authored inside the audited repository — and offers no flag to make
  them inert. The subject can therefore weaken or silence checks
  (`ignore_errors = True`, `disable_error_code`, inline ignores); this is
  a documented limitation, not a gate. Operators who need an operator-owned
  ruleset pin one outside the repository via `ConfigPath` (or
  `--config-file` in `ExtraArguments`) — note that inline ignores remain
  honored even then.
- **Findings under excluded prefixes.** `ExcludePaths` is a finding filter —
  mypy still checks those files, but findings under `vendor/`,
  `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/`,
  `.venv/`, `venv/`, `.tox/`, `.mypy_cache/` are dropped. Override
  `ExcludePaths` to re-include them.
- **Anything mypy cannot parse as a completion.** Exit 2 is infrastructure
  (see below), so blocker diagnostics like syntax errors surface as "the
  check did not complete" rather than as findings — the tool's own error
  text is preserved in the failure detail.

## Exit codes and failure classification

mypy's convention (verified against v1.18.x) is **not** the usual linter
table:

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Analysis completed: clean, or notes only | Verdict (pass; notes report as `Info` findings) |
| `1` | Analysis completed with error-level diagnostics | Verdict (`Passed = false` — every `error` maps to `Error`) |
| `2` | Analysis did **not** complete: blocker errors ("errors prevented further checking", e.g. syntax errors — emitted as plain text, not JSON), bad flags, unreadable config, **or no Python sources found** | Infrastructure (`AuditUnavailableException`), never a verdict |
| `0`/`1` with a non-JSON line on stdout | Foreign output — crash, truncation, an operator `--output` override | Infrastructure — the JSONL parser fails closed |
| `1` with no diagnostics on stdout | Reported errors but produced no report | Infrastructure |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

Exit 2 is deliberately not a verdict even though blocker diagnostics are
real problems: upstream's own message is "errors prevented further
checking", so the run is not a complete analysis of the diff — and the
plain-text blocker lines cannot be told apart from a crash's output anyway.
The failure detail carries mypy's message, so the offending file is still
visible to the operator.

A missing `mypy` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **mypy `1.18.1`** (`ExpectedVersion` in scoped
config; `--output json` requires mypy ≥ 1.11). A type checker's rules,
defaults, and JSON shape change between releases, so an unpinned tool would
change findings under you: the auditor probes `mypy --version` before every
run and reports an infrastructure failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`: the
distro package is unpinned and typically too old for `--output=json`.
Provision the pinned release (plus a Python 3 interpreter) in your sandbox
baseline:

```sh
# baseline bake step
pipx install mypy==1.18.1   # or: pip install mypy==1.18.1
mypy --version            # must print mypy 1.18.1 (compiled: ...)
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.mypy"],
      "Enabled": ["codeybox.mypy"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.mypy" }
```

The `mypy` tool requirement is only contributed to baseline provisioning
while the plugin is enabled.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.mypy`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `1.18.1` | Pinned mypy release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | `null` | Path passed to `--config-file` — an operator-pinned mypy config outside the repository, or a repo file overriding the `mypy.ini`/`.mypy.ini`/`setup.cfg`/`pyproject.toml` lookup. Ignored when `ExtraArguments` already supplies `--config-file`. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact mypy error codes to keep/drop (e.g. `assignment`, `import-not-found`, `unused-ignore`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/`, `.venv/`, `venv/`, `.tox/`, `.mypy_cache/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--strict`, `--python-version`, or explicit targets. A repeated flag wins over the built-in default — take care: `--output` would replace the JSON report the parser expects and break the run into infrastructure failure. |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. A repository-wide scan on a large tree should be given a budget that reflects the tree — mypy is not fast on cold caches (the audit always runs it cache-off). |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Default scope

`mypy .` — the repository's own mypy configuration (`mypy.ini`,
`.mypy.ini`, `setup.cfg` `[mypy]`, `pyproject.toml` `[tool.mypy]`) decides
what gets checked through its `files`/`exclude`/`packages` settings; that is
the project's own declaration of checkable scope. mypy's discovery already
skips hidden directories (so `.venv/` etc. are not scanned even though they
sit in the default `ExcludePaths` — those entries are a finding-level
backstop for configs that opt back in). On top of that, findings under
vendored (`vendor/`, `third_party/`, `node_modules/`) and generated
(`dist/`, `build/`, `out/`, `coverage/`) prefixes are dropped by default:
violations there belong to upstream packages or build output, not the change
under audit — reporting them produces noise that trains operators to ignore
the auditor. Re-include a prefix by overriding `ExcludePaths`.

A repository with no Python sources is **not** a pass: `mypy .` exits 2
("There are no .py[i] files in directory '.'"), which is an infrastructure
failure. Enable this auditor only on projects where Python type checking is
a meaningful gate.
