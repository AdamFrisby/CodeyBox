# CodeyBox: Oxlint JavaScript/TypeScript Linter

Auditor plugin wrapping [oxlint](https://oxc.rs/docs/guide/usage/linter/):
it analyses the audited repository with `oxlint --format json
--no-error-on-unmatched-pattern .` and reports each diagnostic as an audit
finding with the oxlint rule code (e.g. `eslint(no-debugger)`,
`typescript(no-explicit-any)`) and `file:line` location.

## What it reports

- One finding per oxlint diagnostic. The title carries the rule code and
  the first line of the message (e.g. `` eslint(no-debugger): `debugger`
  statement is not allowed ``); the description carries the tool, rule,
  tool-reported level, location, and the full message. `Location` is
  `filename:spanLine`; oxlint emits worktree-relative paths in the JSON
  report, so no relativization is needed.
- **Syntax errors** (e.g. a malformed `.ts` file) are findings too — level
  `error`, so they fail the audit rather than vanishing as tool noise.
  Parse errors carry no rule code; the finding is reported with no rule id
  rather than an invented one.
- **Gate behaviour: hybrid / severity-driven — not blocking on every
  finding.** oxlint's severities go through a declared map, never raw:
  `error` (and `fatal`) → `Error` (fail the audit); `warning` → `Warning`
  (advisory); `advice` → `Info`. Whether a rule is error or warning comes
  from the ruleset in force — the default `correctness` category plus the
  repository's `.oxlintrc.json` by default, or an operator-pinned config via
  `ConfigPath`. Deny a rule on the command line (`-D <rule>` via
  `ExtraArguments`) or in the config to make it fail the audit.
  `MinimumSeverity` can only drop findings, so the intended way to harden
  the gate is raising rule severities in the ruleset.

## What it cannot see

- **Files oxlint does not lint.** Only files oxlint's configuration and
  built-in language support cover are analysed — JavaScript and TypeScript
  (including JSX/TSX and, with plugins, Vue/Svelte/Astro). Other languages
  produce no findings. A repository with no lintable files is a clean pass
  (`--no-error-on-unmatched-pattern`), not an error.
- **Files oxlint ignores.** `.oxlintrc.json` ignore configuration,
  `.eslintignore` / `--ignore-path` / `--ignore-pattern` entries, and
  oxlint's built-ins decide the analysed set. All of these are
  repository-authored unless the operator pins them outside the repository.
- **Findings under excluded prefixes.** `ExcludePaths` is a finding filter —
  oxlint still lints those files, but findings under `vendor/`,
  `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/` are
  dropped. Override `ExcludePaths` to re-include them.
- **Suppressed violations.** oxlint honors inline suppression comments
  (`// oxlint-disable-line`, `/* oxlint-disable */`, and the
  ESLint-compatible `eslint-disable` comments) authored inside the audited
  repository, and offers no flag to make those comments inert, so the
  auditor cannot switch that off: a violation the diff suppresses inline
  stays suppressed in the audit. That is a documented limitation, not an
  oversight: the base has no mechanism for it and the plugin does not
  hand-roll one. If the audit passes a tree whose local `oxlint` run
  disagrees only where the diff adds suppression comments, inspect those
  comments. Pinning an operator-owned ruleset via `ConfigPath` still leaves
  inline suppressions honored.
- **More than `MaxFindings` diagnostics.** Findings beyond `MaxFindings`
  (default 1000) are dropped and the truncation is reported in the raw
  output.

## Exit codes and failure classification

oxlint's convention (verified against v1.85.0 — **not** assumed from the
common "0 clean / 1 findings / 2 error" table):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Linted clean, or warnings only | Verdict (pass, or advisory findings) |
| `1` with JSON `{"diagnostics": […]}` on stdout | Linted, error-level diagnostics found | Verdict (`Passed = false` when any finding maps to `Error`) |
| `1` with no JSON on stdout | Usage failure (`Error: `--bogus-flag` is not expected in this context`) — text, not a report | Infrastructure — the JSON parser fails closed |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

Note there is deliberately no "could not run" row for missing files: with
`--no-error-on-unmatched-pattern`, "No files found to lint" exits 0 with an
empty `diagnostics` array — a clean pass. Without that flag it would exit 1
with a text prefix before the JSON, which the parser rejects as
infrastructure; the flag keeps the tool's own convention honest.

A missing `oxlint` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **oxlint `1.85.0`** (`ExpectedVersion` in scoped
config). A linter's rules change between releases, so an unpinned tool would
change findings under you: the auditor probes `oxlint --version` before
every run and reports an infrastructure failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`: oxlint
ships via npm (`oxlint`) and as a standalone binary, and no distro package
carries a version pin. Provision the pinned release in your sandbox baseline
**only when this plugin is enabled**:

```sh
# baseline bake step (npm distribution; standalone needs no Node.js)
npm install -g oxlint@1.85.0
oxlint --version   # must print 1.85.0
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates, and baseline provisioning installs `oxlint` only in that state:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.oxlint"],
      "Enabled": ["codeybox.oxlint"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.oxlint" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.oxlint`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `1.85.0` | Pinned oxlint release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | `null` | Path passed to `--config` — an operator-pinned `.oxlintrc.json` outside the repository, or a repo file overriding oxlint's default lookup. Ignored when `ExtraArguments` already supplies `--config`/`-c`. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact oxlint codes to keep/drop (e.g. `eslint(no-debugger)`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `-D <rule>`/`-A <rule>`/`-W <rule>` severity overrides or `--deny-warnings`. A repeated `--format` replaces the JSON report the parser expects and breaks the run into infrastructure failure. |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Default scope

`oxlint .` — analysis over the whole audited tree, with oxlint's own
configuration deciding which files are linted. On top of that, findings
under vendored (`vendor/`, `third_party/`, `node_modules/`) and generated
(`dist/`, `build/`, `out/`, `coverage/`) prefixes are dropped by default:
violations there belong to upstream packages or build output, not the change
under audit — reporting them produces noise that trains operators to ignore
the auditor. Re-include a prefix by overriding `ExcludePaths`.
