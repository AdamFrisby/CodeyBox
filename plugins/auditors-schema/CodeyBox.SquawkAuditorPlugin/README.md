# CodeyBox: Squawk PostgreSQL Migration Auditor

Auditor plugin wrapping [squawk](https://github.com/sbdchd/squawk): it lints
every SQL file matching the configured glob patterns (`**/*.sql` by default)
against squawk's PostgreSQL migration-safety rules — blocking lock hazards,
table rewrites, non-reversible changes, missing timeout settings — and
reports each violation as an audit finding.

## What it reports

- One finding per squawk violation. The title carries the squawk rule id
  (e.g. `require-concurrent-index-creation`, `ban-drop-table`,
  `adding-field-with-default`) so `IncludedRules`/`ExcludedRules` select
  rules directly; the description carries the tool, rule, squawk's raw
  level, location, message, and squawk's `help` remediation text when
  present.
- `Location` is the reported file path plus the 1-based line — squawk emits
  a 0-based line index, converted for findings. A violation without a line
  is reported file-scoped.
- `syntax-error` violations (SQL squawk's parser rejected) carry level
  `Error`; all rule violations carry `Warning`.
- **Severity: blocking.** squawk's levels are not a severity grade — every
  violated safety rule is `Warning`, only unparseable SQL is `Error` — so
  the declared mapping sends both, and any unrecognised level, to
  `AuditSeverity.Error`. Every reported violation fails the audit. There is
  no advisory mode; narrow scope with `ExcludedRules`, `ExcludePaths`, or
  `Patterns` instead.

## What it cannot see

- **Anything outside the matched files.** squawk lints SQL text on disk; it
  does not see which statements a migration tool will actually run, in what
  order, or against which database size — a rule like
  `require-lock-timeout` cannot prove a deployment is safe.
- **The database itself.** No catalog knowledge: row counts, existing
  indexes, replication lag, or lock contention. squawk is a static linter,
  not a planner.
- **`squawk-ignore` / `squawk-ignore-file` comments.** squawk honors these
  unconditionally — it ships no flag to disable them — so the audited
  change can suppress a rule inline. This is a residual repo-controlled
  suppression surface the auditor cannot close; reviewers should grep for
  `squawk-ignore` in audited SQL. (The file-level `.squawk.toml` surface
  *is* closed — see Configuration.)
- **Non-`.sql` payloads.** Migrations written as Python/Go/etc. migration
  DSLs, or SQL embedded in other languages, are not linted.

## Exit codes and failure classification

squawk does **not** follow the common "0 = clean, 1 = findings, 2 = could
not run" convention — verified against the v2.64.0 source: a completed scan
with violations exits `1`, **and every run failure also exits `1`** — glob
resolution errors, unreadable files, and config parse errors all
`process::exit(1)` or propagate as `Err` from `main` (which Rust reports as
1). The discriminator is the report, not the exit code.

| Exit | stdout | Meaning | Classification |
|---|---|---|---|
| `0` | JSON array (`[]` when clean) | ran clean | pass |
| `1` | JSON array | ran, violations found | findings |
| `0`/`1` | none / not a JSON array | could not run (bad flags, unmatched glob, read failure, config parse error) | infrastructure |
| `2` | — | clap usage error | infrastructure |
| `126` / `127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

A missing `squawk` binary, a version mismatch, a timeout, and unparseable
output are likewise infrastructure failures naming the tool — never a
passing audit.

**The unmatched-pattern trap:** squawk exits 1 when its patterns match no
files, and with `--no-error-on-unmatched-pattern` it instead falls back to
linting **stdin** whenever no positional path resolves. On an empty file set
that could block a scan on a read. The auditor appends a `/dev/null`
sentinel to every pattern list — it always resolves to a readable empty
file, so a repository with no SQL produces a deterministic `[]` report and a
pass, and squawk's stdin branch is never reachable from this invocation.

## Version pinning

The auditor is pinned to **squawk `2.64.0`** (`ExpectedVersion` in scoped
config): the rule set, message text, and report shape change between
releases, so an unpinned binary would change findings under you.
`squawk --version` is probed before every run; a missing binary, an
unrecognised version string, or a different release is an infrastructure
failure.

The tool requirement is declared **verify-only** — no `AptPackage`: no
distro package carries squawk. Provision the pinned upstream release into
the sandbox baseline via `CodeyBox:MultipassExtraRuncmd` /
`CodeyBox:Incus:ExtraRuncmd` or `ExecutableProvisions`, e.g.:

```sh
# baseline bake step (adjust arch; verify against the release checksums)
SQUAWK_VERSION=2.64.0
curl -fsSL "https://github.com/sbdchd/squawk/releases/download/v${SQUAWK_VERSION}/squawk-linux-x64" -o /usr/local/bin/squawk
chmod +x /usr/local/bin/squawk
squawk --version   # must print the pinned version
```

(`npm install -g squawk-cli@<version>` or `pip install squawk-cli==<version>`
also work when the baseline carries a Node or Python toolchain — the binary
is the same; keep the version pinned.)

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.squawk"],
      "Enabled": ["codeybox.squawk"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.squawk" }
```

Only then does the declared `squawk` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.squawk`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `2.64.0` | Pinned squawk release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `Patterns` | `**/*.sql` | Comma-separated positional glob patterns squawk scans. Repo-relative patterns keep finding paths repo-relative. Patterns starting with `-`, or equal to a squawk subcommand name (`server`, `upload-to-github`, `help`), are rejected deterministically — clap would read them as flags/commands. |
| `PgVersion` | — | `--pg-version` value (e.g. `16.4`); squawk lints version-gated rules against it. |
| `AssumeInTransaction` | — | `true` → `--assume-in-transaction`, `false` → `--no-assume-in-transaction`; unset → squawk's default. Relevant for rules like `ban-concurrent-index-creation-in-transaction`. |
| `ConfigPath` | — | Explicit `--config` path — operator-chosen, may point at a repo `.squawk.toml` or a baseline-provisioned file. |
| `TrustRepositoryConfig` | `false` | When `true`, no `--config` is passed at all: squawk discovers `.squawk.toml` by traversing up from the working directory (repo root and sandbox ancestors), honoring its `excluded_rules`, `excluded_paths`, `included_rules`, `pg_version`, and `assume_in_transaction`. Off by default because the audited repo could silence the audit. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. Everything maps to `error`, so this only matters if the mapping changes. |
| `IncludedRules` / `ExcludedRules` | — | Exact squawk rule ids to keep/drop (`require-concurrent-index-creation`, `syntax-error`, …). Post-scan finding filter — squawk still lints them. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `.git/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Post-scan filter. Setting it replaces the default list. For scan-level exclusion use squawk's `--exclude-path` via `ExtraArguments`. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). May carry squawk flags (`--exclude`, `--include`, `--exclude-path`, `--verbose`) or extra patterns. An operator-supplied `-c`/`--config` here outranks the pinned `/dev/null` config. |
| `TimeoutSeconds` | `300` | Per-run bound; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

**`.squawk.toml` and the audited repo:** squawk discovers a `.squawk.toml`
by traversing up from the working directory and honors its
`excluded_rules`/`excluded_paths` — a repository under audit could use it
to silence the audit. The auditor therefore pins `--config /dev/null` by
default (an empty config that disables repo-root and ancestor discovery).
Set `ConfigPath` or `TrustRepositoryConfig` to opt repository configuration
back in. Inline `squawk-ignore` comments have no disable flag and remain
honored regardless — see *What it cannot see*.

## Default scope

`squawk --reporter json '**/*.sql' /dev/null` over the whole work tree:
squawk expands the glob itself and lints only `*.sql` files, so non-SQL
content is never read. On top of that, findings under `vendor/`,
`third_party/`, `node_modules/`, and `.git/` are dropped by default —
unsafe migrations inside vendored dependencies or git internals describe
content that is not the change under audit, and reporting them would train
operators to ignore the auditor. `ExcludePaths` is a finding-level filter;
an operator who also wants the scan itself narrowed sets `Patterns` or adds
`--exclude-path` via `ExtraArguments`.
