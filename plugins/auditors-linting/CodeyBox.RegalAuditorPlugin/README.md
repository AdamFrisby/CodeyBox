# CodeyBox: Regal Rego Auditor

Auditor plugin wrapping [Regal](https://www.openpolicyagent.org/projects/regal)
(`regal lint --format sarif`): it lints Rego policy source (`*.rego` files) and
reports each rule violation as a CodeyBox audit finding with its rule identifier
and file/line location preserved.

Auditing the Rego policy source is distinct from using that policy to inspect other
configuration (the Conftest auditor's job): this auditor reports problems *in* the
policy code itself — style, idioms, and bugs — not problems the policy finds elsewhere.

## What it reports

- One finding per Regal rule violation. The title carries the rule identifier
  (e.g. `use-assignment-operator`, `prefer-snake-case`); the description carries the
  tool, rule, tool-reported severity, location, and problem message. `Location` is
  `path:startLine`.
- **Gate behaviour: hybrid / severity-driven.** Regal classifies each violation as
  `error` or `warning` (a rule set to `ignore` is not reported at all):
  - `error`: rule violations configured at error severity. These map to `AuditSeverity.Error`
    and **block the audit** (`Passed = false`).
  - `warning`: rule violations configured at warning severity. These map to
    `AuditSeverity.Warning` and are **advisory** (do not block the audit).
  - `note`: informational results. These map to `AuditSeverity.Info` and are non-blocking.
  Operators can change rule severities in Regal configuration or set `MinimumSeverity`
  in scoped configuration.

## What it cannot see

- **Non-Rego files.** Only `*.rego` policy source is linted; every other file kind is
  out of scope by construction.
- **`opa check --strict` coverage.** Regal does not run OPA's strict-mode checks
  (unused imports/assignments and the remaining `--strict` checks). Run
  `opa check --strict` separately if you need that coverage — this auditor makes no
  claim about it.
- **Whether the policies are correct.** Regal lints policy source; it does not evaluate
  policies against data the way Conftest does. A policy can lint clean and still deny
  (or allow) the wrong things.
- **Suppressed rules.** The audited repository's `.regal/config.yaml` (or `.regal.yaml`)
  decides rule severities and options — that is the scanner's own configuration surface.
  Operators who want a fixed rule set pin it with `ConfigPath` (an operator-owned file
  outside the audited tree).
- **Findings under an `ExcludePaths` prefix** are dropped from the report
  (Regal still scans them — the filter is post-scan). Re-include by overriding
  `ExcludePaths`.

## Exit codes and failure classification

Regal's exit conventions (per the official Regal CLI documentation — not a generic
0/1 assumption):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Ran clean, or warnings only (default `--fail-level error`) | Verdict (Pass, or advisory findings if warnings present) |
| `2` | Ran and found warnings (`--fail-level warning` only) | Verdict (advisory findings) |
| `3` | Ran and found errors | Verdict (`Passed = false`, findings reported) |
| `2`/`3` (no SARIF) | Usage or execution failure with no report | Infrastructure (`AuditUnavailableException`) |
| anything else (e.g. `1`) | Unknown convention — usage errors, missing inputs | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure (`AuditUnavailableException`) |

`--fail-level` only moves warnings between exits `0` and `2` — both are declared
findings-producing, so either setting classifies correctly. A missing `regal` binary,
a version mismatch, a timeout, and unparseable output are likewise infrastructure
failures naming the tool — never a passing audit.

## Version pinning

The auditor is pinned to **Regal `0.40.0`** (`DefaultExpectedVersion`).
A linter's rule set changes between releases, so an unpinned tool would change findings
across runs. The auditor probes `regal version` before scanning; any version mismatch
or missing binary fails closed as infrastructure. Operators running a different pinned
build configure `ExpectedVersion` in scoped configuration.

The tool requirement is declared **verify-only** (no `AptPackage`): no distro package
carries a pinned Regal release. Provision the pinned upstream release binary into the
sandbox baseline:

```sh
# Baseline provisioning bake step (adjust arch; verify against the release checksums)
REGAL_VERSION=0.40.0
curl -fsSL "https://github.com/open-policy-agent/regal/releases/download/v${REGAL_VERSION}/regal_Linux_x86_64" -o /usr/local/bin/regal
chmod 0755 /usr/local/bin/regal
regal version   # must print the pinned version
```

## Enabling

The plugin is **disabled by default**. It loads only when allowlisted and enabled:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.regal"],
      "Enabled": ["codeybox.regal"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.regal" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.regal`, hot-reloadable per run:

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `0.40.0` | Pinned Regal release. A different installed version fails closed as infrastructure. |
| `ConfigPath` | `null` | Path to a Regal configuration file passed as `--config-file`. If omitted, Regal auto-discovers `.regal/config.yaml` (or `.regal.yaml`) in the repository. |
| `Targets` | `.` | Comma-separated list of repo-relative files or directories to lint. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact Regal rule identifiers to include or drop (e.g. `use-assignment-operator`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repository-relative paths dropped from findings. Setting replaces the default list. |
| `ExtraArguments` | — | Extra CLI arguments appended to argv (e.g. `--fail-level warning`). `--format`/`-f` and `--fix` are rejected: the former is the auditor's parsing contract, the latter would rewrite the audited tree. |
| `TimeoutSeconds` | `300` | Per-run execution bound. Exceeding it is an infrastructure failure. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/findings bounds; excess is reported as truncated. |

## Default scope

By default, the auditor runs `regal lint --format sarif .` from the work-tree root —
Regal walks the tree itself and lints only `*.rego` files — and filters out findings
in `vendor/`, `third_party/`, and `node_modules/`. The audit is strictly read-only:
there is no fix mode (`--fix` is rejected), and nothing is downloaded during the audit.
