# CodeyBox: Biome JavaScript/TypeScript Linter

Auditor plugin wrapping [Biome](https://biomejs.dev): it analyses the audited
repository with `biome lint --reporter=json --max-diagnostics=none
--no-errors-on-unmatched .` and reports each diagnostic as an audit finding
with the Biome rule category (e.g. `lint/suspicious/noDoubleEquals`,
`parse`) and `file:line` location.

## What it reports

- One finding per Biome diagnostic. The title carries the rule category and
  the first line of the message (e.g. `lint/suspicious/noDoubleEquals: Using
  == may be unsafe …`); the description carries the tool, rule,
  tool-reported level, location, and the full message. `Location` is
  `path:startLine`; Biome emits repo-relative paths in the JSON report, so no
  relativization is needed.
- **Syntax errors** (`category: parse`, e.g. a malformed `.ts` file) are
  findings too — level `error`, so they fail the audit rather than vanishing
  as tool noise.
- **Gate behaviour: hybrid / severity-driven — not blocking on every
  finding.** Biome's severities go through a declared map, never raw:
  `error` (and `fatal`) → `Error` (fail the audit); `warning` → `Warning`
  (advisory); `info` → `Info`. Whether a rule is error or warning comes from
  the ruleset in force — Biome's recommended rules by default, or an
  operator-pinned config via `ConfigPath`. `MinimumSeverity` can only drop
  findings, so the intended way to harden the gate is raising rule severities
  in the ruleset.

## What it cannot see

- **Files Biome does not lint.** Only files Biome's configuration and
  built-in language support cover are analysed — JavaScript, TypeScript, JSX,
  JSON, CSS, and GraphQL. Other languages produce no findings. A repository
  with no lintable files is a clean pass (`--no-errors-on-unmatched`), not
  an error.
- **Files Biome ignores.** `biome.json` `files.includes`/`files.ignore`,
  VCS ignore integration, and Biome's built-ins decide the analysed set.
- **Findings under excluded prefixes.** `ExcludePaths` is a finding filter —
  Biome still lints those files, but findings under `vendor/`,
  `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/` are
  dropped. Override `ExcludePaths` to re-include them.
- **Suppressed violations.** Biome honors `biome-ignore` suppression comments
  authored inside the audited repository, and — unlike ESLint — offers no
  flag to make those comments inert, so the auditor cannot switch that off:
  a violation the diff suppresses inline stays suppressed in the audit. That
  is a documented limitation, not an oversight: the base has no mechanism for
  it and the plugin does not hand-roll one. If the audit passes a tree whose
  local `biome lint` run disagrees only where the diff adds suppression
  comments, inspect those comments. Pinning an operator-owned ruleset via
  `ConfigPath` still leaves inline suppressions honored.
- **Formatter and assist drift.** The scan is `biome lint`, not `biome check`
  or `biome ci`: formatting and assist actions never become findings. Gate
  formatting separately if you want it.
- **More than `MaxFindings` diagnostics.** The tool-level display cap is
  lifted (`--max-diagnostics=none`) so Biome emits everything; findings
  beyond `MaxFindings` (default 1000) are dropped and the truncation is
  reported in the raw output.

## Exit codes and failure classification

Biome's convention (verified against v2.5.14 — **not** assumed from the
common "0 clean / 1 findings / 2 error" table):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Linted clean, or warnings/infos only | Verdict (pass, or advisory findings) |
| `1` with JSON `{"diagnostics": […]}` on stdout | Linted, error-level diagnostics found | Verdict (`Passed = false` when any finding maps to `Error`) |
| `1` with no JSON on stdout | Usage failure (`--bogus-flag is not expected in this context`) — text, not a report | Infrastructure — the JSON parser fails closed |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

Note there is deliberately no "could not run" row for missing files: with
`--no-errors-on-unmatched`, "no files were processed" exits 0 with an empty
`diagnostics` array — a clean pass. Without that flag it would exit 1 with
an empty report, which the parser would accept as a pass anyway; the flag
keeps the tool's own convention honest.

A missing `biome` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **Biome `2.5.14`** (`ExpectedVersion` in scoped
config). A linter's rules change between releases — and Biome marks even its
`json` reporter experimental ("may change in patch releases"), so the report
shape itself can move — which means an unpinned tool would change findings
under you: the auditor probes `biome --version` before every run and reports
an infrastructure failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`: Biome
ships via npm (`@biomejs/biome`) and as a standalone binary, and no distro
package carries a version pin. Provision the pinned release in your sandbox
baseline **only when this plugin is enabled**:

```sh
# baseline bake step (npm distribution; standalone needs no Node.js)
npm install -g @biomejs/biome@2.5.14
biome --version   # must print 2.5.14
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates, and baseline provisioning installs `biome` only in that state:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.biome"],
      "Enabled": ["codeybox.biome"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.biome" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.biome`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `2.5.14` | Pinned Biome release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | `null` | Path passed to `--config-path` — an operator-pinned `biome.json` outside the repository, or a repo file overriding Biome's default lookup. Ignored when `ExtraArguments` already supplies `--config-path`. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact Biome categories to keep/drop (e.g. `lint/suspicious/noDoubleEquals`, `parse`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--only=<rule>`, `--skip=<group>`, or `--diagnostic-level=<level>`. A repeated `--reporter` replaces the JSON report the parser expects and breaks the run into infrastructure failure; a repeated `--max-diagnostics` replaces the built-in `none`. |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Default scope

`biome lint .` — analysis only, over the whole audited tree, with Biome's own
configuration deciding which files are linted. On top of that, findings under
vendored (`vendor/`, `third_party/`, `node_modules/`) and generated (`dist/`,
`build/`, `out/`, `coverage/`) prefixes are dropped by default: violations
there belong to upstream packages or build output, not the change under
audit — reporting them produces noise that trains operators to ignore the
auditor. Re-include a prefix by overriding `ExcludePaths`.
