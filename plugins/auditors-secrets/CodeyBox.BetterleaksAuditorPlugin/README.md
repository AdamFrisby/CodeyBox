# CodeyBox: Betterleaks Secrets Auditor

Auditor plugin wrapping [betterleaks](https://github.com/betterleaks/betterleaks):
it scans the audited repository's committed source **and full git history**
(`betterleaks git`) for leaked credentials and reports each hit as an audit
finding with the betterleaks rule id and `file:line` location. betterleaks is
maintained by the folks who made gitleaks (including the original author) and
accepts the same scan flags, so this auditor is the Gitleaks-compatible
alternative for operators that standardise on it.

## What it reports

- One finding per betterleaks result. The title carries the rule id
  (e.g. `slack-bot-token`, `generic-api-key`); the description carries the
  tool, rule, tool-level, location, and betterleaks's message (which includes
  the commit sha for history hits). `Location` is `path:startLine`.
- **Secrets are never written into findings or raw output** — the scan runs
  with `--redact=100`, so report snippets contain masked secrets. The
  `json`/`csv` report formats are deliberately not used: they embed raw
  `Match`/`Secret` values.
- **Severity: blocking.** betterleaks emits no per-finding severity — its
  native vocabulary is per-rule `confidence` (`low`/`medium`/`high`), a
  detection likelihood, not a severity — and its SARIF carries no level, so
  the plugin's declared mapping sends every result to `Error`. A detected
  credential fails the audit — that is the intended gate. There is no
  advisory mode; narrow the scope instead (`ExcludedRules`, `ExcludePaths`,
  or — with `TrustRepositorySuppression` — the repo's own config/ignore
  files). To drop low-confidence candidates at scan time, pass
  `--confidence high` (or `medium`) via `ExtraArguments`.

## What it cannot see

- **Uncommitted worktree changes.** `betterleaks git` scans committed history;
  a secret staged but never committed is out of scope. Audit sandboxes audit
  produced commits, so this is the intended boundary.
- **Non-git directories.** If the working directory is not a git repository
  the scan cannot run and is reported as infrastructure, not a pass.
- **Anything betterleaks's rules don't cover.** Findings are exactly what the
  pinned betterleaks build detects; custom detectors need an operator-supplied
  `--config` via `ExtraArguments` (or `TrustRepositorySuppression` to honor a
  repo config — see below).
- **Binary and archived payloads beyond the tool's unpacking.** In `git` mode,
  binary files produce no text fragments to scan; archives are unpacked up to
  `--max-archive-depth` (default `8`). A secret committed inside an archive
  nested deeper than that is never reported.
- **Paths the stock ruleset skips outright.** The pinned ruleset extends
  betterleaks's built-in config, whose global `prefilter` drops a fragment on
  path alone before any rule runs: image/font/office/binary extensions
  (`.png`, `.woff2`, `.pdf`, `.exe`, …), lockfiles (`go.sum`,
  `package-lock.json`, `yarn.lock`, `poetry.lock`, …), vendored trees
  (`vendor/…`, `node_modules/`, `bower_components/`, Python `dist-info`/
  site-packages trees), and `.git` — upstream's noise bound, meaning even
  text content in a file with one of those names is never scanned. One
  prefilter entry is treated differently: the unanchored `gitleaks\.toml`
  pattern exempts ANY path containing that literal (`docs/gitleaks.toml`,
  `x-gitleaks.toml.bak`, a deleted historical `.gitleaks.toml`) — it names
  the tool's own config surface, so instead of staying a silent blind spot
  the auditor **fails closed** when a `*gitleaks.toml*` path exists anywhere
  in the worktree or in git history (unless `TrustRepositorySuppression` is
  set).
- **Secrets committed under an `ExcludePaths` prefix.** `vendor/`,
  `third_party/`, and `node_modules/` are finding filters: findings there
  are dropped — so a leak committed under an excluded prefix never
  surfaces. (Parts of that space — `node_modules/`, the major
  package-host `vendor/` trees — are also prefilter-skipped at scan time,
  per above.) Re-include by overriding `ExcludePaths`.
