# CodeyBox: Gitleaks Secrets Auditor

Auditor plugin wrapping [gitleaks](https://github.com/gitleaks/gitleaks):
it scans the audited repository's committed source **and full git history**
(`gitleaks git`) for leaked credentials and reports each hit as an audit
finding with the gitleaks rule id and `file:line` location.

## What it reports

- One finding per gitleaks result. The title carries the rule id
  (e.g. `generic-api-key`, `aws-access-token`); the description carries the
  tool, rule, tool-level, location, and gitleaks's message (which includes the
  commit sha for history hits). `Location` is `path:startLine`.
- **Secrets are never written into findings or raw output** — the scan runs
  with `--redact=100`, so report snippets contain masked secrets.
- **Severity: blocking.** gitleaks emits no per-finding severity, so the
  plugin's declared mapping sends every result to `Error`. A detected
  credential fails the audit — that is the intended gate. There is no
  advisory mode; narrow the scope instead (`ExcludedRules`, `ExcludePaths`,
  or — with `TrustRepositorySuppression` — the repo's `.gitleaks.toml`
  allowlist).

## What it cannot see

- **Uncommitted worktree changes.** `gitleaks git` scans committed history;
  a secret staged but never committed is out of scope. Audit sandboxes audit
  produced commits, so this is the intended boundary.
- **Non-git directories.** If the working directory is not a git repository
  the scan cannot run and is reported as infrastructure, not a pass.
- **Anything gitleaks's rules don't cover.** Findings are exactly what the
  pinned gitleaks build detects; custom detectors need an operator-supplied
  `--config` via `ExtraArguments` (or `TrustRepositorySuppression` to honor a
  `.gitleaks.toml` in the repository — see below).
