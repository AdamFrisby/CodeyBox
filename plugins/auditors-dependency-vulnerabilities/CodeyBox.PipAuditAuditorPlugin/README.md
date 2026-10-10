# CodeyBox: pip-audit Python Dependency Vulnerabilities

Auditor plugin wrapping [`pip-audit`](https://github.com/pypa/pip-audit)
(the Python Packaging Authority's scanner for known vulnerabilities in
Python dependencies) on the shared external-tool auditor base. Plugin id
`codeybox.pip-audit`, auditor name `codeybox:pip-audit`. **Disabled by
default** — add it to `Plugins:Enabled` to load it. The tool requirement
reaches baseline provisioning only while the plugin is enabled.

## What it reports

One finding per matched vulnerability: the primary advisory id as the
rule id (usually a `PYSEC-…` id), the affected `package@version`, known
aliases (`CVE-…`/`GHSA-…`), the fixed releases when the tool reports
any, and the tool's description. pip-audit supplies no file or line for
a match, so findings carry no location — the title and description
identify the package instead.

Dependencies pip-audit declined to audit (`skip_reason`, e.g. an
unresolvable spec) surface as advisory `warning` findings so a coverage
gap is visible rather than a silent partial pass.

## What it cannot see

- **Severity.** pip-audit reports no per-vulnerability severity, so every
  reported CVE maps to `error` (blocking). There is nothing to grade on —
  this matches pip-audit's own verdict (it exits non-zero on any match).
- **Code.** pip-audit matches resolved dependency versions against
  advisory data; it never reads your source. Reachability is not assessed.
- **Vendored copies.** Resolution-based matching reports the declared
  version set, never file-tree contents, so vendored trees produce no
  findings and `ExcludePaths` defaults to empty (there is no vendored
  noise to exclude; rule selection narrows instead).
- **Alias-aware rule filtering has a seam.** The tool's own
  `--ignore-vuln` (fed from your `ExcludedRules`, so it matches aliases)
  suppresses before the report; the shared post-filter matches the
  primary id only. Prefer excluding by the primary id; aliases are listed
  in each finding's description.

## Exit codes and failure classification

Verified against pip-audit 2.10.1 (`_cli.py` plus the documented codes):

- `0` — ran, no known vulnerabilities.
- `1` — **ambiguous by design**: either "ran and matched" (JSON manifest
  on stdout) or a fatal run failure (stderr diagnostic only — a missing
  project file, an unresolvable requirements file, a dead vulnerability
  service). The JSON manifest is the discriminator: exit `1` without a
  parseable manifest is infrastructure, never findings and never a pass.
- `2` (usage errors), `126`/`127` (cannot-execute) — infrastructure.

A missing `pip-audit` binary, an unrecognised version banner, or a
version other than the pin is an infrastructure failure naming the
tool — never a passing audit. A security scanner that silently passes
because it did not run is the worst outcome available.

## Version pinning and provisioning

Pinned to **pip-audit 2.10.1** (`ExpectedVersion` scoped-config
overrides the pin to match what you provisioned). The requirement is
verify-only — no distro apt package carries a pinned pip-audit — so
provision it into the sandbox baseline yourself, e.g.

```bash
python3 -m pip install pip-audit==2.10.1   # Python 3.10+ with pip
```

via `CodeyBox:MultipassExtraRuncmd` / `CodeyBox:Incus:ExtraRuncmd` or
`ExecutableProvisions`. Pre-seed nothing: pip-audit queries its
vulnerability service live on every run.

## Policy configuration and repository-controlled files

pip-audit reads **no config file from the repository under audit**
(there is no `--config` flag; `--ignore-vuln` is CLI-only and this
auditor derives it from your operator-owned `ExcludedRules`). The
dependency manifests are the audit subject, not policy. One caveat:
requirement files may carry resolver directives (`--index-url`,
`--extra-index-url`, `--find-links`) that steer where packages resolve
from — review those lines on untrusted trees, or audit fully-pinned
hashed requirements with `--no-deps` via `ExtraArguments`.

`PIP_AUDIT_OUTPUT` is stripped from the tool environment so the report
cannot be redirected off stdout; every other `PIP_AUDIT_*` knob is
already pinned by an explicit CLI flag.

## Enabling

```json
{
  "CodeyBox:Plugins:codeybox.pip-audit:ExpectedVersion": "2.10.1",
  "CodeyBox:Plugins:codeybox.pip-audit:RequirementsFiles": "requirements.txt,requirements-dev.txt",
  "CodeyBox:Plugins:codeybox.pip-audit:VulnerabilityService": "pypi"
}
```

## Configuration

| Key | Meaning |
| --- | --- |
| `ExpectedVersion` | Pinned pip-audit release (default `2.10.1`). |
| `RequirementsFiles` | Comma-separated repo-relative requirements files audited with repeatable `-r` instead of the project-path default. Must stay inside the worktree. |
| `VulnerabilityService` | `pypi` (default), `osv`, or `esms`; always passed explicitly. |
| `MinimumSeverity` | Shared threshold (`info`/`warning`/`error`); drops only. |
| `IncludedRules` / `ExcludedRules` | Exact-match rule selection on the primary id; `ExcludedRules` is additionally forwarded as `--ignore-vuln` (alias-aware). |
| `ExcludePaths` | Default empty (findings have no file locations). |
| `ExtraArguments` | Appended argv; `--format`/`-f`, `--output`/`-o`, `--fix`, `--dry-run`/`-d`, `--requirement`/`-r`, `--local`/`-l`, and `--vulnerability-service`/`-s` are rejected deterministically (each has a knob or is incompatible with auditing). |
| `TimeoutSeconds` / `MaxOutputBytesPerStream` / `MaxFindings` | Shared execution bounds (defaults 300 s, 1 MiB/stream, 1000 findings). |

## Network egress

Declares `AuditCapabilities.Network` unconditionally — there is no
offline mode. The vulnerability service (`https://pypi.org` by default,
`https://api.osv.dev` for `osv`) and the package index used for
resolution must be in `AuditToolAllowedHosts`. Fully offline
deployments should leave this plugin disabled.

## Gate behaviour

**Blocking by default**: any reported vulnerability is `error` and
fails the audit. Skipped dependencies are `warning` (advisory). State
this in your rollout notes — enabling the plugin turns known Python
CVEs into audit failures.
