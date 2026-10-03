# CodeyBox: Checkov Infrastructure-as-Code Security Auditor

Auditor plugin wrapping [Checkov](https://www.checkov.io) (Bridgecrew): it runs
`checkov -d <targets>` over the audited repository and reports every failed IaC
check it finds — Terraform, CloudFormation, Kubernetes manifests, Dockerfiles,
Helm, Bicep, ARM, Ansible, Serverless, OpenAPI, and the rest of Checkov's
frameworks — as audit findings.

## What it reports

- One finding per failed check in Checkov's SARIF report (`-o sarif`). The
  report carries the check id (`CKV_*`), the severity-derived level, the message,
  and the file/line location per result, so no plugin-local parser exists to
  drift.
- `Title` carries the check id plus the first line of the message;
  the description carries the tool, rule, tool severity, and location.
- `Location` is `path:line` — repo-relative for the default `-d .` target.
- **Severity mapping (declared, never raw pass-through):**

  | Checkov | CodeyBox | Gate |
  |---|---|---|
  | `CRITICAL`, `HIGH` (`error`) | `Error` | blocks the audit |
  | `MEDIUM` (`warning`) | `Warning` | advisory |
  | `LOW`, `NONE` (`note`, `none`) | `Info` | informational |
  | unrecognised | `Warning` | advisory |

  `MinimumSeverity` can only drop findings, never raise them. The auditor is a
  merge gate for high/critical misconfigurations, not a blocker on every low
  note — it is **not blocking by default** in the "every finding fails" sense.

## What it cannot see

- **Misconfigurations below the check corpus.** Checkov matches its bundled
  checks against IaC text; anything outside the corpus — wrong values that parse
  fine, missing policy context, runtime security — is invisible.
- **Non-IaC files.** Checkov only parses its supported frameworks; application
  source is out of scope by construction.
- **Rendered or templated output.** Checkov scans files on disk. Helm charts
  and kustomize overlays are checked as their raw templates; rendered manifests
  they would produce are not seen.
- **Checks suppressed by inline `checkov:skip=<check>` comments.** That is the
  tool's normal suppression surface, visible in the scanned file; there is no
  tool flag to disable it, so the auditor does not try.
- **Findings under an `ExcludePaths` prefix** are dropped from the report —
  the filter is post-scan, so excluded trees still cost scan time. To also skip
  scanning them, pass Checkov's own `--skip-path` through `ExtraArguments`.

## Report routing

Checkov cannot stream SARIF: `-o sarif` prints a human-readable console summary
to stdout and writes the machine report to a file (`results.sarif` in the
working directory by default, `<output-file-path>/results_sarif.sarif` when
`--output-file-path` is set). The scan writes `results_sarif.sarif` as a plain
file inside a per-run directory under the sandbox temp area (outside the audited
worktree, so the scan never pollutes the diff), and after a findings-producing
exit the auditor reads it back through a separate bounded `cat` exec — the report
reaches only the parser. `--compact` (no code blocks) and `--quiet` (only failed
checks, no progress bars) stay pinned so the captured console summary stays small
and free of echoed source lines. A missing, oversized, or unparseable report fails
closed as infrastructure — never a pass.

The per-run directory is minted under the sandbox temp area and is not
explicitly removed: VM sandboxes discard the whole temp area with the instance,
so nothing accumulates. Under the host `ProcessSandboxProvider` (developer
machines) a tiny directory per run can linger in the host temp directory until
the OS sweeps it.

## Exit codes and failure classification

Checkov's convention was verified against the 3.3.x source
(`checkov/main.py`, `checkov/common/runners/runner_registry.py`) — the common
"0 = clean, 1 = findings" shape holds here, with two sharp edges:

| Exit | Meaning | Classification |
|---|---|---|
| `0` | scan completed, no failed checks | pass / findings from report |
| `1` | scan completed with failed checks | findings |
| `2` | crash (`exit_run`: `exit(0) if no_fail_on_crash else exit(2)`) or argparse/usage error | infrastructure |
| `126` | cannot execute | infrastructure |
| `127` | not found / not executable | infrastructure |
| anything else | unknown convention | infrastructure (fails loud, never a pass) |

Sharp edge: the SIGINT handler (`sys.exit('')`) also exits `1` — with no report
file. The discriminator is therefore the SARIF file, not the exit code alone: an
exit-`0`/`1` run without a readable report fails closed as infrastructure through
the report read. A missing `checkov` binary, a version mismatch, a timeout, and an
absent/unparseable report are likewise infrastructure failures naming the tool —
never a passing audit.

## Version pinning

Pinned to **Checkov `3.3.22`** (`ExpectedVersion` in scoped config): the check
corpus changes between releases, so an unpinned scanner changes findings under
you. `checkov --version` is probed before every run; a missing binary, an
unrecognised version string, or a version other than the pinned one is an
infrastructure failure.

The tool requirement is declared **verify-only** — no `AptPackage`: no distro
package carries a pinned Checkov. Provision the pinned upstream release into the
sandbox baseline via `CodeyBox:MultipassExtraRuncmd` /
`CodeyBox:Incus:ExtraRuncmd` or `ExecutableProvisions`, e.g.:

```sh
# baseline bake step (verify against the PyPI checksums)
pip install "checkov==3.3.22"
checkov --version   # must print the pinned version
```

## The repository-config surface

Checkov auto-loads `.checkov.yaml`/`.checkov.yml` from the scan directory, the
process working directory, and the home directory — which, for the default scan,
is the audited repository. A committed file could inject `skip-check`,
`soft-fail`, `framework`, or other keys and silently empty the report. Passing
`--config-file` alone does **not** neutralize it (checkov merges config files
per key: the explicit file wins per key, but keys it does not set still fall
through to the repository file).

The auditor therefore does both:

1. It always passes `--config-file`: a generated empty file in the report
   directory by default, or your `ConfigFile` path. Checkov resolves a relative
   configured path against its cwd — the worktree — so `ConfigFile` must resolve
   **outside** the audited worktree. Before the scan, the auditor canonicalizes
   the configured path and the scan cwd in the sandbox with `realpath -m` and
   rejects the run as a deterministic configuration failure when the canonical
   path lands inside the tree — relative paths, `..` segments, and symlinked
   components all collapse to the path Checkov would actually open.
2. It probes for repository `.checkov.yaml`/`.checkov.yml` files under the scan
   roots before every run and fails closed as deterministic infrastructure when
   one is present — unless you set `TrustRepositoryConfig` to `true`, explicitly
   accepting repository config.

`CKV_*` environment variables can also set un-passed flags; they come from the
sandbox baseline (operator-controlled), not from the audited tree.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.checkov"],
      "Enabled": ["codeybox.checkov"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.checkov" }
```

Only then does the declared `checkov` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed because
no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.checkov`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `3.3.22` | Pinned Checkov release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `Targets` | `.` | Comma-separated repo-relative paths, each becoming a repeatable `-d` argument. Must stay inside the worktree. |
| `Frameworks` | — (all) | Comma-separated Checkov framework ids (`terraform`, `cloudformation`, `kubernetes`, `dockerfile`, `helm`, `bicep`, `arm`, …), each becoming a repeatable `--framework` argument. |
| `IncludedChecks` | — (all) | Comma-separated check ids (`CKV_AWS_21`, …) or severities, emitted as one `--check` argument — any other checks are skipped by the tool. |
| `SkippedChecks` | — | Comma-separated check ids or severities, emitted as one `--skip-check` argument. |
| `ConfigFile` | — (generated empty config) | Absolute path passed to `--config-file`; must resolve outside the audited worktree (rejected otherwise). See "The repository-config surface" above. |
| `TrustRepositoryConfig` | `false` | Accept repository `.checkov.yaml`/`.checkov.yml` files instead of failing closed on them. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids to keep/drop — Checkov check ids (`CKV_*`). Post-scan filtering. |
| `ExcludePaths` | `.terraform/`, `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Post-scan filter; setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Reserved flags are rejected deterministically: `-o`/`--output`, `--output-file-path`, `--compact`, `--quiet`, `-d`/`--directory`, `-f`/`--file`, `--config-file`, `-c`/`--check`, `--skip-check`, `--framework`, `-s`/`--soft-fail`, `--soft-fail-on`, `--hard-fail-on` — use the scoped keys instead. Everything else (e.g. `--skip-framework`, `--skip-path`, `--external-checks-dir`) passes through to Checkov. |
| `TimeoutSeconds` | `600` | Per-run bound; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps. The report file read shares the stream cap, so a report larger than it fails closed as deterministic infrastructure; raise the cap or narrow the scan for very large trees. |

## Default scope

`checkov -d .` over the whole work tree: Checkov detects frameworks itself, so
application source is not linted. On top of that, findings under `.terraform/`,
`vendor/`, `third_party/`, and `node_modules/` are dropped by default —
downloaded modules and vendored templates describe upstream packages, not the
change under audit, and reporting them would train operators to ignore the
auditor. Narrow the scan itself with `Targets`, `Frameworks`,
`IncludedChecks`/`SkippedChecks`, or `--skip-path` in `ExtraArguments`.

The auditor declares `AuditCapabilities.None`: the check corpus ships inside the
provisioned package, so the default scan needs no network and no agent
credentials (no API key means local-only checks and no result upload).
