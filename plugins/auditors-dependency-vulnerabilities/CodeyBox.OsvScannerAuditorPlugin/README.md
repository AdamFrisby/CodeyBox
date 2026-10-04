# CodeyBox: OSV-Scanner Dependency Vulnerabilities Auditor

Auditor plugin wrapping [osv-scanner](https://github.com/google/osv-scanner):
it scans the audited repository's worktree
(`osv-scanner scan source --recursive .`) for known vulnerabilities in
project dependencies by matching every recognised lockfile against the OSV
database — reporting every match as an audit finding from osv-scanner's
SARIF report.

## What it reports

- One finding per SARIF result on **stdout** (`--format sarif`).
  osv-scanner's rule id (a CVE such as `CVE-2021-23337`, a GHSA such as
  `GHSA-35jh-r3h4-6jhm`, or another OSV identifier) becomes the finding's
  rule id, so `IncludedRules`/`ExcludedRules` select by vulnerability.
- **Severity: mapped from CVSS, severity-driven gate.** osv-scanner emits
  every SARIF result at level `warning` — the level carries no signal —
  and instead records the computed CVSS score per rule in the rule
  metadata (`properties["security-severity"]`, e.g. `"8.1"`). The parser
  converts each score to a qualitative token using the standard CVSS bands
  (Critical ≥ 9.0, High 7.0–8.9, Medium 4.0–6.9, Low 0–3.9, unknown when the
  tool supplies no usable score), and the auditor maps those tokens to
  CodeyBox severities: `critical`/`high` → Error, `medium`/`unknown` →
  Warning, `low` → Info. Error findings (CVSS ≥ 7.0) fail the audit;
  warnings are advisory. `MinimumSeverity` only drops findings, it never
  raises them.
- **Location: file, no real line.** The SARIF location carries the lockfile
  or manifest path as found from the scan root (`file:///…` absolute URIs
  are relativized onto the worktree, e.g. `package-lock.json`), preserved
  in the finding. osv-scanner attaches no line region to dependency
  results — the match is against the lockfile as a whole, not a line — so
  findings are file-level.

## What it cannot see

- **Container images.** The scan subcommand is fixed to `scan source`
  (the worktree). `scan image` targets need a container runtime the
  worktree audit does not have and are out of scope.
- **Unmatchable packages.** Files with no recognised lockfile, packages
  with no version, and ecosystems osv-scanner does not cover produce no
  matches — absence of findings is not proof of absence of vulnerable
  code. A worktree with nothing to scan exits `0` with an empty report
  (see below), not with a proof of cleanliness.
- **Reachability.** The report lists every vulnerable dependency whether
  or not the audited code calls into the vulnerable symbol — there is no
  call-graph filtering on this path.
- **Licenses and deprecations.** `--licenses` and the deprecated-package
  flag are reserved: this auditor's verdict is vulnerabilities only.

## Exit codes and failure classification

osv-scanner follows the common "1 = findings" convention — verified
against the 2.6.0 binary:

