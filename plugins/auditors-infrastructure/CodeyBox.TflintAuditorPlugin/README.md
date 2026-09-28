# CodeyBox: TFLint Terraform Auditor

Auditor plugin wrapping [TFLint](https://github.com/terraform-linters/tflint):
it runs `tflint --format sarif --recursive` over the audited repository and
reports each result — lint findings from the `tflint` run and repository
problems from the `tflint-errors` run — as an audit finding with its rule id
and file/line location preserved.

## What it reports

- One finding per SARIF result, from both runs in the report:
  - `tflint` run — lint results, e.g. `terraform_unused_declarations`,
    `terraform_required_version`, `terraform_required_providers`. The title
    carries the rule id, so `IncludedRules`/`ExcludedRules` select by rule.
  - `tflint-errors` run — repository problems the tool could not look past:
    unparseable `.tf` files (`Invalid expression`), an unreadable config
    (`application_error: Failed to load TFLint config`), a required plugin
    that was never installed (`application_error: … Did you run "tflint
    --init"?`). These are findings, not silent skips — and because they map
    to `error` they fail the audit loudly.
- `Location` is the repo-relative file path with a `:line` suffix wherever
  tflint supplies a region (lint results do; some `tflint-errors` results
  carry no location and report without one).
- **Severity: driven by the tool, mapped explicitly — advisory by default,
  blocking on errors.** `error` → `Error` (fails the audit), `warning` →
  `Warning` (advisory), `notice`/`note`/`info` → `Info` (informational), and
  anything unrecognised from a foreign build → `Warning` (visible, never
  silently informational). Raw tool levels never reach findings; the tool
  level is preserved in the finding description as proof the value flowed
  through the mapping.

## What it cannot see

- **Files tflint does not inspect.** Only `.tf` / `.tf.json` files (and
  variable files passed explicitly, which the auditor does not pass) are
  analysed — packaging, CI, and wrapper scripts around the Terraform are out
  of scope.
- **Provider-correctness beyond the enabled rulesets.** The default scan runs
  the bundled `terraform` language ruleset (deprecated syntax, unused
  declarations, naming, required versions/constraints). Cloud-specific rules
  (valid instance types, …) need the AWS/Azure/GCP ruleset plugins
  provisioned at bake time — see below.
- **Plan/apply-time semantics.** TFLint is static analysis: it cannot tell
  whether values are sane, credentials valid, state consistent, or `apply`
  would converge.
- **Suppressed rules.** The audited repository's `.tflint.hcl` (rule toggles,
  presets) and `tflint-ignore: <rule>` comments narrow what the tool reports —
  that is the scanner's own configuration surface. Operators who want a fixed
  rule set pin it with `ConfigFile` (outside the audited tree) or the
  `EnableRules`/`DisableRules`/`OnlyRules` knobs.
- **Findings under an `ExcludePaths` prefix** are dropped from the report
  (tflint still scans them — the filter is post-scan). Re-include by
  overriding `ExcludePaths`.

## Exit codes and failure classification

Verified by running the pinned binary (0.64.0) across a matrix of clean,
issue-bearing, broken-HCL, missing-plugin, missing-config, and bad-flag
invocations — tflint does **not** follow the common "0 = clean, 2 = findings,
1 = could not run" split cleanly, because exit `1` doubles as the exit for
repository problems that still produce a SARIF report:

