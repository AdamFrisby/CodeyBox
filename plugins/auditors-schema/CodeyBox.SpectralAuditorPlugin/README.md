# CodeyBox: Spectral Schema Auditor

Auditor plugin wrapping [Spectral](https://github.com/stoplightio/spectral) (`@stoplight/spectral-cli`):
it lints OpenAPI (v2, v3.0, v3.1) and AsyncAPI (v2.x, v3.x) specifications in JSON and YAML files
and reports schema, style, and semantic rule violations as CodeyBox audit findings.

## What it reports

- One finding per Spectral rule violation. The title carries the rule identifier
  (e.g. `oas3-schema`, `asyncapi-schema`, `info-contact`); the description carries the
  tool, rule, tool-reported severity, location, and problem message. `Location` is `path:startLine`.
- **Gate behaviour: hybrid / severity-driven.** Spectral emits findings with severities:
  - `error`: syntax errors, invalid JSON/YAML structure, and schema violations (e.g. missing required properties).
    These map to `AuditSeverity.Error` and **block the audit** (`Passed = false`).
  - `warn` / `warning`: convention and best-practice rules (e.g. missing operation tags or descriptions).
    These map to `AuditSeverity.Warning` and are **advisory** (do not block the audit).
  - `info` / `hint` / `note`: informational suggestions and upgrade notes.
    These map to `AuditSeverity.Info` and are non-blocking.
  Operators can change rule severities in their repository ruleset or set `MinimumSeverity` in scoped configuration.

## What it cannot see

- **Non-specification files.** Files that are not JSON or YAML are outside Spectral's domain.
- **Unrelated JSON/YAML documents.** With `--ignore-unknown-format` passed by default, non-specification documents
  (such as `package.json` or `.github/workflows/*.yml`) are skipped without emitting false-positive warnings.
- **Excluded paths.** Files under paths configured in `ExcludePaths` (defaults to `vendor/`, `third_party/`,
  `node_modules/`) are filtered out of findings.
- **Remote `$ref` schemas when offline.** Spectral requires network access to resolve remote URLs;
  in standard offline audit sandboxes, unresolved remote references may trigger resolution failures.
- **Repositories without ruleset configuration.** Spectral requires a ruleset file (e.g. `.spectral.yaml`,
  `.spectral.yml`, `.spectral.json`, `.spectral.js`) in the repository or an operator-configured `RulesetPath` /
  `--ruleset` flag. If no ruleset is found, Spectral exits with code 2 and the auditor fails closed as infrastructure.

## Exit codes and failure classification

Spectral's exit conventions:

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Ran clean (or warnings/hints only with default error fail-severity) | Verdict (Pass, or advisory findings if warnings/notes present) |
| `1` | Ran and found rule violations at or above fail-severity (errors) | Verdict (`Passed = false`, findings reported) |
| `2` | Technical failure (missing ruleset, invalid ruleset syntax, missing document file) | Infrastructure (`AuditUnavailableException`) |
| `1` (no SARIF) | Usage or execution failure (invalid CLI flags, CLI crash) | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure (`AuditUnavailableException`) |
| anything else | Unknown convention or signal termination | Infrastructure (`AuditUnavailableException`) |

## Version pinning

The auditor is pinned to **Spectral `6.16.3`** (`DefaultExpectedVersion`).
A scanner's rule definitions and schema validations evolve between releases, so an unpinned tool
would change findings across runs. The auditor probes `spectral --version` before scanning;
any version mismatch or missing binary fails closed as infrastructure.
Operators running a different pinned build configure `ExpectedVersion` in scoped configuration.

The tool requirement is declared **verify-only** (no `AptPackage`): Spectral is distributed via npm
(`@stoplight/spectral-cli`) or standalone binary releases, neither of which has an official distribution apt
package that supports version pinning on Ubuntu. Provision the pinned release in your sandbox baseline:

```sh
# Baseline provisioning bake step
npm install -g @stoplight/spectral-cli@6.16.3
spectral --version   # must print 6.16.3
```

## Enabling

The plugin is **disabled by default**. It loads only when allowlisted and enabled:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.spectral"],
      "Enabled": ["codeybox.spectral"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.spectral" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.spectral`, hot-reloadable per run:

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `6.16.3` | Pinned Spectral release. A different installed version fails closed as infrastructure. |
| `RulesetPath` | `null` | Path to ruleset file passed to `--ruleset`. If omitted, Spectral looks for `.spectral.yaml` (or `.spectral.yml`, `.json`, `.js`) in the repository root. |
| `TargetPatterns` | `**/*.{json,yml,yaml}` | Comma-separated list of document globs or file paths to lint. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact Spectral rule identifiers to include or drop (e.g. `oas3-schema`, `info-contact`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repository-relative paths dropped from findings. Setting replaces the default list. |
| `ExtraArguments` | — | Extra CLI arguments appended to argv (e.g. `--show-documentation-url`). |
| `TimeoutSeconds` | `300` | Per-run execution bound. Exceeding it is an infrastructure failure. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/findings bounds; excess is reported as truncated. |

## Default scope

By default, the auditor targets `**/*.{json,yml,yaml}` with `--ignore-unknown-format` enabled,
and filters out findings in `vendor/`, `third_party/`, and `node_modules/`.
This ensures that vendored schemas and unrelated JSON/YAML files (like package manifests and workflow definitions)
do not produce false-positive noise that trains operators to ignore auditor output.
