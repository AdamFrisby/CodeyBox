# CodeyBox: Markdownlint Markdown Linter

Auditor plugin wrapping [markdownlint-cli](https://github.com/igorshubovych/markdownlint-cli):
it lints the audited repository's Markdown files with `markdownlint --json
--dot --ignore-path /dev/null .` and reports each diagnostic as an audit
finding with the Markdownlint rule code (e.g. `MD009`, `MD013`, `MD041`)
and `file:line` location. Markdown diagnostics only.

## What it reports

- One finding per markdownlint-cli `--json` issue. The title carries the
  rule code and the first line of the message (e.g. "MD009: Trailing spaces
  [Expected: 0 or 2; Actual: 3]"); the description carries the tool, rule,
  tool-reported severity, location, and the full message including the
  tool's error detail and context. `Location` is `path:lineNumber`.
- **Gate behaviour: hybrid / severity-driven — not blocking on every
  finding.** Diagnostics the tool reports at severity `error` map to
  `Error` and fail the audit; diagnostics at severity `warning` map to
  `Warning` and are advisory. Whether a rule reports as error or warning is
  decided by the configuration in force (see below) — the declared map
  never passes raw severities through, and `MinimumSeverity` only drops
  findings, never raises them.

## What it cannot see

- **Non-Markdown files.** The scan lints the tool's Markdown extensions
  (`.md`, `.markdown`). Everything else produces no findings. A repository
  with no Markdown files is a clean pass (empty report), not an error.
- **Files excluded from the walk.** Each `ExcludePaths` entry is passed as
  `--ignore`, so vendored and generated trees are never linted; an operator
  `Inputs` value replaces the default `.` input outright; and the
  repository's own `.markdownlintignore` is inert by default (see below).
- **Inline disable comments (always honored).** `<!-- markdownlint-disable
  MD013 -->` and friends inside Markdown files suppress diagnostics, and
  markdownlint-cli offers no flag to turn that off. The audit subject
  writes those files, so a file can hide its own violations from this
  auditor — treat a clean result on Markdown-heavy diffs with the same
  scepticism you would give any self-reported check, and review added
  disable comments in the diff itself.
- **The repository config's blind spots.** Without an operator `ConfigPath`,
  the scan merges the repository's markdownlint config files
  (`.markdownlint.jsonc` / `.markdownlint.json` / `.markdownlint.yaml` /
  `.markdownlint.yml` / `.markdownlintrc`) over the built-in defaults, and
  the merged ruleset — including per-rule `severity` — decides what is
  reported. A clean audit against a permissive repo config says nothing
  about rules the repo never enabled. Operators who want a fixed bar pin an
  operator-owned config via `ConfigPath`.
- **Custom rules.** `--rules` modules are JavaScript loaded into the scan
  process. The auditor never passes `--rules` itself; an operator who adds
  it in `ExtraArguments` executes that code in the audit sandbox (which
  runs with no credentials and no network).
- **More than `MaxFindings` diagnostics.** Findings beyond `MaxFindings`
  (default 1000) are dropped and the truncation is reported in the raw
  output.

## Exit codes and failure classification

markdownlint-cli's convention (verified against 0.49.1 — **not** assumed
from the common "0 clean / 1 findings / 2 error" table):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Linted clean (no report emitted), or linted with warning-severity diagnostics only (JSON report emitted on stderr) | Verdict (pass when no findings; warnings-only findings are advisory and still pass) |
| `1` | Linted with error-severity diagnostics (JSON report on stderr) | Verdict (`Passed = false` — every `error` finding maps to `Error`) |
| `1` with no JSON on stderr | Ran but emitted no report | Infrastructure — the parser fails closed |
| `2` | Could not write the `--output` file | Infrastructure (`AuditUnavailableException`) |
| `3` | Could not load a `--rules` module | Infrastructure (`AuditUnavailableException`) |
| `4` | Unexpected problem: malformed config, bad flags | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `markdownlint` is always an infrastructure failure naming the
tool — never a passing audit.

Do not pass `-o`/`--output` or `-q`/`--quiet` in `ExtraArguments`: both move
or suppress the stderr report the verdict is parsed from, turning findings
into infrastructure failures (exit 1 with no report) or hiding warnings
(exit 0 with no report). Do not pass `-f`/`--fix`: it rewrites the audited
tree — the audit must never mutate its subject.

## Version pinning

The auditor is pinned to **markdownlint-cli `0.49.1`** (`ExpectedVersion`
in scoped config). A linter's rules change between releases, so an unpinned
tool would change findings under you: the auditor probes `markdownlint
--version` before every run and reports an infrastructure failure on any
other version.

The tool requirement is declared **verify-only** — no `AptPackage`: the
pinned release ships via npm, and no distro package carries a version pin.
Provision it in your sandbox baseline **only when this plugin is enabled**:

```sh
npm install -g markdownlint-cli@0.49.1
markdownlint --version   # must print 0.49.1
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates, and baseline provisioning verifies `markdownlint` only in that
state:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.markdownlint"],
      "Enabled": ["codeybox.markdownlint"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.markdownlint" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.markdownlint`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `0.49.1` | Pinned markdownlint-cli release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | `null` | Path passed to `--config` — an operator-pinned config file outside the repository. Merges over (and wins against) any repository config files. Ignored when `ExtraArguments` already supplies `--config`/`-c`. |
| `Inputs` | `null` (whole-tree `.` input) | Comma-separated files, globs, or directories replacing the default `.` input. Useful for scoping the scan to `docs` without touching `ExcludePaths`. |
| `TrustRepositorySuppression` | `false` | When `false` (default) the scan passes `--ignore-path /dev/null`, so the repository's `.markdownlintignore` is inert. When `true`, the pin is dropped and the repository's ignore file decides which Markdown files are linted. Inline `markdownlint-disable` comments are honored either way (see above). |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). Set to `error` for a blocking-errors-only gate. |
| `IncludedRules` / `ExcludedRules` | — | Exact markdownlint rule codes to keep/drop (e.g. `MD009`, `MD013`, `MD041`). |
| `ExcludePaths` | `.git/`, `vendor/`, `third_party/`, `node_modules/`, `.venv/`, `venv/`, `dist/`, `build/`, `out/`, `coverage/`, `bin/`, `obj/`, `target/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Each entry is also passed to the tool as `--ignore`, so excluded trees are never walked (the tool has no default exclusion list). Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--enable <rules>` / `--disable <rules>` (note the CLI's variadic form: end the rule list with `--` only when files follow it) or `--config`. Never use `-o`/`--output`, `-q`/`--quiet`, or `-f`/`--fix` (see above). A repeated `--config`/`-c` or `--ignore-path`/`-p` replaces the built-in flag the parser depends on with the same effect: the operator's value wins and the built-in is skipped. |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

**Repository-controlled suppression is off by default where the tool
allows it.** The audit subject writes the repository, and
`.markdownlintignore` would let it hide whole files from the scan. The
default `--ignore-path /dev/null` keeps that file inert — expect *more*
findings than a local `markdownlint` run on repos that rely on it; that is
the gate working as intended. The remaining repo-authored surfaces — the
config files (rule selection and severity) and inline disable comments —
cannot be neutralized through the CLI and are honored; changes to config
files are visible in the audited diff, and added disable comments deserve a
look during review.

## Default scope

`markdownlint --json --dot .` — the whole worktree, including
dot-directories such as `.github/` where documentation legitimately lives.
markdownlint-cli ships no default exclusion list, so the vendored
(`.git/`, `vendor/`, `third_party/`, `node_modules/`, `.venv/`, `venv/`)
and generated (`dist/`, `build/`, `out/`, `coverage/`, `bin/`, `obj/`,
`target/`) `ExcludePaths` defaults double as the walk boundary via
`--ignore`: violations there belong to upstream packages or build output,
not the change under audit — reporting them produces noise that trains
operators to ignore the auditor. Re-include a path by overriding
`ExcludePaths`, or narrow the scan with `Inputs`.
