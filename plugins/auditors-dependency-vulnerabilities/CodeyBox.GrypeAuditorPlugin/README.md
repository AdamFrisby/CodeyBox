# CodeyBox: grype Dependency Vulnerability Auditor

Auditor plugin wrapping [grype](https://github.com/anchore/grype):
it scans the audited repository's worktree (`grype dir:.`) for known
vulnerabilities in language dependencies (npm, PyPI, Maven, Go, RubyGems,
and more) and in OS packages with enough distro context to match, reporting
every match as an audit finding from grype's SARIF report.

## What it reports

- One finding per SARIF result on **stdout** (`-o sarif`). Grype's rule id
  (`<vulnerability-id>-<package-name>`, e.g. `GHSA-35jh-r3h4-6jhm-lodash`,
  `CVE-2023-44487-nghttp2`) becomes the finding's rule id, so
  `IncludedRules`/`ExcludedRules` select by vulnerability — and note the
  same CVE in two packages is two rule ids.
- **Severity: mapped, severity-driven gate.** Grype collapses its own
  severities onto SARIF levels upstream (`critical`/`high` → `error`,
  `medium` → `warning`, `low`/`negligible`/`unknown` → `note`), and the
  auditor maps those levels to CodeyBox severities: `error` → Error,
  `warning` → Warning, `note` → Info, unknown → Warning. Error findings
  (grype high/critical) fail the audit; warnings are advisory.
  `MinimumSeverity` only drops findings, it never raises them.
- **Location: file, no real line.** The SARIF location carries the package
  path as found from the scan root (e.g. `package-lock.json`), preserved in
  the finding. Grype emits a stub `1,1–1,1` region for every result — it
  knows which file a package came from, not which line — so treat the `:1`
  suffix as "file-level", not as pointing at line 1.

## What it cannot see

- **Container images.** The scan target is fixed to `dir:.` (the worktree).
  `image:`/`registry:` targets need a container runtime or registry
  credentials the worktree audit does not have and are out of scope.
- **Unmatchable packages.** Packages with no version, no distro context
  for OS packages, and ecosystems grype does not cover produce no matches —
  absence of findings is not proof of absence of vulnerable code.
- **Fixed-ness filtering by default.** All matches are reported whether or
  not a fix exists; pass `--only-fixed` via `ExtraArguments` to drop
  matches with no available fix.
- **VEX evaluation without operator input.** Grype only applies VEX
  documents the operator supplies (`--vex`); the repository cannot
  suppress findings with a VEX file it authors — no such file is
  auto-loaded.

## Exit codes and failure classification

Grype does **not** follow the common "0 = clean, 1 = findings" convention —
verified against the 0.119.0 binary: without `--fail-on` it exits `0`
whether or not it matched anything. The auditor always passes
`--fail-on negligible` (the lowest named severity) so the exit separates
"ran" from "could not run":

| Exit | stdout | Meaning | Classification |
|---|---|---|---|
| `0` | SARIF | ran (clean, or only below-threshold/unknown-severity matches — the SARIF is the verdict either way) | pass / findings |
| `2` | SARIF | ran, `--fail-on` threshold tripped (a match at/above negligible) | findings |
| `1` | usage/error text | could not run: bad flag, unscannable target, unparseable config, vulnerability-database load failure | infrastructure |
| `126`/`127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

A missing `grype` binary, a version mismatch, a timeout, and unparseable
output are likewise infrastructure failures naming the tool — never a
passing audit. Do not override `-o`/`--output` via `ExtraArguments`: the
parser reads SARIF, and a second output format breaks the parse loudly
(the run fails closed as infrastructure, it does not pass).

## Version pinning and provisioning

The auditor is pinned to **grype `0.119.0`** (`ExpectedVersion` in scoped
config): matchers and severity assignments change between releases, so an
unpinned binary would change findings under you. `grype --version` is
probed before every run.

One tool requirement is declared, **verify-only** (no `AptPackage` — no
distro package carries a pinned grype):

- `grype` — provision the versioned upstream release tarball
  (`https://github.com/anchore/grype/releases`) through
  `CodeyBox:MultipassExtraRuncmd` / `CodeyBox:Incus:ExtraRuncmd` or
  `ExecutableProvisions`, then pre-seed the vulnerability database
  (`grype db update`) so cold sandbox runs do not pay a multi-hundred-MB
  download inside the 300 s default timeout — or at all on offline
  deployments.

The requirement reaches baseline provisioning only while the plugin is
enabled.

## Policy configuration and repository-controlled files

Grype loads `.grype.yaml` (or `.grype.yml`, `.grype/config.yaml`,
`.grype/config.yml`) from the working directory — the audited worktree
root. Its `ignore:` rules and `exclude:` globs are a suppression surface
the audit subject could use to hide a match (verified: a one-rule
`.grype.yaml` dropped a real match from the report), so their presence
fails the audit as a deterministic infrastructure error by default.
Operators who deliberately trust repo-authored config set
`TrustRepositorySuppression: true`. An operator who needs a fixed
organizational policy sets `ConfigPath` to a config provisioned outside
the repository; this does not lift the gate (grype may merge discovered
config), it only replaces the default policy source.

The application self-update phone-home (`check-for-app-update`) is
disabled by the auditor via the environment — it is latency and egress,
not signal, and the version under audit is the pinned baseline build.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.grype"],
      "Enabled": ["codeybox.grype"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.grype" }
```

Only then does the declared `grype` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.grype`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `0.119.0` | Pinned grype release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | — (repo's `.grype.yaml` chain) | `--config` — an operator-owned config file provisioned in the baseline, replacing the default policy source. Does not lift the repository-config gate. |
| `TrustRepositorySuppression` | `false` | Allow repository-authored `.grype.yaml` (and twins) instead of failing closed. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids (`<vuln-id>-<package>`) to keep/drop. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings. Re-include a path by overriding the list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell), e.g. `--only-fixed`, `--distro`, `--exclude`, `--vex`. Do not pass `-o`/`--output` — the parser reads SARIF. |
| `TimeoutSeconds` | `300` | Per-run bound — covers cataloguing, database validation, and matching; exceeding it is infrastructure, not a pass. Pre-seed the database so cold runs fit. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Network egress

The auditor declares `AuditCapabilities.Network`: grype validates and
auto-updates its vulnerability database on every run, so the database
hosts must be in the deployment's `AuditToolAllowedHosts` list or the run
fails loudly as infrastructure. Fully offline deployments pre-seed the
database into the baseline (`grype db update` at bake time).

## Default scope

`grype dir:.` at the worktree root — the whole tree, language
dependencies and matchable OS packages together. `vendor/`,
`third_party/`, and `node_modules/` are excluded by default: findings
there describe upstream code and usually duplicate the manifest-declared
match for the same package — noise that trains operators to ignore the
auditor. Re-include a path by overriding `ExcludePaths` in scoped config.
Container-image targets are out of scope (see above).