| Exit | stdout | Meaning | Classification |
|---|---|---|---|
| `0` | SARIF | ran (clean, or nothing to scan under `--allow-no-lockfiles` — the SARIF is the verdict either way) | pass / findings |
| `1` | SARIF | ran and matched at least one vulnerability | findings |
| `127` | log text | could not run: bad flag, unreadable config file, extraction error | infrastructure |
| `128` | — | no package sources found (unreachable with the auditor's pinned `--allow-no-lockfiles`, classified all the same) | infrastructure |
| `129` | log text | could not run: vulnerability-database query failed | infrastructure |
| `130` | log text | could not run: invalid (unparseable or misused) config file | infrastructure |
| `126`/`127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

A missing `osv-scanner` binary, a version mismatch, a timeout, and
unparseable output are likewise infrastructure failures naming the tool —
never a passing audit. Do not override `--format`/`--output-file` via
`ExtraArguments`: the parser reads SARIF from stdout, and a redirected
report breaks the parse loudly (the run fails closed as infrastructure,
it does not pass).

## Version pinning and provisioning

The auditor is pinned to **osv-scanner `2.6.0`** (`ExpectedVersion` in
scoped config): extractors, matchers, and severity data change between
releases, so an unpinned binary would change findings under you.
`osv-scanner --version` is probed before every run.

One tool requirement is declared, **verify-only** (no `AptPackage` — no
distro package carries a pinned osv-scanner):

- `osv-scanner` — provision the versioned upstream release binary
  (`https://github.com/google/osv-scanner/releases`) through
  `CodeyBox:MultipassExtraRuncmd` / `CodeyBox:Incus:ExtraRuncmd` or
  `ExecutableProvisions`.

The requirement reaches baseline provisioning only while the plugin is
enabled.

## Policy configuration and repository-controlled files

osv-scanner loads an `osv-scanner.toml` sitting **beside each scanned
lockfile** — at any depth, not just the worktree root. Its
`IgnoredVulns` entries and `PackageOverrides` ignore rules suppress
matches the audit subject authors (verified against 2.6.0: a one-entry
file dropped its CVE from the report; `--config` with an operator file
restores the full report, proving the suppression lives in the repo
file).

Any such file anywhere in the worktree fails the audit as a
deterministic infrastructure error by default. Operators who deliberately
trust repo-authored config set `TrustRepositorySuppression: true`. An
operator who needs a fixed organizational policy sets `ConfigPath` to a
config provisioned outside the repository: the path is canonicalized in
the sandbox (`realpath -m`, collapsing relative paths, `..` segments,
and symlinks) and rejected when it resolves inside the worktree — a
relative `--config` resolves against the worktree, so an in-tree path
would hand the diff author flag control. Pinning `ConfigPath` replaces
the default policy source; it does not lift the gate.

`--allow-no-lockfiles` is passed on every invocation so
dependency-free worktrees (and lockfiles yielding zero packages) report
clean instead of tripping the no-package-sources exit — consistent with
the sibling auditors' clean runs. The flag is reserved in
`ExtraArguments` because it is part of the declared exit-code contract.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.osv-scanner"],
      "Enabled": ["codeybox.osv-scanner"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.osv-scanner" }
```

Only then does the declared `osv-scanner` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.osv-scanner`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `2.6.0` | Pinned osv-scanner release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | — (gated per-directory `osv-scanner.toml` chain) | `--config` — an operator-owned config file provisioned **outside** the repository. In-tree paths are rejected deterministically. Does not lift the repository-suppression gate. |
| `TrustRepositorySuppression` | `false` | Allow repository-authored `osv-scanner.toml` files (at any depth) instead of failing closed. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids (CVE, GHSA, …) to keep/drop. `ExcludedRules` covers the operator-owned equivalent of an `IgnoredVulns` entry. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings. Re-include a path by overriding the list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell), e.g. `--no-resolve`, `--verbosity`, `--offline-vulnerabilities` with a pre-seeded database. Reserved: `--format`/`-f`, `--output`, `--output-file`, `--config`, `--recursive`/`-r`, `--allow-no-lockfiles`, `--lockfile`/`-L`, `--sbom`/`-S`, `--licenses`, `--serve` — rejected deterministically with a pointer to the scoped key covering the same need. |
| `TimeoutSeconds` | `300` | Per-run bound — covers lockfile extraction, database queries, and matching; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Network egress

The auditor declares `AuditCapabilities.Network`: every run queries the
OSV database (via the default `deps.dev` data source), so those hosts
must be in the deployment's `AuditToolAllowedHosts` list. A run that
cannot reach the database fails loudly as infrastructure — it never
passes on stale or absent data. Fully offline deployments cannot use
this auditor as configured; `--offline-vulnerabilities` with a
pre-seeded database is available via `ExtraArguments`, with the verdict
then reflecting the staleness of the operator's cache.

## Default scope

`scan source --recursive .` at the worktree root — every lockfile the
built-in extractors recognise, including nested directories.
`vendor/`, `third_party/`, and `node_modules/` are excluded by default:
findings there describe upstream code and usually duplicate the
manifest-declared match for the same package — noise that trains
operators to ignore the auditor. Re-include a path by overriding
`ExcludePaths` in scoped config. Container-image targets are out of
scope (see above).
