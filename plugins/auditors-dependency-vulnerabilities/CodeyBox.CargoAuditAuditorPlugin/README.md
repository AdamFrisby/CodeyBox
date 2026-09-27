# CodeyBox: cargo-audit Rust Dependency Vulnerabilities Auditor

Auditor plugin wrapping [cargo-audit](https://github.com/rustsec/rustsec/tree/main/cargo-audit):
it parses the audited repository's `Cargo.lock` and matches the resolved
crate versions against the RustSec advisory database, reporting every
matched vulnerability advisory and dependency warning as an audit finding.

## What it reports

- One finding per `vulnerabilities.list` entry (a matched RustSec
  vulnerability advisory) and per `warnings` entry (yanked packages and the
  informational-advisory kinds `unmaintained`, `unsound`, `notice`).
- cargo-audit's SARIF report (`cargo-audit audit --format sarif` on stdout)
  is parsed by the shared `SarifToolOutputParser`. The result's `ruleId`
  becomes the finding's rule — the advisory id (`RUSTSEC-2020-0071`,
  `GHSA-…`, or a CVE alias id) for advisories, or the warning-kind name
  (`yanked`, `unmaintained`, `unsound`, `notice`) for warnings with no
  advisory — so `IncludedRules`/`ExcludedRules` select advisories
  directly, which also serves as the `cargo audit --ignore` equivalent.
- **Severity: mapped, severity-driven gate.** The SARIF `level` maps as:
  `error` → Error (a matched vulnerability advisory — the same verdict
  cargo-audit itself exits non-zero for), `warning` → Warning (yanked and
  informational advisories), `note` → Info, unknown → Warning. Raw tool
  levels never pass through. A vulnerability fails the audit; warnings are
  advisory.
- **Location: the lockfile.** SARIF results carry
  `artifactLocation.uri = "Cargo.lock"` with `startLine = 1` — the only
  location cargo-audit reports for lockfile audits, preserved verbatim.
  (Upstream quirk: the URI is the literal `Cargo.lock` even when `--file`
  points elsewhere, so `ExcludePaths` cannot distinguish multiple
  lockfiles.)

## What it cannot see

- **Severity inside vulnerabilities.** The report scores each advisory via
  the rule's `security-severity` CVSS property, but the result level is
  fixed — every vulnerability is `error`. cargo-audit itself treats any
  matched advisory as failure-worthy regardless of score; this auditor
  does the same.
- **Why a specific version matched.** The report carries advisory metadata
  (title, versions ranges, package) but no dependency-graph chain showing
  which parent pulled the crate in.
- **Unlisted or non-registry dependencies.** cargo-audit matches the
  lockfile's resolved versions; local `path`/`git` dependencies and crates
  absent from the advisory database produce nothing. Code outside the
  lockfile — build scripts, vendored sources — is out of scope by design.
- **`cargo audit fix`/`bin`.** The auditor runs the lockfile check only.

## Exit codes and failure classification

Verified against the cargo-audit 0.22.x source — the convention is **not**
the common "0 = clean, 1 = findings, 2 = could not run":

| Exit | stdout | Meaning | Classification |
|---|---|---|---|
| `0` | SARIF report | ran, no vulnerabilities (warnings may exist) | pass / advisory findings |
| `1` | SARIF report | ran, `vulnerabilities.found` held | findings |
| `1` | no SARIF report | could not run — advisory-db fetch/load failure aborts with `exit(1)` | infrastructure |
| `2` | clap text / none | could not run — usage error, lockfile not found/unloadable, audit error | infrastructure |
| `101` | panic text | cargo-audit panic | infrastructure |
| `126`/`127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

`1` is ambiguous on its own, so the discriminator is the SARIF document:
`print_report` writes it only when the audit completes. A `1` exit without
a parseable report fails closed as infrastructure. A missing
`cargo-audit` binary, a version mismatch, a timeout, and unparseable or
stream-truncated output are likewise infrastructure failures naming the
tool — never a passing audit.

## Version pinning and provisioning

The auditor is pinned to **cargo-audit `0.22.2`** (`ExpectedVersion` in
scoped config): matching behaviour and report shape change between
releases, so an unpinned binary would change findings under you.
`cargo-audit --version` is probed before every run.

One tool requirement is declared, **verify-only** (no `AptPackage` — no
distro package carries a pinned cargo-audit): provision via
`cargo install cargo-audit --locked --version 0.22.2` or the versioned
GitHub release binary through `CodeyBox:MultipassExtraRuncmd` /
`CodeyBox:Incus:ExtraRuncmd` or `ExecutableProvisions`. The `cargo`
toolchain itself is *not* required: the auditor passes an explicit
`--file`, which disables cargo-audit's implicit
`cargo update --workspace` lockfile-generation path, and no other path
invokes `cargo`. The requirement reaches baseline provisioning only while
the plugin is enabled.

## Repository-controlled config

cargo-audit loads `./.cargo/audit.toml` relative to its working directory —
the audited worktree root — before `$CARGO_HOME/audit.toml`. That file can
list advisories to `ignore`, repoint the advisory database
(`[database] url`/`path` — including at an attacker-chosen or empty
database), disable the yanked check, or narrow `target` filters: all
suppression surfaces the audit subject authors. Its presence fails the
audit as a deterministic infrastructure error by default; operators who
trust repo-authored config set `TrustRepositorySuppression: true`. The
`$CARGO_HOME` load site is operator territory. A repository
`.cargo/config.toml` is *not* gated: with an explicit `--file`, cargo is
never invoked, so it cannot influence the scan.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.cargo-audit"],
      "Enabled": ["codeybox.cargo-audit"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.cargo-audit" }
```