| Exit | stdout | Meaning | Classification |
|---|---|---|---|
| `0` | SARIF, empty results | ran clean | pass |
| `2` | SARIF with results | ran, issues found | findings |
| `1` | SARIF with `tflint-errors` results | ran, repository problem (broken HCL, missing plugin, unreadable config) | findings (error severity — fails the audit) |
| `1` | none / not SARIF | could not run (unknown flags, bad `--format`) | infrastructure |
| `126` / `127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

The discriminator is the report, not the exit code: exits `0`, `1`, and `2`
are findings-producing, and an exit without a parseable SARIF document fails
closed as infrastructure through the shared parser. A missing `tflint`
binary, a version mismatch, a timeout, and unparseable output are likewise
infrastructure failures naming the tool — never a passing audit. A security
scanner that silently passes because it did not run is the worst outcome
available, and this auditor has no path that produces it.

## Version pinning

The auditor is pinned to **tflint `0.64.0`** (`ExpectedVersion` in scoped
config): the bundled ruleset, severity assignments, and SARIF shape change
between releases, so an unpinned binary would change findings under you.
`tflint --version` is probed before every run; any other version is an
infrastructure failure.

The tool requirement is declared **verify-only** — no `AptPackage`: no
distro package carries tflint. Provision the pinned upstream release into
the sandbox baseline via `CodeyBox:MultipassExtraRuncmd` /
`CodeyBox:Incus:ExtraRuncmd` or `ExecutableProvisions`, e.g.:

```sh
# baseline bake step (adjust arch; verify against the release checksums)
TFLINT_VERSION=0.64.0
curl -fsSL "https://github.com/terraform-linters/tflint/releases/download/v${TFLINT_VERSION}/tflint_linux_amd64.zip" -o /tmp/tflint.zip
python3 -c "import zipfile; zipfile.ZipFile('/tmp/tflint.zip').extractall('/tmp/tflint-out')"
install -c -m 0755 /tmp/tflint-out/tflint /usr/local/bin/tflint
tflint --version   # must print the pinned version
```

If the repository under audit requires external ruleset plugins (e.g.
`tflint-ruleset-aws`), install them at bake time with `tflint --init`
against a pinned plugin version — the auditor never runs `--init` itself,
because that would fetch unpinned code during the audit. An uninstalled
plugin is an error-severity finding, not a silent skip.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.tflint"],
      "Enabled": ["codeybox.tflint"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.tflint" }
```

Only then does the declared `tflint` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.tflint`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `0.64.0` | Pinned tflint release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigFile` | — (repo `.tflint.hcl`) | `--config` file. Point outside the audited tree for a rule set the repository cannot narrow. |
| `EnableRules` | — | Comma-separated rule names, each passed as repeatable `--enable-rule`. |
| `DisableRules` | — | Comma-separated rule names, each passed as repeatable `--disable-rule`. |
| `OnlyRules` | — | Comma-separated rule names, each passed as repeatable `--only` (the tool disables all other defaults). |
| `Recursive` | `true` | Pass `--recursive`. Without it tflint inspects only the top-level directory and a nested Terraform tree passes vacuously — set `false` only when the Terraform lives at the root. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids to keep/drop (finding-level filter, applied after the scan). |
| `ExcludePaths` | `.terraform/`, `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Post-scan filter. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell), e.g. `--filter` globs or `--var-file`. **Managed by the auditor:** `--format`/`-f` (the SARIF report is the parsing contract) and `--fix` (auditors never rewrite the tree) are rejected with a deterministic infrastructure error. |
| `TimeoutSeconds` | `300` | Per-run bound; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

tflint's `--minimum-failure-severity` and `--force` only reshape the exit
code, not the report — and exits `0`, `1`, and `2` are all
findings-producing here — so passing them via `ExtraArguments` cannot mask
findings; the verdict always comes from the SARIF document.

## Default scope

`tflint --format sarif --recursive` from the work-tree root: recursive
because the non-recursive default inspects only the top-level directory, and
the finding-level `ExcludePaths` default drops `.terraform/` (modules
downloaded by `terraform init` — upstream code, not the change under audit)
alongside the conventional vendored trees (`vendor/`, `third_party/`,
`node_modules/`). Reporting downloaded modules would train operators to
ignore the auditor; an operator who also wants the scan itself narrowed uses
`Recursive: false`, `ConfigFile`, or `--filter` via `ExtraArguments`.
