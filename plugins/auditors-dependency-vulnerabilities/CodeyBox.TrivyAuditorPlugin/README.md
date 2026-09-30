# CodeyBox: Trivy Dependency, Config and Container Vulnerabilities Auditor

Auditor plugin wrapping [Trivy](https://github.com/aquasecurity/trivy):
it scans the audited repository's worktree (`trivy fs .`) for known
vulnerabilities in language dependencies (npm, PyPI, Maven, Go, RubyGems,
and more) and OS packages reachable from the filesystem, for
misconfigurations in IaC and Dockerfiles, and for embedded secrets —
reporting every match as an audit finding from Trivy's SARIF report.

## What it reports

- One finding per SARIF result on **stdout** (`--format sarif`). Trivy's
  rule id (a CVE such as `CVE-2020-8203`, a misconfiguration id such as
  `DS-0002` or `AWS-0086`) becomes the finding's rule id, so
  `IncludedRules`/`ExcludedRules` select by vulnerability or check.
- **Severity: mapped, severity-driven gate.** Trivy collapses its own
  severities onto SARIF levels upstream (`CRITICAL`/`HIGH` → `error`,
  `MEDIUM` → `warning`, `LOW`/`UNKNOWN` → `note`), and the auditor maps
  those levels to CodeyBox severities: `error` → Error, `warning` →
  Warning, `note` → Info, unknown → Warning. Error findings (Trivy
  high/critical) fail the audit; warnings are advisory.
  `MinimumSeverity` only drops findings, it never raises them.
- **Location: file, no real line.** The SARIF location carries the file
  path as found from the scan root (e.g. `package-lock.json`,
  `Dockerfile`, `main.tf`), preserved in the finding. Trivy emits a stub
  `1,1–1,1` region for every result — it knows which file a match came
  from, not which line — so treat the `:1` suffix as "file-level", not as
  pointing at line 1.

## What it cannot see

- **Container images.** The scan target is fixed to `fs .` (the worktree).
  `image:`/`repository:` targets need a container runtime or registry
  credentials the worktree audit does not have and are out of scope — but
  OS-package vulnerabilities reachable from the filesystem scan (unpacked
  rootfs content, distro package databases) are reported.
- **Unmatchable packages.** Packages with no version, ecosystems Trivy
  does not cover, and misconfiguration checks outside Trivy's built-in
  bundle produce no matches — absence of findings is not proof of absence
  of vulnerable code.
- **Licenses.** The `license` scanner is not enabled by default: license
  detection is not a vulnerability signal. Operators who want it add it
  via `Scanners`.
- **VEX evaluation without operator input.** Trivy only applies VEX
  documents the operator supplies (`--vex`); the repository cannot
  suppress findings with a VEX file it authors — no such file is
  auto-loaded.

## Exit codes and failure classification

Trivy follows **neither** direction of the common "1 = findings"
convention — verified against the 0.74.0 binary:

- Without `--exit-code` (how the auditor always runs) Trivy exits `0`
  whether or not anything matched (7 findings and clean fixtures both
  exit `0`).
- With `--exit-code 1` Trivy exits `1` both when findings trip the
  threshold **and** on fatal errors (bad flags, unscannable targets,
  config load failures, database failures) — the two are indistinguishable
  from the exit alone.

The auditor therefore never passes `--exit-code` (the flag is reserved in
`ExtraArguments`), collapsing the convention to "0 = ran":

| Exit | stdout | Meaning | Classification |
|---|---|---|---|
| `0` | SARIF | ran (clean or with findings — the SARIF is the verdict either way) | pass / findings |
| `1` | log text | could not run: bad flag, unscannable target, unparseable config, database load failure | infrastructure |
| `126`/`127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

A missing `trivy` binary, a version mismatch, a timeout, and unparseable
output are likewise infrastructure failures naming the tool — never a
passing audit. Do not override `--format`/`--output` via `ExtraArguments`:
the parser reads SARIF from stdout, and a redirected report breaks the
parse loudly (the run fails closed as infrastructure, it does not pass).

## Version pinning and provisioning

The auditor is pinned to **Trivy `0.74.0`** (`ExpectedVersion` in scoped
config): matchers, checks, and severity assignments change between
releases, so an unpinned binary would change findings under you.
`trivy --version` is probed before every run.

One tool requirement is declared, **verify-only** (no `AptPackage` — no
distro package carries a pinned Trivy):

- `trivy` — provision the versioned upstream release tarball
  (`https://github.com/aquasecurity/trivy/releases`) through
  `CodeyBox:MultipassExtraRuncmd` / `CodeyBox:Incus:ExtraRuncmd` or
  `ExecutableProvisions`, then pre-seed the vulnerability database
  (`trivy fs --download-db-only`) so cold sandbox runs do not pay a
  multi-hundred-MB download inside the 300 s default timeout — or at all
  on offline deployments.

The requirement reaches baseline provisioning only while the plugin is
enabled.

## Policy configuration and repository-controlled files

Trivy loads three files from the working directory — the audited worktree
root — by default, each a suppression surface the audit subject could use
to hide a match (all verified against 0.74.0: a `severity: [CRITICAL]`
`trivy.yaml` emptied a 7-finding report; a one-line `.trivyignore`
dropped its CVE; a malformed `trivy-secret.yaml` is read before the scan
starts):

- `trivy.yaml` (default `--config`): severity filters, `skip-dirs` /
  `skip-files` globs, and every other scan flag. (Trivy resolves this
  name against the current directory, not the scan target — a copy buried
  in a subdirectory has no effect, which is why the gate watches the
  worktree root.)
- `.trivyignore` (default `--ignorefile`): per-CVE ignores.
- `trivy-secret.yaml` (default `--secret-config`): secret-rule
  exclusions.

Their presence fails the audit as a deterministic infrastructure error by
default. Operators who deliberately trust repo-authored config set
`TrustRepositorySuppression: true`. An operator who needs a fixed
organizational policy sets `ConfigPath` to a config provisioned outside
the repository: the path is canonicalized in the sandbox (`realpath -m`,
collapsing relative paths, `..` segments, and symlinks) and rejected
when it resolves inside the worktree — Trivy resolves a relative
`--config` against the worktree, so an in-tree path would hand the diff
author flag control.

Two CLI pins further harden the run against a repo file that slips past
review: `--scanners` and `--severity` are passed explicitly on every
invocation, and CLI flags take precedence over file values on those two
axes (verified: an explicit `--severity` restored all 7 findings under a
restrictive `trivy.yaml`). Everything the file alone controls
(`skip-dirs`, `skip-files`, ignores) stays behind the gate.

The version-update notice (`--skip-version-check`) is disabled by the
auditor — it is latency and egress, not signal, and the version under
audit is the pinned baseline build. Database and check-bundle updates
stay on: they are the signal.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.trivy"],
      "Enabled": ["codeybox.trivy"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.trivy" }
```

Only then does the declared `trivy` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.trivy`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `0.74.0` | Pinned Trivy release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `Scanners` | `vuln,misconfig,secret` | Comma-separated Trivy scanners (`vuln`, `misconfig`, `secret`, `license` — exact match, unknown values fail closed). Trivy's own default omits `misconfig`; the auditor default adds it. |
| `ConfigPath` | — (gated repo `trivy.yaml` chain) | `--config` — an operator-owned config file provisioned **outside** the repository. In-tree paths are rejected deterministically. |
| `TrustRepositorySuppression` | `false` | Allow repository-authored `trivy.yaml` / `.trivyignore` / `trivy-secret.yaml` instead of failing closed. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids (CVE, `DS-*`, `AWS-*`, …) to keep/drop. `ExcludedRules` covers the operator-owned equivalent of a `.trivyignore`. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings. Re-include a path by overriding the list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell), e.g. `--skip-dirs`, `--skip-files`, `--skip-db-update`. Reserved: `--format`/`-f`, `--output`/`-o`, `--exit-code`, `--config`/`-c`, `--scanners`, `--severity`/`-s`, `--ignorefile`, `--secret-config` — rejected deterministically with a pointer to the scoped key covering the same need. |
| `TimeoutSeconds` | `300` | Per-run bound — covers cataloguing, database validation, and matching; exceeding it is infrastructure, not a pass. Pre-seed the database so cold runs fit. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Network egress

The auditor declares `AuditCapabilities.Network`: Trivy validates and
auto-updates its vulnerability database and its misconfiguration checks
bundle on every run, so those hosts must be in the deployment's
`AuditToolAllowedHosts` list or the run fails loudly as infrastructure.
Fully offline deployments pre-seed the database and the checks bundle
into the baseline (`trivy fs --download-db-only` at bake time, plus
`--skip-db-update` / `--skip-check-update` via `ExtraArguments` to stop
per-run update attempts).

## Default scope

`trivy fs .` at the worktree root — the whole tree, with dependency
vulnerabilities, misconfigurations, and secrets together (Trivy's own
default would omit misconfigurations; the auditor default restores
them). `vendor/`, `third_party/`, and `node_modules/` are excluded by
default: findings there describe upstream code and usually duplicate the
manifest-declared match for the same package — noise that trains
operators to ignore the auditor. Re-include a path by overriding
`ExcludePaths` in scoped config. Container-image targets are out of scope
(see above); OS-package vulnerabilities reachable from the filesystem
scan are reported.