**Gate behaviour:** blocking on vulnerabilities. Matched vulnerability
advisories are `Error` findings and fail the audit; dependency warnings
(yanked / unmaintained / unsound / notice) are advisory. This is
cargo-audit's own default verdict (`vulnerabilities.found` → exit 1), kept
as CodeyBox severities rather than re-decided. `MinimumSeverity` drops
findings below a threshold; it never escalates warnings to blocking.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.cargo-audit`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `0.22.2` | Pinned cargo-audit release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `LockfilePath` | `Cargo.lock` | `--file` — the lockfile under audit, relative to the worktree or absolute (e.g. a baseline-provisioned one). Always passed explicitly, which disables cargo-audit's `cargo update --workspace` lockfile generation. `-` (stdin) is rejected as a deterministic configuration failure. |
| `DatabasePath` | — (`~/.cargo/advisory-db`) | `--db` — path to the advisory database git checkout; pin a baseline-provisioned copy for offline deployments. |
| `DatabaseUrl` | — (RustSec advisory-db) | `--url` — advisory database git URL, e.g. an organization mirror. |
| `Offline` | `false` | `--no-fetch` — never touch the network; requires a pre-seeded advisory database and, for yanked checks, a cached crates.io index. |
| `Stale` | `false` | `--stale` — accept an advisory database that has not been updated recently instead of failing the fetch. |
| `NoYanked` | `false` | `--no-yanked` — skip the yanked-crate check and its crates.io-index access entirely. |
| `TargetArch` | — | Comma-separated `--target-arch` filters — advisories scoped to other architectures stop matching. Invalid values fail loudly as a tool run failure. |
| `TargetOs` | — | Comma-separated `--target-os` filters — advisories scoped to other platforms stop matching. |
| `TrustRepositorySuppression` | `false` | Allow a repository-authored `.cargo/audit.toml` instead of failing closed. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids (advisory ids such as `RUSTSEC-2020-0071`, or warning kinds such as `yanked`) to keep/drop. `ExcludedRules` is the `cargo audit --ignore` equivalent. |
| `ExcludePaths` | — | Repo-relative paths dropped from findings. Only matches `Cargo.lock` — the single location the tool reports. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). They land inside the `audit` subcommand — valid entries are audit flags such as `--ignore RUSTSEC-…` / `-D warnings` / `-f path` / `-u url`; the root `-v` flag does not apply there and fails loudly as exit 2. |
| `TimeoutSeconds` | `300` | Per-run bound — covers the advisory-db fetch and matching; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; a stdout overrun that clips the SARIF document fails closed as infrastructure. |

## Network egress

By default cargo-audit fetches the RustSec advisory database (git) and
updates the cached crates.io index for yanked checks, so the auditor
declares `AuditCapabilities.Network` and the egress hosts must be in the
deployment's `AuditToolAllowedHosts` list — a failed fetch aborts the run
(exit 1, no report) and is reported as infrastructure, not a clean audit.
Fully offline deployments pre-seed the advisory database
(`DatabasePath` or the default `$CARGO_HOME` location) plus the index and
set `Offline: true`; a missing index degrades to a skipped yanked check
with a tool warning, never a hidden pass.

## Default scope

`cargo-audit audit --format sarif --file Cargo.lock` at the worktree root:
the whole resolved version set in the committed lockfile, all advisory
and warning kinds. cargo-audit's subject is the lockfile, not a file tree —
vendored or generated source directories cannot produce findings or hide
any, so no `ExcludePaths` defaults apply (an auditor that scoped out
vendored *code* would misdescribe this tool). Narrow the match set with
`LockfilePath`, `TargetArch`, `TargetOs`, or `ExcludedRules`.