- **Binary and archived payloads.** In `git` mode, binary files produce no
  text fragments to scan and archives are never unpacked
  (`--max-archive-depth` stays at gitleaks's default `0`). A secret
  committed inside a `.zip` or any other binary blob is never reported.
- **A repo-root `.gitleaks.toml`'s own contents.** gitleaks exempts its
  config path from the scan in every commit, so a secret committed inside
  that file is invisible to it. The plugin therefore fails closed when the
  file exists — see *Repository-controlled suppression* below.
- **Secrets committed under an `ExcludePaths` prefix.** `vendor/`,
  `third_party/`, and `node_modules/` are finding filters: gitleaks still
  scans them, but findings there are dropped — so a leak committed under an
  excluded prefix never surfaces. Re-include by overriding `ExcludePaths`.
- **Runtime provenance of a finding.** `git`-mode SARIF locations point at the
  file path and line; the originating commit is in the finding description,
  not the location.

## Exit codes and failure classification

gitleaks's stock convention is unusable directly — findings exit
`--exit-code` (default `1`) and *every* failure mode also exits `1`
(config load, scan error, report-write failure). The plugin overrides
`--exit-code` to `4`:

| Exit | Meaning | Classification |
|---|---|---|
| `0` | ran clean, no findings | pass |
| `4` | ran, secrets found | findings |
| `1` | could not run (config/scan/report error, incl. gitleaks's own `--timeout`) | infrastructure |
| `126` / `127` | usage error / binary not executable | infrastructure |
| anything else | unknown convention | infrastructure (fails loud, never a pass) |

An error exit also wins over a findings exit upstream, so a partial scan can
never surface as a verdict.

## Version pinning

The auditor is pinned to **gitleaks `8.30.1`** (`ExpectedVersion` in scoped
config). Because a scanner's rule set changes between releases, an unpinned
scanner would change findings under you, so the plugin probes
`gitleaks version` before every run and reports an infrastructure failure on
any other version. Note: gitleaks's SARIF `tool.driver.semanticVersion` is a
hardcoded `v8.0.0` upstream, which is why the report cannot carry the check.

The tool requirement is declared **verify-only** — no `AptPackage`: the distro
package cannot carry a version pin, and the one Ubuntu LTS ships (8.16.x)
predates the flags this auditor needs (`--report-path -`, `git` subcommand,
all ≥ 8.24.0). Provision the pinned upstream release in your sandbox baseline
instead, e.g. via `CodeyBox:MultipassExtraRuncmd` / `CodeyBox:Incus:ExtraRuncmd`
or `ExecutableProvisions`:

```sh
# baseline bake step (adjust arch; verify against the release checksums.txt)
GITLEAKS_VERSION=8.30.1
curl -fsSL "https://github.com/gitleaks/gitleaks/releases/download/v${GITLEAKS_VERSION}/gitleaks_${GITLEAKS_VERSION}_linux_x64.tar.gz" -o /tmp/gitleaks.tar.gz
curl -fsSL "https://github.com/gitleaks/gitleaks/releases/download/v${GITLEAKS_VERSION}/gitleaks_${GITLEAKS_VERSION}_checksums.txt" -o /tmp/gitleaks.sha256
(cd /tmp && grep "gitleaks_${GITLEAKS_VERSION}_linux_x64.tar.gz" gitleaks.sha256 | sha256sum -c -)
tar -xzf /tmp/gitleaks.tar.gz -C /usr/local/bin gitleaks
gitleaks version   # must print 8.30.1
```

## Enabling

The plugin is **disabled by default** — like every plugin outside the four
grandfathered bundled ones it loads only when named in both gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.gitleaks"],
      "Enabled": ["codeybox.gitleaks"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.gitleaks" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.gitleaks`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `8.30.1` | Pinned gitleaks release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `TrustRepositorySuppression` | `false` | When `true`, the audited repository's own suppression surfaces are honored: `.gitleaks.toml` config, `.gitleaksignore` fingerprints, and `gitleaks:allow` comments. When `false`, a repo-root `.gitleaksignore` or `.gitleaks.toml` (worktree or git history) fails the run closed as infrastructure. See below — off by default because the audit subject authors those files. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. Everything maps to `error`, so this only matters if the mapping changes. |
| `IncludedRules` / `ExcludedRules` | — | Exact gitleaks rule ids to keep/drop (e.g. `generic-api-key`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--config <sandbox path>` to pin an operator-controlled ruleset. A repeated flag wins over the built-in default — so take care: `--log-opts` replaces gitleaks's `git log` flags, silently dropping `--all`/`--full-history` history coverage, and `--baseline-path`, `--config`, or `--enable-rule` narrow or suppress findings by design. |
| `TimeoutSeconds` | `300` | Per-run bound; also forwarded to gitleaks's own `--timeout`. Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

**Repository-controlled suppression is off by default.** gitleaks honors
three suppression surfaces authored inside the audited repository — a
`.gitleaks.toml` (custom rules *and* allowlists), a `.gitleaksignore`
(fingerprint suppression; gitleaks loads it unconditionally — no flag
disables it), and inline `gitleaks:allow` comments. `.gitleaks.toml` is also
special to the scanner itself: gitleaks exempts its own config path from the
scan in every commit, so a secret committed inside that file — even one
deleted before the audit — is never reported. Because the audited agent can
write all three, the plugin neutralizes them unless the operator opts in:

- the scan exports `GITLEAKS_CONFIG_TOML` pinning gitleaks's built-in
  ruleset, which outranks `<repo>/.gitleaks.toml` as *config* (gitleaks
  config precedence: `--config` → `GITLEAKS_CONFIG` →
  `GITLEAKS_CONFIG_TOML` → repo `.gitleaks.toml` → built-in default — so an
  operator `--config` in `ExtraArguments` or a baseline `GITLEAKS_CONFIG`
  still wins);
- `--ignore-gitleaks-allow` disables `gitleaks:allow` comments;
- `--gitleaks-ignore-path` points at an inert path, and pre-scan checks
  **fail closed as infrastructure** when `<repo>/.gitleaksignore` or
  `<repo>/.gitleaks.toml` exists in the worktree, or when `.gitleaks.toml`
  appears anywhere in git history (a committed-then-deleted copy would
  still exempt that path from the scan). The gate applies regardless of an
  operator `--config` — remove the file(s), or set
  `TrustRepositorySuppression=true` to trust repository-controlled
  suppression.

Set `TrustRepositorySuppression: true` under
`CodeyBox:Plugins:codeybox.gitleaks` when the audited repositories
legitimately carry `.gitleaks.toml` detectors or `.gitleaksignore`
fingerprints. Understand the trade-off below before enabling it.

The alternative to trusting repo files is operator-controlled config: pin a
ruleset outside the repo via `ExtraArguments` (`--config
/abs/path/in/sandbox.toml`) or a baseline `GITLEAKS_CONFIG`, and manage
suppression through `ExcludedRules`/`ExcludePaths`.

## Default scope

Vendored and dependency trees (`vendor/`, `third_party/`, `node_modules/`)
are excluded by default: secrets reported there belong to upstream packages,
not the change under audit, and the noise would teach operators to ignore the
auditor. The exclusion is a finding filter — gitleaks still scans those paths
(scan-time exclusion belongs to an operator-pinned `--config`). Re-include them
by overriding `ExcludePaths`.
