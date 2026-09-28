# CodeyBox: KICS Infrastructure-as-Code Security Auditor

Auditor plugin wrapping [KICS](https://kics.io) (Checkmarx "Keeping
Infrastructure as Code Secure"): it runs `kics scan` over the audited
repository and reports every IaC misconfiguration it finds — Terraform,
Kubernetes manifests, Dockerfiles, CloudFormation, Ansible, OpenAPI, and the
rest of KICS's supported platforms — as audit findings.

## What it reports

- One finding per (query, file) pair in KICS's JSON report
  (`--report-formats json`). KICS groups affected files under each query; the
  parser flattens that to one finding per location.
- `Title` carries the query UUID (the rule id) plus the query name; the
  description carries the tool, rule, tool severity, location, the query
  description, and the query/file metadata KICS supplies (`issue_type`,
  `platform`, `category`, `similarity_id`). KICS's `search_key`/`expected_value`/`actual_value`
  fields are deliberately **not** copied: for the "Passwords And Secrets"
  queries they contain the matched literal secret, and findings are sent to
  the rework prompt, webhooks, and the persisted audit report.
- `Location` is `path:line` — repo-relative for the default `.` target;
  absolute paths are relativized against the probed scan root.
- **Severity mapping (declared, never raw pass-through):**

  | KICS | CodeyBox | Gate |
  |---|---|---|
  | `CRITICAL`, `HIGH` | `Error` | blocks the audit |
  | `MEDIUM` | `Warning` | advisory |
  | `LOW`, `INFO`, `TRACE` | `Info` | informational |
  | unrecognised | `Warning` | advisory |

  `MinimumSeverity` can only drop findings, never raise them.
- **`kics/incomplete-scan`** — a synthesized `Warning` finding emitted when
  the report's `files_failed_to_scan` or `queries_failed_to_execute` counters
  are nonzero. A scan that could not parse part of the tree covered less
  than it appears to; the gap must be visible, not silent.

## What it cannot see

- **Misconfigurations below the query corpus.** KICS matches its bundled
  rego queries against IaC text; anything outside the corpus — wrong values
  that parse fine, missing policy context, runtime security — is invisible.
- **Non-IaC files.** KICS only parses its supported platforms; application
  source is out of scope by construction.
- **Rendered or templated output.** KICS scans files on disk. Helm charts
  and kustomize overlays are checked as their raw templates; rendered
  manifests they would produce are not seen.
- **Files exceeding KICS's max file size**, and files/queries the engine
  failed on — those surface as the `kics/incomplete-scan` warning finding,
  not as silently missing coverage.
- **Findings under an `ExcludePaths` prefix** are dropped from the report —
  the filter is post-scan, so excluded trees still cost scan time. To also
  skip scanning them, pass KICS's own `-e`/`--exclude-paths` through
  `ExtraArguments`.

## Report routing

KICS cannot stream its report: `--report-formats` always writes files under
`--output-path` (the output name is basename-sanitised, so `/dev/stdout`
cannot be named directly). Before the scan, the auditor creates a fresh
per-run directory under the sandbox temp area containing a
`results.json` → `/dev/stdout` symlink, and points `--output-path` at it.
KICS's report open follows the symlink and the JSON report lands on captured
stdout, where the shared parser reads it. `--silent` is load-bearing — it
suppresses KICS's console output, so stdout carries only the report.

The per-run directory is minted under the sandbox temp area and is not
explicitly removed: VM sandboxes discard the whole temp area with the
instance, so nothing accumulates. Under the host `ProcessSandboxProvider`
(developer machines) a tiny directory per run can linger in the host temp
directory until the OS sweeps it.

## Exit codes and failure classification

KICS does **not** follow the common "0 = clean, 1 = findings" convention —
verified against the v2.x source (`internal/console/helpers/exit_handler.go`,
`cmd/console/main.go`):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | scan completed (always, under the pinned `--ignore-on-exit results`) | pass / findings from report |
| `20`–`60` | results at INFO/LOW/MEDIUM/HIGH/CRITICAL (the semantic exit codes — reachable only if the ignore pin is overridden) | findings |
| `126` | engine error — KICS's own `EngineErrorCode`, also used for flag-parse and config errors | infrastructure |
| `130` | signal interrupt | infrastructure |
| `127` | not found / not executable | infrastructure |
| anything else | unknown convention | infrastructure (fails loud, never a pass) |

The auditor pins `--ignore-on-exit results`: KICS writes the report before
computing its result exit code, so a completed scan — findings or not —
always exits `0`, and only "could not run" is non-zero. A missing `kics`
binary, a version mismatch, a timeout, and an absent/unparseable report are
likewise infrastructure failures naming the tool — never a passing audit.

## Version pinning

Pinned to **KICS `2.2.0`** (`ExpectedVersion` in scoped config): the query
corpus changes between releases, so an unpinned scanner changes findings
under you. `kics version` is probed before every run; a missing binary, an
unrecognised version string, or a version other than the pinned one is an
infrastructure failure.

The tool requirement is declared **verify-only** — no `AptPackage`: no
distro package carries KICS. Provision the pinned upstream release into the
sandbox baseline via `CodeyBox:MultipassExtraRuncmd` /
`CodeyBox:Incus:ExtraRuncmd` or `ExecutableProvisions`, e.g.:

```sh
# baseline bake step (adjust arch; verify against the release checksums)
KICS_VERSION=2.2.0
curl -fsSL "https://github.com/Checkmarx/kics/releases/download/v${KICS_VERSION}/kics_${KICS_VERSION}_linux_amd64.tar.gz" -o /tmp/kics.tar.gz
mkdir -p /opt/kics
tar -xzf /tmp/kics.tar.gz -C /opt/kics
ln -sf /opt/kics/kics /usr/local/bin/kics
# The unpacked assets/ directory (queries + libraries) must sit next to the
# real kics executable — KICS resolves it relative to os.Executable, which
# follows the symlink.
kics version   # must print the pinned version
```

## The repository-config surface

KICS binds flags through viper: with a single `-p` target and no `--config`,
it loads a `kics.config` file sitting next to the scan path — which would be
the audited repository. A committed `kics.config` could inject
`exclude-queries`, `exclude-severities`, or other flags and silently empty
the report. The auditor therefore always passes `--config`: a generated
empty JSON file by default, or your `ConfigFile` path. KICS's bind order
only applies config values to flags absent from argv, so the pipeline flags
(`--output-path`, `--silent`, `--ignore-on-exit`, …) cannot be turned by a
config file — but query selection still binds, so `ConfigFile` must be an
absolute path that resolves **outside** the audited worktree. A relative
path (KICS resolves it against the scan cwd — the worktree) or an absolute
path inside the tree is rejected as a deterministic configuration failure
before the scan runs.

`KICS_*` environment variables can also set un-passed flags; they come from
the sandbox baseline (operator-controlled), not from the audited tree.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.kics"],
      "Enabled": ["codeybox.kics"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.kics" }
```

Only then does the declared `kics` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.kics`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `2.2.0` | Pinned KICS release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `Targets` | `.` | Comma-separated paths, each becoming a `--path` argument. Repo-relative paths keep finding locations repo-relative. |
| `ConfigFile` | — (generated empty config) | Absolute path passed to `--config`; must resolve outside the audited worktree (rejected otherwise). See "The repository-config surface" above. |
| `Platforms` | — (all) | Comma-separated KICS platform ids (`terraform`, `k8s`, `dockerfile`, `cloudFormation`, `ansible`, `openAPI`, `dockerCompose`, …), each becoming a `--type` entry. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids to keep/drop — KICS query UUIDs, plus `kics/incomplete-scan` for the coverage finding. Post-scan filtering. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Post-scan filter; setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Reserved flags are rejected deterministically: `--output-path`/`-o`, `--output-name`, `--report-formats`, `--config`, `--ignore-on-exit`, `--silent`/`-s`, `--ci`, `--verbose`/`-v`, `--path`/`-p` — use the scoped keys instead. Everything else (e.g. `--exclude-queries`, `-e`/`--exclude-paths`, `--fail-on`, `--queries-path`) passes through to KICS. |
| `TimeoutSeconds` | `600` | Per-run bound; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps. The report rides stdout, so a report larger than the stream cap is truncated — the parse fails closed as infrastructure; raise the cap for very large trees. |

## Default scope

`kics scan -p .` over the whole work tree: KICS detects supported IaC
formats itself, so application source is not linted. On top of that,
findings under `vendor/`, `third_party/`, and `node_modules/` are dropped by
default — vulnerable templates shipped inside a dependency describe the
upstream package, not the change under audit, and reporting them would
train operators to ignore the auditor. Narrow the scan itself with
`Targets`, `Platforms`, or `-e` in `ExtraArguments`.

The auditor declares `AuditCapabilities.None`: the query corpus is embedded
in the baseline provision, so the scan needs no network and no agent
credentials. (KICS does a best-effort latest-release check during startup
that degrades gracefully without egress.)
