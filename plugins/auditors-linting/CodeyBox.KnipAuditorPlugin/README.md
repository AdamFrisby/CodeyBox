# CodeyBox: Knip unused JS/TS files, exports and dependencies

Auditor plugin wrapping [knip](https://knip.dev): it analyses the audited
JavaScript/TypeScript repository with
`knip --reporter json --include files,exports,dependencies` and reports
each unused file, unused export, and unused dependency as an audit finding
with the knip issue-type id (`files`, `exports`, `dependencies`, …) and
`file:line` location when knip supplies one.

## What it reports

- One finding per knip issue-type item. The title carries the issue type
  (the rule id) and a short message (`Unused file`, `Unused export
  'factorial'`, `Unused dependency 'lodash'`); the description carries the
  tool, rule, tool-reported level, location, and the full message.
  `Location` is `path` for unused files (knip does not emit a line) and
  `path:line` for unused exports and `package.json` dependency entries.
- **Gate behaviour: blocking by default** for this auditor's default
  include set. knip classifies unused files, unused exports, and unused
  dependencies as `error`; those map to `AuditSeverity.Error` and fail the
  audit. Warning-class knip types such as `cycles` are not included unless
  an operator adds them — they map to `AuditSeverity.Warning` and stay
  advisory. `MinimumSeverity` only drops findings; it never raises them.

## What it cannot see

- **Issue types outside the default `--include`.** Unused exported *types*,
  unlisted dependencies, unresolved imports, unused binaries, duplicate
  exports, catalog issues, and circular dependencies are off unless added
  via `ExtraArguments` (`--include types`, `--include cycles`, …). knip's
  `--exports` shortcut is broader than this auditor's default.
- **Unused exports in entry files.** knip does not report unused exports
  from entry points (`package.json` `main`/`bin`/`exports`, configured
  `entry`) unless `includeEntryExports` is set in knip config or
  `--include-entry-exports` is passed.
- **Languages knip does not analyse.** This is a JavaScript/TypeScript
  project linter. A repository without a root `package.json` makes knip
  exit 2 — infrastructure, not a pass.
- **Files knip never walks.** knip's `project`/`entry` globs, built-in
  ignores (`node_modules`), and (by default) `.gitignore` decide the
  analysed set. Gitignored generated files are invisible.
- **Findings under excluded prefixes.** `ExcludePaths` is a finding
  filter — knip may still mention those paths, but findings under
  `vendor/`, `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`,
  `coverage/` are dropped. Override `ExcludePaths` to re-include them.
- **Suppression authored in the repository.** knip honours `knip.json` /
  `knip.jsonc` / `.knip.json` / `package.json#knip` (`ignore`,
  `ignoreDependencies`, `ignoreFiles`, `ignoreIssues`, `tags`, `rules`)
  and JSDoc tags the config opts out of. That is the project's own unused-
  code contract, same posture as ESLint's repo `eslint.config.*`. For an
  operator-owned gate, pin an out-of-repo config via `ConfigPath` (or
  `--config` in `ExtraArguments`).

## Exit codes and failure classification

knip's convention (verified against v6.38.0 source and CLI — **not**
assumed from the common "0 clean / 1 findings / 2 error" table alone):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Analysed clean, or only warning-class issue types | Verdict (pass, or advisory findings) |
| `1` with JSON `{ "issues": […] }` on stdout | Analysed, error-level issues found | Verdict (`Passed = false` when any finding maps to `Error`) |
| `1` with no JSON on stdout | Usage failure: unknown flag, `parseArgs` catch prints help and `process.exit(1)` | Infrastructure — the JSON parser fails closed |
| `2` | Could not run: missing `package.json`, unreadable/invalid knip config, plugin load error, internal error | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `knip` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **knip `6.38.0`** (`ExpectedVersion` in scoped
config). An unpinned scanner changes its findings under you: the auditor
probes `knip --version` before every run and reports an infrastructure
failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`: knip
is an npm package (`engines`: Node `^20.19.0 || >=22.12.0`) and no distro
package carries a version pin. Provision the pinned release in your
sandbox baseline **only when this plugin is enabled**:

```sh
# baseline bake step
npm install -g knip@6.38.0
knip --version   # must print 6.38.0
```

Because knip runs on Node.js, the baseline also needs Node.js 20.19+ or
22.12+ (`apt-get install -y nodejs npm`, or the agent-stack Node already
on most baselines).

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates, and baseline provisioning installs `knip` only in that state:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.knip"],
      "Enabled": ["codeybox.knip"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.knip" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.knip`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `6.38.0` | Pinned knip release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | `null` | Path passed to `--config` — an operator-pinned knip config outside the repository, or a repo file overriding knip's default lookup (`knip.json`, `knip.jsonc`, `.knip.json`, `.knip.jsonc`, `knip.ts` / `knip.js` / `knip.config.*`, `package.json#knip`). Ignored when `ExtraArguments` already supplies `--config`/`-c`. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact knip issue-type ids to keep/drop (`files`, `exports`, `dependencies`, `devDependencies`, …). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--include types`, `--include-entry-exports`, `--production`, `--strict`, or `--workspace`. A repeated `--reporter` would replace the JSON report the parser expects and break the run into infrastructure failure. |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Default scope

`knip --include files,exports,dependencies` — unused JS/TS files, unused
exported values, and unused package dependencies. That matches what this
auditor is for and keeps unlisted binaries, unresolved specifiers, and
catalog noise out of the default gate; operators expand the include set
when they want those types.

knip itself already skips `node_modules` and (by default) gitignored
paths at analysis time. On top of that, findings under vendored
(`vendor/`, `third_party/`, `node_modules/`) and generated (`dist/`,
`build/`, `out/`, `coverage/`) prefixes are dropped by default: unused
symbols there belong to upstream packages or build output, not the change
under audit — reporting them produces noise that trains operators to
ignore the auditor. Re-include a prefix by overriding `ExcludePaths`.
