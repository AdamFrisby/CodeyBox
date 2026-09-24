# CodeyBox: ESLint JavaScript/TypeScript Auditor

Auditor plugin wrapping [ESLint](https://eslint.org): it lints the audited
repository with `eslint --format json-with-metadata .` and reports each rule violation and
parse error as an audit finding with the ESLint rule id and `file:line`
location. JavaScript and TypeScript analysis only — whatever the repository's
flat config covers through its `files` entries.

## What it reports

- One finding per ESLint message. The title carries the rule id
  (e.g. `no-unused-vars`, `eqeqeq`, `@typescript-eslint/no-explicit-any`) and
  the first line of the message; the description carries the tool, rule,
  tool-reported level, location, and the full message. `Location` is
  `path:startLine`; ESLint's absolute `filePath` values are relativized
  against the report's own `metadata.cwd`.
- **Fatal parse errors** (`fatal: true`, e.g. a `.ts` file with no TypeScript
  parser configured) are findings too — rule id `(none)`, level `fatal`.
- **Gate behaviour: hybrid / severity-driven — not blocking on every
  finding.** ESLint's numeric severities go through a declared map, never
  raw: `"2"` (error) and `"fatal"` → `Error` (fail the audit); `"1"` (warn)
  → `Warning` (advisory); anything unrecognised → `Warning`. Whether a rule
  is error or warn comes from the ruleset in force — the repo's
  `eslint.config.*` by default. `MinimumSeverity` can only drop findings, so
  the intended way to harden the gate is `rules: { "<rule>": "error" }` in
  the ruleset.

## What it cannot see

- **Languages outside the config's `files` globs.** The scan is `eslint .`;
  files the flat config does not cover are simply not linted. A repo whose
  config only matches `**/*.js` produces no TypeScript findings.
- **TypeScript semantics without a TypeScript parser.** Core ESLint parses
  with espree — a `.ts` file covered by `files` but lacking
  `@typescript-eslint/parser` (or a compatible parser) surfaces as a `fatal`
  parse-error finding, not type analysis. Wire `typescript-eslint` in the
  repo's config for real TS rules.
- **Files ESLint ignores.** Config `ignores`, `globalIgnores`, and ESLint's
  built-ins (`node_modules`, dotfiles) are skipped by the tool itself.
- **Findings under excluded prefixes.** `ExcludePaths` is a finding filter —
  ESLint still lints those files, but findings under `vendor/`,
  `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/` are
  dropped. Override `ExcludePaths` to re-include them.
- **Anything without a config.** ESLint requires `eslint.config.js` (or
  `.mjs`/`.cjs`/`.ts`) in the repository — or an operator-supplied
  `ConfigPath`. Without one ESLint exits 2 and the run is infrastructure,
  not a pass.
- **Configs whose imports need `node_modules`.** A flat config that imports
  plugins, parsers, or shared configs (`typescript-eslint`,
  `eslint-plugin-*`, `@eslint/js`, …) resolves them from the audited
  repository's `node_modules`. If dependencies are not installed in the
  audit sandbox the config fails to load — an exit-2 infrastructure failure,
  not a pass. Provision `npm ci`/`npm install` into the sandbox baseline for
  repos whose config needs it, or pin a self-contained `ConfigPath`.

## Exit codes and failure classification

ESLint's convention (verified against v10.x):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Linted clean, or warnings only | Verdict (pass, or advisory findings) |
| `1` | Linted, error-level violations found (or `--max-warnings` breached) | Verdict (`Passed = false` when any finding maps to `Error`) |
| `2` | Could not run: configuration problem (incl. **no `eslint.config.*` in the repo**) or internal error | Infrastructure (`AuditUnavailableException`) |
| `1`/`2` with no JSON on stdout | Usage/execution failure (bad flags, crash) | Infrastructure — the JSON parser fails closed |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `eslint` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **ESLint `10.10.0`** (`ExpectedVersion` in scoped
config). A linter's rules change between releases, so an unpinned tool would
change findings under you: the auditor probes `eslint --version` before every
run and reports an infrastructure failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`: ESLint is
an npm package and no distro package carries a version pin. Provision the
pinned release in your sandbox baseline:

```sh
# baseline bake step
npm install -g eslint@10.10.0
eslint --version   # must print v10.10.0
```

Because ESLint runs on Node.js, the baseline also needs Node.js
(`apt-get install -y nodejs npm`, or the agent-stack Node already on most
baselines).

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.eslint"],
      "Enabled": ["codeybox.eslint"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.eslint" }
```

The `eslint` tool requirement is only contributed to baseline provisioning
while the plugin is enabled.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.eslint`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `10.10.0` | Pinned ESLint release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | `null` | Path passed to `--config` — an operator-pinned ruleset outside the repository, or a repo file overriding the `eslint.config.*` lookup. Ignored when `ExtraArguments` already supplies `--config`/`-c`. Pair with `--no-config-lookup` in `ExtraArguments` to keep the repo's own config fully out of the run. |
| `TrustRepositorySuppression` | `false` | When `false` (default) the scan passes `--no-inline-config`, so `/* eslint-disable */`, `/* global */`, and per-line rule/severity comments authored in the audited tree are inert — the subject cannot silence findings line-by-line. When `true`, ESLint honors those comments. The config *file* is repo-authored either way (see below). |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact ESLint rule ids to keep/drop (e.g. `no-unused-vars`, `@typescript-eslint/no-explicit-any`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `dist/`, `build/`, `out/`, `coverage/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--max-warnings <n>`, `--report-unused-disable-directives`, or `--config`/`--no-config-lookup` to pin an operator ruleset. A repeated flag wins over the built-in default — take care: `--format` would replace the JSON report the parser expects and break the run into infrastructure failure. |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

**Repository-controlled suppression is off by default.** The audit subject
writes the repository, and ESLint lets source files reconfigure the linter
inline (`/* eslint-disable */` suppresses findings; `/* eslint rule: off */`
changes the ruleset mid-file; `/* global */`, `/* eslint-env */` alter the
analysis environment). The scan therefore passes `--no-inline-config` unless
`TrustRepositorySuppression: true` is set — findings then appear for code the
comments would have suppressed. Expect *more* findings than a local
`npm run lint` on repos that rely on inline disables; that is the gate
working as intended.

The larger suppression surface is `eslint.config.*` itself: it is
repo-authored **and executed** (flat configs are arbitrary JavaScript). The
auditor runs under `AuditCapabilities.None` — no agent credentials, no
network — so repo config executes unprivileged. Its `rules`/`ignores`/
`suppressionsLocation` are honored because the project's own lint contract is
the meaningful check, and changes to it are visible in the audited diff. For
a fully operator-owned gate, pin an out-of-repo config via `ConfigPath` (or
`--config` + `--no-config-lookup` in `ExtraArguments`).

## Default scope

`eslint .` — the repository's flat config decides what gets linted through
its `files`/`ignores` entries; that is the project's own declaration of
lintable scope and avoids the "file ignored" noise explicit globs would
produce for uncovered files. On top of that, findings under vendored
(`vendor/`, `third_party/`, `node_modules/`) and generated (`dist/`,
`build/`, `out/`, `coverage/`) prefixes are dropped by default: violations
there belong to upstream packages or build output, not the change under
audit — reporting them produces noise that trains operators to ignore the
auditor. ESLint's own ignores already skip `node_modules` and dotfiles at
scan time; the `ExcludePaths` defaults are a finding-level backstop for
configs that lint wider. Re-include a prefix by overriding `ExcludePaths`.
