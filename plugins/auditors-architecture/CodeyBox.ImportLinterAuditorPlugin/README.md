# CodeyBox: Import Linter Architecture Contracts

Auditor plugin wrapping [Import Linter](https://import-linter.readthedocs.io/):
it checks the audited repository's declared Python import contracts with
`lint-imports --no-logo --no-cache` and reports each broken-contract detail
line as an audit finding with the contract name as the rule id and the
violating module + line as the location. Python import-boundary and
architecture checks only.

## What it reports

- One finding per rendered violation inside the `Broken contracts` report
  section: direct import links (`importer -> imported (l.N)`), undeclared
  layer modules, and chain links. `RuleId` is the contract name (the tool's
  only identifier — contract types are not rendered in the report).
- One advisory finding per entry under the `Warnings` section (e.g. unmatched
  `ignore_imports` entries with `unmatched_ignore_imports_alerting = warn`).
- One fallback finding per broken contract whose detail block renders in a
  shape this parser does not recognise (e.g. custom contract types), so a
  BROKEN verdict can never parse to zero findings.
- **Gate behaviour: blocking for broken contracts, advisory for warnings.**
  Broken-contract entries are reported at tool level `error` → `Error` and
  fail the audit; warnings map to `Warning` and never block on their own.
- **Locations are Python modules, not file paths.** lint-imports reports
  dotted module names, so a finding's `Location` is the module rendered in
  slash form plus the first reported line — `mypkg.high -> mypkg.low (l.1)`
  lands at `mypkg/high:1`, where the file is `mypkg/high.py` or
  `mypkg/high/__init__.py`. ExcludePaths prefixes match this slash form.

## What it cannot see

- **Repositories with no import-linter configuration.** Contracts come from
  `pyproject.toml [tool.importlinter]`, `setup.cfg [importlinter]`, or
  `.importlinter` in the audited repo — `lint-imports` exits 1 with "Could
  not read any configuration." when none exists, which the auditor reports
  as **infrastructure**, never as a pass. Enabling this plugin requires
  declaring at least one contract (and `root_package`/`root_packages`) in
  the repository, or pinning an operator-owned file via `ConfigPath`.
- **Non-Python files and non-import violations.** lint-imports checks the
  static import graph; it sees only what the configured `root_packages`
  cover.
- **Contract types.** The text report renders contract names but not their
  type (`forbidden`, `layers`, `independence`, `protected`,
  `acyclic_siblings`, custom); findings carry the contract name only.
- **Indentation conventions of the checked code.** It sees imports, not
  formatting or lint — pair with a linting auditor for that.
- **More than `MaxFindings` violations.** Findings beyond `MaxFindings`
  (default 1000) are dropped and the truncation is reported in the raw
  output.

## Exit codes and failure classification

lint-imports' convention (verified against import-linter 2.15 — **not** the
common "0 clean / 1 findings / 2 error" table): there are only two exits.

| Exit | Meaning | Classification |
|---|---|---|
| `0` | All contracts kept; report ends `Contracts: N kept, 0 broken.` | Verdict (pass) |
| `1` + `Contracts: … broken.` | Checks ran; broken contracts found | Verdict (`Passed = false`) |
| `1`, no summary line | Could not run: missing/unreadable config, invalid contract options (`Contract "x" is not configured correctly`), unknown `--contract` id, or a caught exception — all rendered as plain error text | Infrastructure (`AuditUnavailableException`) |
| `1`, summary says `0 broken` | Contradictory state — cannot be read as a verdict | Infrastructure |
| `1`, report claims broken but no recognised `Broken contracts` details | Unrecognised report shape | Infrastructure |
| `2` | Click usage error — bad flags | Infrastructure |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `lint-imports` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **import-linter `2.15.0`** (`ExpectedVersion` in
scoped config). Contract types and the rendered report change between
releases, so an unpinned tool would change findings under you: the auditor
probes `lint-imports --version` before every run and reports an
infrastructure failure on any other version. `lint-imports --version` prints
`import-linter 2.15` — a two-part PEP 440 release — which the auditor
zero-pads to `2.15.0` for comparison; always set `ExpectedVersion` in
three-part form.

The tool requirement is declared **verify-only** — no `AptPackage`:
import-linter ships via pip/pipx and no distro package carries a version pin.
Provision the pinned release **only when this plugin is enabled**:

```sh
# baseline bake step (needs Python 3 + pip/pipx on the image)
pip install 'import-linter==2.15.0'   # resolves the 2.15 release
lint-imports --version              # must print import-linter 2.15
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates, and baseline provisioning verifies `lint-imports` only in that state:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.import-linter"],
      "Enabled": ["codeybox.import-linter"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.import-linter" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.import-linter`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `2.15.0` | Pinned import-linter release (three-part form); a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | `null` | Path passed to `--config` — an operator-pinned `.importlinter`/INI or TOML file outside the repository, or a repo file overriding the default discovery (`pyproject.toml`, `setup.cfg`, `.importlinter`). Ignored when `ExtraArguments` already supplies `--config`. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). Can only weaken the gate (e.g. `error` keeps warnings out entirely, and broken contracts still block since they are errors — there is no advisory mode for this auditor). |
| `IncludedRules` / `ExcludedRules` | — | Contract names (exact match) to keep/drop as findings — the tool's only rule identifier. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/`, `.venv/`, `venv/`, `.tox/` | Module-path prefixes dropped from findings — findings carry modules in slash form (`vendor/pkg`), so these match vendored/generated trees. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--contract <name>` to limit the check, `--cache-dir <path>` to opt back into caching, or `--verbose`. A repeated `--config`/`--no-cache`/`--no-logo` defers to the operator's own setting. |
| `TimeoutSeconds` | `300` | Per-run bound — graph building is the dominant cost on large trees. Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

**The contract set is repo-authored.** The audited repository writes
`[tool.importlinter]`/`[importlinter]` — which contracts exist, their
`ignore_imports`, and `root_packages` — so the subject can weaken its own
gate (the same posture every config-file-driven auditor takes: the project's
own declared contracts are the meaningful check, and config changes are
visible in the audited diff). Operators who need an operator-owned contract
set pin a file outside the repository via `ConfigPath`. The auditor runs
under `AuditCapabilities.None` (no agent credentials, no network).

## Default scope

`lint-imports` checks every module in the configured `root_packages` — the
repository's own declaration of what its architecture covers. The scan passes
`--no-cache` so it never writes `.import_linter_cache` into the audited tree,
and `TERM=dumb` in the tool environment keeps a baseline `FORCE_COLOR` (or a
pty-allocating provider) from injecting Rich ANSI escapes and live-progress
control codes into stdout. On top of that, the finding-level `ExcludePaths`
backstop drops violations under vendored and generated module prefixes —
noise that would train operators to ignore the auditor. Narrow the check with
`--contract` in `ExtraArguments`, or widen it by extending `ExcludePaths`
overrides.

### Known edge cases

- `TERM=dumb` fixes the rendered width at 80 columns; a violation line longer
  than that wraps mid-line in Rich's renderer and may not parse as a single
  import link. The broken contract still produces a fallback finding (without
  a precise location), so no verdict is ever silently lost.
- Contracts without an `id` cannot be limited via `--contract`; findings
  still carry the contract `name` as the rule id.
