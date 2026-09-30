# CodeyBox: Dependency-Check Dependency Vulnerability Auditor

Auditor plugin wrapping [OWASP
dependency-check](https://github.com/dependency-check/DependencyCheck):
it scans the audited repository's worktree (`dependency-check --scan .`)
for known vulnerabilities in its dependencies (Java, .NET, npm, Python,
Go, Ruby, and more — resolved from manifests and binaries by the tool's
analyzer set), reporting every matched CVE as an audit finding from the
tool's native JSON report.

## What it reports

- One finding per (dependency, vulnerability) pair in the JSON report.
  The vulnerability `name` (usually a CVE id, e.g. `CVE-2023-44487`) becomes
  the finding's rule id, so `IncludedRules`/`ExcludedRules` select by
  vulnerability — and note the same CVE in two dependencies is two findings
  with one shared rule id.
- **Severity: mapped, severity-driven gate (blocking on Error).**
  dependency-check's JSON `severity` comes from the CVSS base-severity
  enums (`CRITICAL`/`HIGH`/`MEDIUM`/`LOW`/`NONE`, or `Unknown` for unscored
  vulnerabilities), and the auditor maps them to CodeyBox severities:
  `CRITICAL`/`HIGH` → Error, `MEDIUM` → Warning, `LOW`/`NONE` → Info,
  `Unknown` (unscored, no CVSS to judge by) → Warning, unknown → Warning.
  Error findings fail the audit; warnings are advisory.
  `MinimumSeverity` only drops findings, it never raises them.
- **Location: file, no real line.** The report carries the dependency's
  absolute `filePath`, relativized to the worktree and preserved in the
  finding (e.g. `package-lock.json`, `lib/struts-2.0.0.jar`).
  dependency-check reports no line numbers, so treat the location as
  file-level. The finding message carries the CVSS score, the CWE list, and
  the tool's description for triage.

## What it cannot see

- **Packages no analyzer resolves.** Binaries without identifiable
  evidence, ecosystems the tool does not cover, and dependencies missed by
  disabled analyzers produce no matches — absence of findings is not proof
  of absence of vulnerable code.
- **Reachability.** A match says "this version is known-vulnerable", not
  "the audited code reaches the vulnerable path" — whether a finding is
  exploitable requires reading the code, not the report.
- **Container images and deployed artifacts.** The scan target is fixed to
  `--scan .` (the worktree). Image registries and out-of-tree artifacts are
  out of scope.
- **Suppressed matches.** Operator-owned `--suppression` files
  (`SuppressionPaths`) genuinely remove matches from the report — that is
  their purpose. Audit them like any other allowlist: every rule should
  carry a `notes` element and, where the risk is time-bounded, an `until`
  date.
- **SARIF consumers.** The auditor reads the native JSON report, not SARIF:
  dependency-check's SARIF template hardcodes `"level": "warning"` on every
  result, so SARIF carries no severity signal. Do not point external SARIF
  tooling at this auditor's expectations.

## Exit codes and failure classification

dependency-check does **not** follow the common "0 = clean, 1 = findings"
convention — verified against the 12.1.0 `App` source. The auditor always
passes `--failOnCVSS 0` (the tool default is `11`, i.e. never fail), so the
exit separates "ran" from "could not run":

| Exit | Report | Meaning | Classification |
|---|---|---|---|
| `0` | JSON written | ran, no vulnerabilities | pass |
| `15` | JSON written | ran, at least one vulnerability (any score — `0` is the lowest threshold) | findings |
| `1`/`2` | none | could not run: CLI parse failure | infrastructure |
| `4` | none | could not run: bad settings/property file | infrastructure |
| `8`/`9` | none | could not run: NVD/hosted-data update or database failure | infrastructure |
| `11` | none | could not run: database exception | infrastructure |
| `12` | none | could not run: report exception | infrastructure |
| `13`/`14` | maybe | could not run: fatal/non-fatal analysis errors (`14` fires after reports are written, but the scan is still incomplete) | infrastructure |
| `126`/`127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

A missing `dependency-check` binary, a version mismatch, a timeout, and a
missing or unparseable report file are likewise infrastructure failures
naming the tool — never a passing audit. Do not override `--out`/`--format`/
`--scan`/`--failOnCVSS`/`--updateonly`/`--suppression`/`--propertyfile`/
`--hints` via `ExtraArguments`: they are reserved (rejected
deterministically) because they would redirect the report sink, rescope the
scan, change the exit contract, skip the scan the report must come from, or
hand the tool an unguarded suppression/property/hints file. Suppression
travels only through the guarded `SuppressionPaths` knob; bake property-file
tuning into the baseline image instead.

## Version pinning and provisioning

The auditor is pinned to **dependency-check `12.1.0`**
(`ExpectedVersion` in scoped config): analyzers, CPE matching, and report
shape change between releases, so an unpinned binary would change findings
under you. `dependency-check --version` (printing
`dependency-check version X.Y.Z`) is probed before every run.

One tool requirement is declared, **verify-only** (no `AptPackage` — no
distro package carries a pinned dependency-check):

- `dependency-check` — unpack the versioned upstream release archive
  (`https://github.com/dependency-check/DependencyCheck/releases`) with
  `bin/dependency-check` on `PATH` through
  `CodeyBox:MultipassExtraRuncmd` / `CodeyBox:Incus:ExtraRuncmd` or
  `ExecutableProvisions`, pre-seed the NVD data directory (`--updateonly`
  at bake time) so cold sandbox runs do not pay a multi-hundred-MB download
  inside the 10-minute default timeout, and provide an NVD API key (see
  below).

The requirement reaches baseline provisioning only while the plugin is
enabled.

## Policy configuration and repository-controlled files

dependency-check loads suppression files, hints, and property files **only
from explicit CLI flags** (verified in the 12.1.0 `App.populateSettings` —
there is no working-directory auto-discovery), so a `suppression.xml`
committed in the audited repository is inert by itself. The only path that
could hand the tool a repository-authored suppression is the operator's own
`SuppressionPaths` key, and that path is guarded: every entry is
canonicalized in the sandbox (`realpath -m`, collapsing relative paths,
`..` segments, and symlinks) and rejected deterministically when it
resolves inside the audited worktree. Set `SuppressionPaths` to
operator-owned files provisioned in the baseline, outside any audited tree.

The centrally published hosted-suppressions feed *is* applied automatically
over the network — that is upstream data, not repository-controlled, but be
aware it narrows matches without appearing in your configuration.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.dependency-check"],
      "Enabled": ["codeybox.dependency-check"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.dependency-check" }
```

Only then does the declared `dependency-check` tool requirement reach
baseline provisioning (presence-verified at bake time; nothing is
apt-installed because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.dependency-check`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `12.1.0` | Pinned dependency-check release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `SuppressionPaths` | — (no suppression) | Comma-separated operator-owned suppression XML files (`--suppression`, repeatable). Each must resolve outside the audited worktree or the run fails deterministically. The guarded knob is the only path — `--suppression` via `ExtraArguments` is reserved. |
| `NoUpdate` | `false` | `--noupdate` — skip NVD/hosted-suppression/RetireJS updates. Requires a pre-seeded data directory; without one the scan fails loudly as infrastructure. Per-scan OSS Index queries may still need egress. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. |
| `IncludedRules` / `ExcludedRules` | — | Exact vulnerability names (usually CVE ids) to keep/drop. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings. Re-include a path by overriding the list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell), e.g. `--exclude`, `--nvdApiKey`, analyzer toggles. `--out`/`--format`/`--scan`/`--failOnCVSS`/`--updateonly` are reserved (see above). |
| `TimeoutSeconds` | `600` | Per-run bound — covers analyzer passes, feed validation, and matching; exceeding it is infrastructure, not a pass. Pre-seed the data directory so steady-state runs need a fraction of this. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `8 MiB` / `1000` | Output/result caps; overruns are reported as truncation. The stream default is raised from the shared 1 MiB because the JSON report embeds per-dependency evidence — a clipped report fails closed as unparseable infrastructure. |

## Network egress and the NVD API key

The auditor declares `AuditCapabilities.Network`: dependency-check
validates and updates the NVD CVE feed, the hosted-suppressions feed, and
RetireJS data on every run (unless `NoUpdate`), and OSS Index analyzers
query per scan — those hosts must be in the deployment's
`AuditToolAllowedHosts` egress list or the run fails loudly as
infrastructure. From 12.x the NVD API requires an API key for timely
updates ([request one](https://nvd.nist.gov/developers/request-an-api-key)):
provision it as `NVD_API_KEY` in the baseline environment (inherited by the
tool process) or pass `--nvdApiKey` via `ExtraArguments`. Without a key,
cold-run updates throttle and can exceed the timeout. Fully offline
deployments pre-seed the data directory into the baseline (`--updateonly`
at bake time) and set `NoUpdate`.

## Default scope

`dependency-check --scan .` at the worktree root — every analyzer the tool
ships, over the whole tree. `vendor/`, `third_party/`, and `node_modules/`
are excluded by default: findings there describe upstream code and usually
duplicate the manifest-declared match for the same package — noise that
trains operators to ignore the auditor. Re-include a path by overriding
`ExcludePaths` in scoped config; narrow the scan itself with `--exclude`
via `ExtraArguments`.
