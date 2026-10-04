# CodeyBox: TruffleHog Secrets Auditor

Auditor plugin wrapping [trufflehog](https://github.com/trufflesecurity/trufflehog):
it scans the audited repository's committed source **and full git history**
(`trufflehog git`) for leaked credentials, **live-verifies** each candidate
against its provider, and reports each hit as an audit finding with the
detector id and `file:line` location.

## What it reports

- One finding per trufflehog result. The title carries the detector id
  (e.g. `PrivateKey`, `AWS`, `Github`); the description carries the tool,
  rule, tool-level, and location. `Location` is `path:startLine`.
- **Verified vs unverified.** TruffleHog checks each candidate secret
  against its provider. A **verified** (live) credential maps to `Error`
  and fails the audit; an **unverified** detection (pattern matched but
  liveness unconfirmed, or verification errored — e.g. no network) maps to
  `Warning`: reported, advisory, does not fail the audit. Triage warnings
  like any other scanner hit — they are leads, not verdicts.
- **Secrets are never written into findings or raw output** — the scan
  reports through `--sarif`, which carries only the detector id, file/line,
  verified-ness, and a SHA-256 fingerprint. (The `--json` report would embed
  each finding's `Raw` secret bytes; the plugin rejects `--json` for exactly
  this reason — see below.)

## What it cannot see

- **Uncommitted worktree changes.** The `git` source clones the repository
  to a temporary directory and scans committed content; a secret in the
  working tree that was never committed is out of scope. Audit sandboxes
  audit produced commits, so this is the intended boundary.
- **Anything trufflehog's detectors don't cover.** Findings are exactly what
  the pinned trufflehog build detects and verifies; custom detectors need an
  operator-supplied `--config` via `ExtraArguments` (canonicalized outside
  the worktree — see below).
- **Secrets committed under an `ExcludePaths` prefix.** `vendor/`,
  `third_party/`, and `node_modules/` are finding filters: findings there
  are dropped — so a leak committed under an excluded prefix never
  surfaces. Re-include by overriding `ExcludePaths`.
- **Runtime provenance of a finding.** SARIF locations point at the file
  path and line; the originating commit is not carried by the tool's SARIF
  output.

## Live verification and network egress

Unlike pure pattern scanners, this auditor verifies: each candidate secret
is checked against its provider's API, so the scan performs **outbound
requests carrying candidate credential material**. The sandbox running the
audit must allow that egress, or every result degrades to unverified
(still reported as warnings — never silently dropped). Do not enable this
auditor in an environment where credential-bearing egress is unacceptable;
the `detect-secrets` auditor is the offline alternative.

## Exit codes and failure classification

Without `--fail`, trufflehog exits `0` with or without findings, so the
plugin passes `--fail` to disjoint the verdicts (verified against
trufflehog 3.97.9):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | ran clean, no findings | pass |
| `183` | ran, results found (SARIF on stdout is the verdict) | findings |
| `1` | could not run (bad flags, unreadable repo, non-git directory, scan error) | infrastructure |
| `126` / `127` | usage error / binary not executable | infrastructure |
| anything else | unknown convention | infrastructure (fails loud, never a pass) |

## Version pinning

The auditor is pinned to **trufflehog `3.97.9`** (`ExpectedVersion` in
scoped config). Because a scanner's detectors change between releases, an
unpinned scanner would change findings under you, so the plugin probes
`trufflehog --version` before every run and reports an infrastructure
failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`: the
tool is not apt-installable with a version pin. Provision the pinned
upstream release in your sandbox baseline instead, e.g. via
`CodeyBox:MultipassExtraRuncmd` / `CodeyBox:Incus:ExtraRuncmd` or
`ExecutableProvisions`:

```sh
# baseline bake step (adjust arch; verify against the release checksums.txt)
TRUFFLEHOG_VERSION=3.97.9
curl -fsSL "https://github.com/trufflesecurity/trufflehog/releases/download/v${TRUFFLEHOG_VERSION}/trufflehog_${TRUFFLEHOG_VERSION}_linux_amd64.tar.gz" -o /tmp/trufflehog.tar.gz
curl -fsSL "https://github.com/trufflesecurity/trufflehog/releases/download/v${TRUFFLEHOG_VERSION}/trufflehog_${TRUFFLEHOG_VERSION}_checksums.txt" -o /tmp/trufflehog.sha256
(cd /tmp && grep "trufflehog_${TRUFFLEHOG_VERSION}_linux_amd64.tar.gz" trufflehog.sha256 | sha256sum -c -)
tar -xzf /tmp/trufflehog.tar.gz -C /usr/local/bin trufflehog
trufflehog --version   # must print 3.97.9
```

## Enabling

The plugin is **disabled by default** — like every plugin outside the four
grandfathered bundled ones it loads only when named in both gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.trufflehog"],
      "Enabled": ["codeybox.trufflehog"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.trufflehog" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.trufflehog`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `3.97.9` | Pinned trufflehog release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `TrustRepositorySuppression` | `false` | When `true`, inline `trufflehog:ignore` comments in the audited repository are honored. When `false` (default), the scan passes `--no-ignore-tag` so the audit subject cannot annotate a leak silent. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. Set to `error` to report only live credentials. |
| `IncludedRules` / `ExcludedRules` | — | Exact detector ids to keep/drop (e.g. `PrivateKey`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--results=verified` (live credentials only) or `--config /abs/path/in/sandbox.toml` for operator-controlled detectors. Flags that name a file the tool loads — `--config`, `--include-paths`/`-i`, `--exclude-paths`/`-x` — are canonicalized and **rejected when they resolve inside the audited worktree**; point them at absolute paths outside the repo. Reserved flags that would break the report contract or silently downgrade the gate — `--json`, `--json-legacy`, `--github-actions`, `--no-verification` — are rejected outright (tune noise with `MinimumSeverity` / rule selection instead). History-narrowing flags (`--since-commit`, `--branch`, `--max-depth`) are allowed but shrink coverage by design — overriding them is an operator decision, recorded in the run's argv. |
| `TimeoutSeconds` | `300` | Per-run bound enforced around the scan exec; exceeding it is infrastructure, not a pass. (TruffleHog offers no global in-tool timeout — only per-detector/per-archive bounds — so the outer bound is the runtime cap.) |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. Note the SARIF report is buffered in tool memory for the full scan (the format requires one document), so very large result counts raise tool memory proportionally — the caps bound what the audit captures, not what the tool buffers. |

## Default scope

Vendored and dependency trees (`vendor/`, `third_party/`, `node_modules/`)
are excluded by default: secrets reported there belong to upstream packages,
not the change under audit, and the noise would teach operators to ignore the
auditor. The exclusion is a finding filter — trufflehog still scans those
paths (scan-time exclusion belongs to an operator `--exclude-globs`).
Re-include them by overriding `ExcludePaths`.