- **Runtime provenance of a finding.** `git`-mode SARIF locations point at the
  file path and line; the originating commit is in the finding description,
  not the location.

## Exit codes and failure classification

betterleaks's stock convention is unusable directly — findings exit
`--exit-code` (default `1`) and *every* failure mode also exits `1`
(config load, scan error, non-git target, `FTL` fatals). The plugin overrides
`--exit-code` to `4` (verified against betterleaks `1.8.1`):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | ran clean, no findings | pass |
| `4` | ran, secrets found | findings |
| `1` | could not run (config/scan error, non-git target, incl. betterleaks's own `--timeout`) | infrastructure |
| `126` / `127` | usage error / binary not executable | infrastructure |
| anything else | unknown convention | infrastructure (fails loud, never a pass) |

An error exit also wins over a findings exit upstream, so a partial scan can
never surface as a verdict.

## Version pinning

The auditor is pinned to **betterleaks `1.8.1`** (`ExpectedVersion` in scoped
config). Because a scanner's rule set changes between releases, an unpinned
scanner would change findings under you, so the plugin probes
`betterleaks version` before every run and reports an infrastructure failure on
any other version. Note: betterleaks's SARIF `tool.driver.semanticVersion` is
a hardcoded `v8.0.0` inherited from its gitleaks lineage, which is why the
report cannot carry the check.

The tool requirement is declared **verify-only** — no `AptPackage`: the tool
is not apt-installable (upstream ships brew/dnf containers, Go installs, and
release binaries), and no distro package can carry the version pin. Provision
the pinned upstream release in your sandbox baseline instead, e.g. via
`CodeyBox:MultipassExtraRuncmd` / `CodeyBox:Incus:ExtraRuncmd` or
`ExecutableProvisions`:

```sh
# baseline bake step (adjust arch; verify against the release checksums.txt)
BETTERLEAKS_VERSION=1.8.1
curl -fsSL "https://github.com/betterleaks/betterleaks/releases/download/v${BETTERLEAKS_VERSION}/betterleaks_${BETTERLEAKS_VERSION}_linux_x64.tar.gz" -o /tmp/betterleaks.tar.gz
curl -fsSL "https://github.com/betterleaks/betterleaks/releases/download/v${BETTERLEAKS_VERSION}/checksums.txt" -o /tmp/betterleaks.sha256
(cd /tmp && grep "betterleaks_${BETTERLEAKS_VERSION}_linux_x64.tar.gz" betterleaks.sha256 | sha256sum -c -)
tar -xzf /tmp/betterleaks.tar.gz -C /usr/local/bin betterleaks
betterleaks version   # must print 1.8.1
```

## Enabling

The plugin is **disabled by default** — like every plugin outside the four
grandfathered bundled ones it loads only when named in both gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.betterleaks"],
      "Enabled": ["codeybox.betterleaks"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.betterleaks" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.betterleaks`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `1.8.1` | Pinned betterleaks release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `TrustRepositorySuppression` | `false` | When `true`, the audited repository's own suppression surfaces are honored: `.betterleaks.toml`/`.gitleaks.toml` config, `.betterleaksignore`/`.gitleaksignore` fingerprints, and `betterleaks:allow`/`gitleaks:allow` comments. When `false`, any of those four files at the repo root — or any path matching `*gitleaks.toml*` anywhere in the worktree or git history — fails the run closed as infrastructure. See below — off by default because the audit subject authors those files. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. Everything maps to `error`, so this only matters if the mapping changes. |
| `IncludedRules` / `ExcludedRules` | — | Exact betterleaks rule ids to keep/drop (e.g. `slack-bot-token`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--config <sandbox path>` to pin an operator-controlled ruleset, or `--confidence high` to drop low-confidence candidates at scan time. Flags that name a file the tool loads — `--config`/`-c`, `--baseline-path`/`-b`, `--gitleaks-ignore-path`/`-i` — are canonicalized and **rejected when they resolve inside the audited worktree** (they resolve against the tool's cwd, so an in-tree path would hand gate-shaping content to repo-controlled bytes); point them at absolute paths outside the repo. A repeated flag wins over the built-in default — so take care: the auditor pins `--log-opts` (the stock rev args plus `--text`, the `.gitattributes` countermeasure) — overriding it must keep `--text` or a `-diff`/`binary` attribute silences the scan — and `--enable-rule`, `--disable-rule`, or the file flags above narrow or suppress findings by design. |
| `TimeoutSeconds` | `300` | Per-run bound; also forwarded to betterleaks's own `--timeout`. Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

**Repository-controlled suppression is off by default.** betterleaks honors
four suppression surfaces authored inside the audited repository — a
`.betterleaks.toml` or `.gitleaks.toml` (global `filter`/`prefilter`
expressions and rule edits), a `.betterleaksignore` or `.gitleaksignore`
(fingerprint suppression; betterleaks loads them unconditionally — no flag
disables them), and inline `betterleaks:allow`/`gitleaks:allow` comments.
Because the audited agent can write all four, the plugin neutralizes them
unless the operator opts in:

- the scan exports `BETTERLEAKS_CONFIG_TOML` pinning the built-in ruleset,
  which outranks the repo config files as *config* (betterleaks config
  precedence: `--config` → `BETTERLEAKS_CONFIG`/`GITLEAKS_CONFIG` →
  `BETTERLEAKS_CONFIG_TOML`/`GITLEAKS_CONFIG_TOML` → repo
  `.betterleaks.toml`/`.gitleaks.toml` → built-in default). The two
  precedence-2 **path** env vars are explicitly unset on the scan: a
  baseline-exported `BETTERLEAKS_CONFIG` — or even `GITLEAKS_CONFIG`, which
  betterleaks honors as a fallback spelling — would silently outrank the
  pin with a file path the `ExtraArguments` canonicalization guard never
  sees. The sanctioned operator override is `--config` in `ExtraArguments`
  (canonicalized outside the worktree), which still wins;
- `--ignore-gitleaks-allow` disables both `betterleaks:allow` and
  `gitleaks:allow` comments;
- `--gitleaks-ignore-path` points at an inert path, and pre-scan checks
  **fail closed as infrastructure** when any of the four files exists at
  the worktree root, or when a `*gitleaks.toml*` path exists anywhere in
  the worktree or git history (the stock ruleset's prefilter exempts
  matching paths from the scan in every commit — a committed-then-deleted
  copy would still hide a secret committed inside it). The gate applies
  regardless of an operator `--config` — remove the file(s), or set
  `TrustRepositorySuppression=true` to trust repository-controlled
  suppression surfaces.

A fifth suppression channel — a committed `.gitattributes` marking a
secret-bearing path `-diff`/`binary` — is neutralized at the scan layer:
the auditor pins `--log-opts` to the stock rev args plus `--text`, so
`git log -p` emits patch content for attributed files instead of "Binary
files differ".

Set `TrustRepositorySuppression: true` under
`CodeyBox:Plugins:codeybox.betterleaks` when the audited repositories
legitimately carry detector configs or ignore fingerprints. Two caveats to
understand in that mode: the `*gitleaks.toml*` family gate is skipped, so
those paths return to being silently exempted; and betterleaks exempts its
*loaded* config path (`Config.Path`) from the scan in every commit — that
path stays empty while the config comes from the pinned inline
`BETTERLEAKS_CONFIG_TOML`, but under `TrustRepositorySuppression` a loaded
repo `.betterleaks.toml`/`.gitleaks.toml` IS self-exempted: a secret
committed inside that file is then never reported (moot in that mode — the
repo can already discard findings via `filter`, but know that the config
file's own contents are never scanned).

The alternative to trusting repo files is operator-controlled config: pin a
ruleset outside the repo via `ExtraArguments` (`--config
/abs/path/in/sandbox.toml`) and manage suppression through
`ExcludedRules`/`ExcludePaths`.

## Default scope

Vendored and dependency trees (`vendor/`, `third_party/`, `node_modules/`)
are excluded by default: secrets reported there belong to upstream packages,
not the change under audit, and the noise would teach operators to ignore the
auditor. The exclusion is a finding filter — betterleaks still scans those paths
(scan-time exclusion belongs to an operator-pinned `--config`). Re-include them
by overriding `ExcludePaths`.
