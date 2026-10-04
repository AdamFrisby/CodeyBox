# CodeyBox: Zizmor GitHub Actions Security Auditor

Auditor plugin wrapping [zizmor](https://github.com/zizmorcore/zizmor):
it runs `zizmor --offline --no-progress --format sarif` over the audited
repository and reports each finding — template injection, overly broad
permissions, unpinned `uses:` references, insecure runner usage, and more —
as an audit finding with its rule id (`zizmor/<audit>`) and file/line
location preserved.

## What it reports

- One finding per zizmor result. The title carries the rule id, so
  `IncludedRules`/`ExcludedRules` select by rule id
  (e.g. `zizmor/template-injection`, `zizmor/excessive-permissions`,
  `zizmor/unpinned-uses`). The full rule list is in zizmor's
  [audit documentation](https://docs.zizmor.sh/audits/) (rule ids match the
  SARIF `ruleId` values with the `zizmor/` prefix).
- `Location` is the repo-relative workflow/action path with a `:line`
  suffix wherever zizmor supplies a start line.
- **Severity: declared mapping, hybrid gate — not blocking by default.**
  zizmor grades each finding informational/low/medium/high and surfaces the
  grade in SARIF as `note` (informational, low), `warning` (medium), or
  `error` (high). The auditor translates that vocabulary to CodeyBox's, so
  its "high" means what every other auditor's "high" means:

  | zizmor severity | SARIF `level` | CodeyBox | Gate effect |
  |---|---|---|---|
  | high | `error` | `Error` | fails the audit |
  | medium | `warning` | `Warning` | advisory |
  | informational, low | `note` | `Info` | informational |
  | (unrecognised level) | — | `Warning` | advisory, never silently informational |

  Raw tool tokens never reach findings; the level is preserved in the
  finding description as proof the value flowed through the mapping.
  `MinimumSeverity` only drops findings, it never raises them. The auditor
  is therefore a merge gate for high-severity findings, not a blocker on
  every note — and the plugin itself is disabled by default (see
  [Enabling](#enabling)).

## What it cannot see

- **Anything outside collected inputs.** The default scan collects
  workflows and composite actions (respecting `.gitignore`); application
  code, Dockerfiles, and deployment manifests around them are out of scope.
  Dependabot and pre-commit inputs are collected only with an explicit
  operator `--collect` override.
- **Online-only audits.** The default scan passes `--offline`: audits that
  require GitHub API access do not run. Their absence is a documented blind
  spot, not a pass over that surface.
- **Runtime semantics.** zizmor is static analysis: it cannot tell whether
  a workflow would go green, whether a referenced action is compromised at
  the pinned SHA, or whether a self-hosted runner is actually exposed.
- **Suppressed rules.** The scan passes `--no-ignores`, so ignore comments
  in workflow files and `ignore` rules in `zizmor.yml` are inert by default
  (set `TrustRepositorySuppression=true` to honor them). Whole-rule
  `disable` entries in a repository `zizmor.yml` still apply — that is the
  scanner's own configuration surface. Operators who want a fixed
  configuration pin it with `ConfigFile` (outside the audited tree).
- **Repository configuration file.** zizmor discovers `zizmor.yml` /
  `zizmor.yaml` in the repository root or its `.github` directory for each
  scanned input (rule disables, ignores, per-audit tuning, severity
  remaps). With `ConfigFile` set, that single file applies globally instead
  and no repository configuration is discovered.
- **Findings under an `ExcludePaths` prefix** are dropped from the report
  (zizmor still scans them — the filter is post-scan). Re-include by
  overriding `ExcludePaths`.
- **Repositories without GitHub Actions.** zizmor exits `3`
  ("no inputs collected") when the tree yields no workflows or actions —
  that is an infrastructure failure, not a pass. Enable this auditor only
  on projects that use GitHub Actions.

## Exit codes and failure classification

Verified by running the pinned binary (1.30.1) across a matrix of clean,
issue-bearing, empty-tree, bad-flag, and error-inducing invocations:

| Exit | stdout | Meaning | Classification |
|---|---|---|---|
| `0` | SARIF (`results: []` or findings) | ran, with or without findings (SARIF mode never uses a non-zero findings exit) | findings (empty → pass) |
| `11`–`14` | SARIF in a SARIF-reporting build, plain diagnostics otherwise | ran, highest finding informational/low/medium/high (only emitted in non-SARIF modes) | findings when the body parses as SARIF, otherwise infrastructure via the parser |
| `1` | none / diagnostics | could not run (audit error, e.g. strict-collection parse failure, unreadable input) | infrastructure |
| `2` | none / diagnostics | could not run (argument parsing failure) | infrastructure |
| `3` | none / diagnostics | could not run (no inputs collected) | infrastructure |
| `126` / `127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

The discriminator is the report, not the exit code: the declared
findings-producing exits are parsed, and an exit without a parseable SARIF
document fails closed as infrastructure through the parser. A missing
`zizmor` binary, a version mismatch, a timeout, and unparseable output are
likewise infrastructure failures naming the tool — never a passing audit.
A scanner that silently passes because it did not run is the worst outcome
available, and this auditor has no path that produces it.

## Version pinning

The auditor is pinned to **zizmor `1.30.1`** (`ExpectedVersion` in scoped
config): the rule set, severity assignments, and SARIF shape change between
releases, so an unpinned binary would change findings under you.
`zizmor --version` is probed before every run; any other version is an
infrastructure failure.

The tool requirement is declared **verify-only** — no `AptPackage`: no
distro package carries a version-pinned zizmor. Provision the pinned
upstream release into the sandbox baseline via
`CodeyBox:MultipassExtraRuncmd` / `CodeyBox:Incus:ExtraRuncmd` or
`ExecutableProvisions`, e.g.:

```sh
# baseline bake step (adjust arch; verify against the release checksums)
ZIZMOR_VERSION=1.30.1
curl -fsSL "https://github.com/zizmorcore/zizmor/releases/download/v${ZIZMOR_VERSION}/zizmor-x86_64-unknown-linux-gnu.tar.gz" -o /tmp/zizmor.tar.gz
tar -xzf /tmp/zizmor.tar.gz -C /tmp zizmor
install -c -m 0755 /tmp/zizmor /usr/local/bin/zizmor
zizmor --version   # must print the pinned version
```

Only when the plugin is enabled does the declared `zizmor` tool requirement
reach baseline provisioning (presence-verified at bake time; nothing is
apt-installed because no `AptPackage` is declared).

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.zizmor"],
      "Enabled": ["codeybox.zizmor"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.zizmor" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.zizmor`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `1.30.1` | Pinned zizmor release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigFile` | — (repo `zizmor.yml` discovery) | Global `--config` path. Point outside the audited tree for a configuration the repository cannot narrow. |
| `Targets` | — (work-tree root) | Comma-separated repo-relative file or directory paths, passed as positional scan inputs. Unset → `.` (zizmor collects workflows and composite actions itself, respecting `.gitignore`). Entries must be repo-relative (no absolute paths, no `..`). |
| `TrustRepositorySuppression` | `false` | When `true`, drop the default `--no-ignores` flag so ignore comments and `zizmor.yml` ignore rules take effect. Default `false`: the audit subject cannot silence findings at finding granularity. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids (`zizmor/<audit>`) to keep/drop (finding-level filter, applied after the scan). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Post-scan filter. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell), e.g. `--persona,pedantic`, `--collect,workflows`, or `--min-severity,high`. **Managed by the auditor:** `--format` (the SARIF report is the parsing contract), `--fix` (auditors never rewrite the tree), `--config`/`-c`/`--no-config` (managed by the `ConfigFile` knob), a lone `-` (the stdin sentinel would redirect input away from the worktree), and `--gh-token` (the default scan is offline so a token is inert, and credentials never ride auditor argv) are rejected with a deterministic infrastructure error. |
| `TimeoutSeconds` | `300` | Per-run bound; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Default scope

`zizmor` over the work-tree root: the tool collects workflows and
composite actions itself (respecting `.gitignore`), so only GitHub Actions
inputs are ever inspected — application and vendored code is out of scope
by construction. The finding-level `ExcludePaths` default additionally
drops `vendor/`, `third_party/`, and `node_modules/`: a vendored snapshot
carrying its own `.github` directory describes upstream workflows, not the
change under audit, and reporting it would train operators to ignore the
auditor. An operator who wants the scan itself narrowed to specific files
uses `Targets`.
