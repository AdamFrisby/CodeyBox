# CodeyBox: Cfn-Lint CloudFormation Auditor

Auditor plugin wrapping [cfn-lint](https://github.com/aws-cloudformation/cfn-lint):
it runs `cfn-lint --format sarif -- <targets>` over the audited repository and
reports each SARIF result as an audit finding with its rule id and file/line
location preserved.

## What it reports

- One finding per SARIF result, e.g. `E3002` (unknown property),
  `E3031` (value fails its pattern), `W2001` (unused parameter), `I1022`
  (prefer `Fn::Sub` over `Fn::Join`). The title carries the rule id, so
  `IncludedRules`/`ExcludedRules` select by rule.
- Repository problems the tool can still report arrive as findings, not
  silent skips: an unparseable template (`E0000`), a missing template file
  (`E0003`), a YAML document that is not a CloudFormation template
  (`E1001`), and a run with no templates at all (`E1001` with no location).
  These map to `error` and fail the audit loudly.
- `Location` is the repo-relative file path with a `:line` suffix wherever
  cfn-lint supplies a region. Results without a file location (the
  no-templates `E1001`) report without one.
- **Severity: driven by the tool, mapped explicitly — advisory by default,
  blocking on errors.** `error` → `Error` (fails the audit), `warning` →
  `Warning` (advisory), `note`/`informational` → `Info` (informational), and
  anything unrecognised from a foreign build → `Warning` (visible, never
  silently informational). Raw tool levels never reach findings; the tool
  level is preserved in the finding description as proof the value flowed
  through the mapping.
- Quirk worth knowing: warning results (e.g. `W2001`) carry **no** `level`
  in the SARIF report — SARIF defaults an absent level to `warning`, which
  the map sends to advisory. Verified against the pinned binary.

## What it cannot see

- **Files that are not CloudFormation templates.** Only the configured
  `Targets` (or the repository `.cfnlintrc` `templates:` list) are
  analysed — application code, CI configs, and wrapper scripts around the
  templates are out of scope.
- **Deploy-time semantics.** cfn-lint is static analysis: it cannot tell
  whether values are sane, credentials valid, stacks consistent, or
  `deploy` would converge.
- **Region-specific validity beyond the configured regions.** Resource
  schemas differ per region; the scan validates against `Regions` (unset →
  the tool's own default region set).
- **Suppressed rules.** The audited repository's `.cfnlintrc` (rule
  toggles, regions, template lists) and per-resource
  `Metadata: cfn-lint: config: ignore_checks:` overrides narrow what the
  tool reports — that is the scanner's own configuration surface. Operators
  who want a fixed rule set pin it with `ConfigFile` (outside the audited
  tree) or the `IncludeChecks`/`IgnoreChecks` knobs.
- **Findings under an `ExcludePaths` prefix** are dropped from the report
  (cfn-lint still scans them — the filter is post-scan). Re-include by
  overriding `ExcludePaths`.

## Exit codes and failure classification

Verified by running the pinned binary (1.57.0) across a matrix of clean,
error-bearing, warning-only, informational-only, broken-YAML, missing-file,
no-template, bad-flag, and variadic-flag-swallowing invocations — cfn-lint
returns a bitwise OR over the severities it found:

| Exit | stdout | Meaning | Classification |
|---|---|---|---|
| `0` | SARIF, empty results | ran clean | pass |
| `2` | SARIF with results | ran, error-severity issues found | findings |
| `4` | SARIF with results | ran, warning-severity issues found | findings |
| `8` | SARIF with results | ran, informational-only issues found | findings |
| `6` / `10` / `12` / `14` | SARIF with results | ran, combined severities found | findings |
| `1` | usage text, no SARIF | could not run (unknown flags) | infrastructure |
| `126` / `127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

The discriminator is the report, not the exit code: a declared
findings-producing exit without a parseable SARIF document fails closed as
infrastructure through the shared parser. A missing `cfn-lint` binary, a
version mismatch, a timeout, and unparseable output are likewise
infrastructure failures naming the tool — never a passing audit. A security
scanner that silently passes because it did not run is the worst outcome
available, and this auditor has no path that produces it.

## Version pinning

The auditor is pinned to **cfn-lint `1.57.0`** (`ExpectedVersion` in scoped
config): rule ids, severity assignments, and the SARIF shape change between
releases, so an unpinned binary would change findings under you.
`cfn-lint --version` is probed before every run; any other version is an
infrastructure failure.

The tool requirement is declared **verify-only** — no `AptPackage`: the
tool is pip-installed and the SARIF formatter needs the `sarif` extra.
Provision the pinned release into the sandbox baseline via
`CodeyBox:MultipassExtraRuncmd` / `CodeyBox:Incus:ExtraRuncmd` or
`ExecutableProvisions`, e.g.:

```sh
# baseline bake step (verify against the PyPI hashes)
pip install "cfn-lint[sarif]==1.57.0"
cfn-lint --version   # must print the pinned version
```

Refresh the bundled resource-provider schemas at bake time with
`cfn-lint --update-specs` if you want newer schemas than the pinned
release ships — the auditor never runs `--update-specs` itself, because
that would fetch unpinned data during the audit.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.cfnlint"],
      "Enabled": ["codeybox.cfnlint"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.cfnlint" }
```

Only then does the declared `cfn-lint` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.cfnlint`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `1.57.0` | Pinned cfn-lint release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigFile` | — (repo `.cfnlintrc`) | `--config-file` path. Point outside the audited tree for a rule set the repository cannot narrow. |
| `IncludeChecks` | — | Comma-separated rule ids or id prefixes (e.g. `I` for all informationals), emitted as the values of one `--include-checks` flag. Prefix matching is the tool's own semantics. |
| `IgnoreChecks` | — | Comma-separated rule ids or id prefixes (e.g. `W` for all warnings), emitted as the values of one `--ignore-checks` flag. Prefix matching is the tool's own semantics. |
| `Regions` | — (tool default) | Comma-separated regions (e.g. `us-east-1,eu-west-1`), emitted as the values of one `--regions` flag. |
| `Targets` | — (`.cfnlintrc` discovery) | Comma-separated repo-relative template paths, passed as positional arguments after a `--` separator. Set → replaces the config file's `templates:` discovery. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids to keep/drop (finding-level filter, applied after the scan — unlike the prefix-matching tool flags above). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Post-scan filter. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra template paths appended after the `--` separator (never via a shell). **Flags are rejected:** cfn-lint's selectors take a variable number of values, so a flag after the separator would be treated as a template file name — use the scoped keys above for flags. In particular `--format` (the SARIF report is the parsing contract), `--output-file` (it would divert the report), and `--update-specs` (unpinned network fetch during the audit) are unavailable by construction. |
| `TimeoutSeconds` | `300` | Per-run bound; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

cfn-lint's `--non-zero-exit-code` only reshapes the exit code, not the
report — and every declared findings-producing exit carries a SARIF
document — so it cannot mask findings; the verdict always comes from the
report. It is unavailable as a knob for the same reason flags are: there is
no supported spelling for passing it.

## Default scope

`cfn-lint --format sarif --` from the work-tree root with no template
arguments: the tool falls back to the repository `.cfnlintrc` `templates:`
list, and with neither it reports `E1001` and fails the audit — a tree
outside the tool's scope is never a vacuous pass. Enable this auditor only
on projects that use CloudFormation.

The auditor performs no filename-glob discovery of its own: a
non-CloudFormation YAML document handed to cfn-lint as a template reports
`E1001`, so scanning every `*.yaml` in the tree would manufacture findings
against files the tool was never meant to read (verified against the
pinned binary). The finding-level `ExcludePaths` default drops `vendor/`,
`third_party/`, and `node_modules/` — vendored templates describe upstream
code, not the change under audit. An operator who wants the scan itself
narrowed sets `Targets`.
