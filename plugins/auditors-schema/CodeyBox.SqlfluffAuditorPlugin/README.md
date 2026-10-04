# CodeyBox: Sqlfluff SQL Auditor

Auditor plugin wrapping [sqlfluff](https://github.com/sqlfluff/sqlfluff): it lints
every SQL file under the configured paths (the whole work tree by default)
against sqlfluff's dialect-aware rules — layout, capitalisation, aliasing,
references, structure, and parsing — and reports each violation as an audit
finding.

## What it reports

- One finding per sqlfluff violation. The title carries the sqlfluff rule
  code (e.g. `LT01`, `CP01`, `PRS`) so `IncludedRules`/`ExcludedRules` select
  rules directly; the description carries the tool, rule, tool-reported
  severity, location, problem message, and the dotted rule name (e.g.
  `layout.spacing`) for rule-documentation lookup.
- `Location` is the reported file path plus the 1-based line — sqlfluff
  already reports 1-based lines, carried verbatim. A violation without a line
  is reported file-scoped.
- **Gate behaviour: hybrid / severity-driven — stated explicitly.**
  sqlfluff marks each violation `warning: false` (a rule violation that fails
  the run) or `warning: true` (a rule the repository configuration downgraded
  via its `warnings` list). The declared mapping sends the former to
  `AuditSeverity.Error` (**blocking**) and the latter to
  `AuditSeverity.Warning` (**advisory**). Unparseable SQL (`PRS`) is a
  non-warning violation and blocks. There is no advisory-only switch; narrow
  scope with `ExcludedRules`, `ExcludePaths`, `Paths`, or `MinimumSeverity`
  instead.

## What it cannot see

- **Whether the SQL is correct.** sqlfluff is a static linter, not a query
  planner: it cannot see the catalog (row counts, indexes, constraints),
  data distributions, or execution plans. A lint-clean migration can still
  take a blocking lock or rewrite a table.
- **Non-`.sql` payloads.** Migrations written as Python/Go/etc. migration
  DSLs, or SQL embedded in other languages, are not linted. Only files
  sqlfluff itself selects under the configured paths are checked.
- **`-- noqa` inline comments by default.** The auditor passes
  `--disable-noqa`, so inline suppressions are inert and findings surface
  for SQL the comments would have suppressed. Setting
  `TrustRepositorySuppression` to `true` honors them again — a residual
  repo-controlled suppression surface; reviewers should grep for `noqa` in
  audited SQL when it is enabled.
- **Repository ruleset choices.** The repository's `.sqlfluff`, `setup.cfg`,
  `tox.ini`, or `pyproject.toml` rule selection (`rules`, `exclude_rules`),
  severity downgrades (`warnings`), and `.sqlfluffignore` path excludes are
  honored — the audited tree influences what is checked, as with the Ruff
  auditor's `ruff.toml`. Pin an operator-owned file via `ConfigPath` and
  select rules via `IncludedRules`/`ExcludedRules` to bound this surface.
- **The wrong dialect still parses.** The default `Dialect` (`ansi`) parses
  most SQL, but dialect-specific syntax (procedural extensions, warehouse
  types) linted as `ansi` surfaces as `PRS` parse findings, not as the rules
  that would apply under the real dialect. Set `Dialect` to the warehouse in
  use.

## Exit codes and failure classification

sqlfluff's exit conventions (verified against 3.4.2 — not assumed):

| Exit | stdout | Meaning | Classification |
|---|---|---|---|
| `0` | JSON array (entries with empty `violations`, or `[]`) | ran clean | pass |
| `0` | JSON array with `warning: true` violations | ran; repository downgraded every violated rule via `warnings` | verdict (advisory findings, still passes) |
| `1` | JSON array with violations | ran, violations found | findings (`Passed = false`) |
| `0`/`1` | none / not a JSON file-results array | could not produce a report (crash, truncated stream) | infrastructure |
| `2` | — | could not run: bad flags, unknown dialect, no dialect configured, unreadable `--config`, nonexistent paths | infrastructure (`AuditUnavailableException`) |
| `126` / `127` | — | cannot execute / not found | infrastructure (`AuditUnavailableException`) |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

A missing `sqlfluff` binary, a version mismatch, a timeout, and unparseable
output are likewise infrastructure failures naming the tool — never a
passing audit.

## Version pinning

The auditor is pinned to **sqlfluff `3.4.2`** (`ExpectedVersion` in scoped
config): rule implementations, message text, and report shape change between
releases, so an unpinned binary would change findings under you.
`sqlfluff --version` is probed before every run; a missing binary, an
unrecognised version string, or a different release is an infrastructure
failure.

The tool requirement is declared **verify-only** — no `AptPackage`: sqlfluff
is PyPI-distributed and no distro package carries a version pin. Provision
the pinned release into the sandbox baseline via
`CodeyBox:MultipassExtraRuncmd` / `CodeyBox:Incus:ExtraRuncmd` or
`ExecutableProvisions`, e.g.:

```sh
# baseline bake step (keep the version pinned)
pip install "sqlfluff==3.4.2"
sqlfluff --version   # must print the pinned version
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.sqlfluff"],
      "Enabled": ["codeybox.sqlfluff"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.sqlfluff" }
```

Only then does the declared `sqlfluff` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.sqlfluff`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `3.4.2` | Pinned sqlfluff release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `Dialect` | `ansi` | `--dialect` value (e.g. `ansi`, `postgres`, `bigquery`, `snowflake`). Overrides any repository `.sqlfluff` dialect by design. Blank defers to the repository file — which fails closed (exit 2, infrastructure) when the repository configures no dialect either. |
| `Paths` | `.` | Comma-separated positional paths sqlfluff lints (files or directories; sqlfluff selects the SQL itself). Entries starting with `-`, including the bare `-` stdin sentinel, are rejected deterministically — click would read them as flags or audit input from stdin. |
| `ConfigPath` | — | Additional `--config` file layered over the repository files — operator-chosen, may point at a baseline-provisioned file. |
| `TrustRepositorySuppression` | `false` | When `true`, `--disable-noqa` is omitted: inline `noqa` comments in the audited SQL suppress findings again. Off by default because the audited repo could silence the audit. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. `warning` keeps only downgraded warnings and above; `error` keeps only blocking violations. |
| `IncludedRules` / `ExcludedRules` | — | Exact sqlfluff rule codes to keep/drop (`LT01`, `PRS`, …). Post-scan finding filter — sqlfluff still lints them. For scan-level selection pass `--rules` / `--exclude-rules` via `ExtraArguments`. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `.git/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Post-scan filter. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). May carry sqlfluff flags (`--rules`, `--exclude-rules`, `--ignore`, `--warn-unused-ignores`, `--processes`) or extra paths. An operator-supplied `--format` / `--dialect` / `--disable-noqa` / `--disable-progress-bar` / `--config` here outranks the built-in argument. |
| `TimeoutSeconds` | `300` | Per-run bound; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

**Repository config files sqlfluff expects:** `.sqlfluff` (also `setup.cfg`,
`tox.ini`, `pyproject.toml [tool.sqlfluff]`) for dialect, rules, and the
`warnings` downgrade list; `.sqlfluffignore` for path exclusions. Both are
honored by default — see *What it cannot see*. The auditor always passes
`--format json` (the report the parser reads) and `--disable-progress-bar`
(deterministic streams); do not override `--format` via `ExtraArguments`
unless you intend the run to fail closed as infrastructure.

## Default scope

`sqlfluff lint --dialect ansi --format json --disable-progress-bar
--disable-noqa .` over the whole work tree: sqlfluff selects the SQL files
itself, so non-SQL content is never read, and a tree with no SQL reports
`[]` and passes. On top of that, findings under `vendor/`,
`third_party/`, `node_modules/`, and `.git/` are dropped by default —
lint failures inside vendored dependencies or git internals describe content
that is not the change under audit, and reporting them would train operators
to ignore the auditor. `ExcludePaths` is a finding-level filter; an operator
who also wants the scan itself narrowed sets `Paths` or adds `--ignore` /
path arguments via `ExtraArguments`.
