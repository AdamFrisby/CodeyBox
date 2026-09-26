# CodeyBox: cargo-semver-checks Rust API Compatibility Auditor

Auditor plugin wrapping
[cargo-semver-checks](https://github.com/obi1kenobi/cargo-semver-checks): it
builds rustdoc JSON for a baseline and the current crate, runs the upstream
semver lint set over the diff between the two public APIs, and reports each
unresolved breaking change as an audit finding with the lint id and
`file:line` location.

## What it reports

- One finding per offending API item under each triggered lint's
  `--- failure <lint-id>: <name> ---` or `--- warning <lint-id>: <name> ---`
  report section. The title carries the lint id (e.g. `function_missing`,
  `pub_use_removed`) so `ExcludedRules`/`IncludedRules` can select by lint;
  the description carries the tool, rule, section level, location, the
  per-result message (`function my_crate::gone, previously in file
  src/lib.rs:12`), and the lint's explanation text.
- `Location` is the `path:line` the result template supplies — relative to
  the **checked crate's package root** (in a workspace, that is the member
  directory, not the repository root). Messages that name a file without a
  line (`… in the package's Cargo.toml`) report the path only; messages with
  no file report no location.
- **Severity: blocking on deny-level findings.** The declared mapping sends
  `failure` sections (upstream `Deny`-level lints — unresolved breaking
  changes) to `Error` and `warning` sections (upstream `Warn`-level lints)
  to `Warning`. Raw tool levels never reach findings.
- **Version-bump accounting is upstream's.** cargo-semver-checks skips
  lints whose required bump the manifest already satisfies (e.g. a breaking
  change under a fresh major bump). Only *unresolved* breakage is reported —
  a crate that correctly bumped its version produces no findings.

## What it cannot see

- **Non-`pub` API.** The check is over the crate's public API surface;
  `#[doc(hidden)]` handling and private items follow upstream semantics.
- **Behavioural breakage.** Only type-/signature-level semver violations
  the upstream lint set covers — not semantics, MSRV changes, or dependency
  bumps' transitive breakage.
- **`publish = false` workspace members** are skipped by cargo-semver-checks
  unless explicitly selected — internal crates are not published API.
  Select one via `--package <name>` in `ExtraArguments` when it should be
  checked anyway.
- **Repository lint configuration when suppressed.** A
  `[package|workspace.metadata.cargo-semver-checks]` table can downgrade or
  silence lints; see *Repository-controlled suppression* below.
- **Anything but the chosen baseline's public API.** Findings compare the
  current worktree to exactly one baseline — a git revision, a registry
  version, a directory, or a pre-built rustdoc JSON. Changes to APIs added
  *and* removed within the same branch range are invisible (they never
  existed at the baseline).

## Baseline selection — the thing an operator must think about

A semver check is meaningless without a "previous API" to compare against.
Unless a baseline is configured, the auditor resolves the **merge-base of
`HEAD` and `origin/<BaseBranch>`** (falling back to the bare branch name) —
the same `origin/<base>...HEAD` semantics as the pipeline's diff auditors —
and passes it as `--baseline-rev`. This is the right default for auditing a
*change*: API added to the base after the branch point is not misread as
removed.

`cargo-semver-checks`'s own stock default (the latest published registry
version) is what `BaselineVersion`/`--baseline-version` selects explicitly;
for crates published to crates.io that is the gold-standard "is it safe to
release" check. It needs the package to exist on the registry and egress to
crates.io.

Set **exactly one** of these scoped keys to pin the baseline; setting more
than one (or none while `BaseBranch` resolution fails) is a deterministic
infrastructure failure:

| Key | Flag | Use when |
|---|---|---|
| `BaselineRev` | `--baseline-rev` | Any git revision (`origin/main`, a tag, a sha). |
| `BaselineVersion` | `--baseline-version` | Compare against a version from the registry. |
| `BaselineRoot` | `--baseline-root` | A directory containing baseline crate sources. |
| `BaselineRustdoc` | `--baseline-rustdoc` | A pre-generated rustdoc JSON baseline. |

## Exit codes and failure classification

cargo-semver-checks does **not** follow the common "0 = clean, 1 =
findings, 2 = could not run" convention — verified against the v0.50.0
source:

| Exit | Meaning | Classification |
|---|---|---|
| `0` | check completed; no deny-level findings (warn-level findings may exist) | pass (advisory findings still reported) |
| `100` | check completed; deny-level findings exist | findings |
| `100` with **no** `--- failure` section on stdout | report contradicts the exit contract (suppressed verbosity, foreign build, truncation) | infrastructure |
| `101` | could not run: bad manifest, unresolvable baseline, rustdoc-format mismatch, required-witness error | infrastructure |
| `2` | clap usage error (bad arguments) | infrastructure |
| `126` / `127` | cannot execute / not found | infrastructure |
| anything else | unknown convention | infrastructure (fails loud, never a pass) |

A missing `cargo-semver-checks` binary, a version mismatch, a missing or
unresolvable baseline, an absent root `Cargo.toml`, a timeout, and
unparseable output are likewise infrastructure failures naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **cargo-semver-checks `0.50.0`** (`ExpectedVersion`
in scoped config): the lint set, exit convention, and report shape change
between releases, so an unpinned binary changes findings under you.
`cargo-semver-checks --version` is probed before every run; any other
version is an infrastructure failure.

Both requirements are declared **verify-only** — no `AptPackage`:

- `cargo-semver-checks` — no distro package carries it. Install the pinned
  release into the baseline via `CodeyBox:MultipassExtraRuncmd` /
  `CodeyBox:Incus:ExtraRuncmd` or `ExecutableProvisions`:

  ```sh
  cargo install --locked cargo-semver-checks@0.50.0   # or cargo-binstall for the prebuilt
  cargo-semver-checks --version                        # must print 0.50.0
  ```

- `cargo` — a Rust toolchain (cargo + rustdoc) whose rustdoc JSON format the
  pinned cargo-semver-checks supports. Each release supports the
  then-current stable and beta toolchains; check the release notes before
  baking a rustup toolchain. A `rust-toolchain.toml` in the audited
  repository also steers the toolchain cargo-semver-checks invokes — an
  incompatible pin fails the run loudly, it cannot silently skip lints.

## Network egress

The check compiles rustdoc JSON for baseline and current crates — two
dependency builds of repository code (build scripts and proc macros run in
the sandbox; the auditor holds no credentials). It declares the `Network`
capability, so the audit-tool egress allowlist must cover `index.crates.io`
and `static.crates.io`, or dependencies must be vendored/pre-fetched into
the baseline with `--offline` or `--frozen` in `ExtraArguments`.
Registry-based baselines (`BaselineVersion`, or the upstream default when
nothing is configured) additionally require the package to be published.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.cargo-semver-checks"],
      "Enabled": ["codeybox.cargo-semver-checks"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.cargo-semver-checks" }
```

Only then do the declared `cargo-semver-checks`/`cargo` tool requirements
reach baseline provisioning (presence-verified at bake time; nothing is
apt-installed because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.cargo-semver-checks`, resolved per
run (hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `0.50.0` | Pinned cargo-semver-checks release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `BaselineRev` / `BaselineVersion` / `BaselineRoot` / `BaselineRustdoc` | merge-base of `origin/<BaseBranch>` | Exactly one baseline source (see above). With none set, the merge-base of the work item's base branch is used. |
| `ManifestPath` | — | `--manifest-path` when the crate under audit is not at the repository root. When set, the root `Cargo.toml` presence check is skipped. |
| `TrustRepositorySuppression` | `false` | When `true`, `[package|workspace.metadata.cargo-semver-checks]` lint tables in the audited repository are honored. When `false`, any `Cargo.toml` carrying `metadata.cargo-semver-checks` fails the run closed as deterministic infrastructure. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity — e.g. `error` keeps only deny-level findings. |
| `IncludedRules` / `ExcludedRules` | — | Exact lint ids to keep/drop (e.g. `function_missing`). |
| `ExcludePaths` | — | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Findings are relative to the checked package root, so prefix exclusions should match accordingly. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--package`, `--exclude`, `--all-features`, `--release-type`, `--offline`. Take care: `--release-type` overrides the detected version bump and can satisfy deny-level lints outright; a second baseline flag conflicts with the configured one and fails the run; verbosity flags (`-q`) suppress the report and fail closed as infrastructure. |
| `TimeoutSeconds` | `900` | Per-run bound (the check compiles dependencies twice — baseline and current); exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

cargo-semver-checks reads **no standalone config file** — the only
repository surface it honors is the `[package|workspace.metadata.
cargo-semver-checks]` lint table gated above.

## Default scope

`check-release` at the repository root: cargo's own manifest discovery
limits the check to the root package or the publishable workspace members —
no vendored or generated code is ever reported, so the auditor ships no
`ExcludePaths` default. A repository without a root `Cargo.toml` (and no
`ManifestPath`) fails closed deterministically: enabling this auditor on a
non-Rust project is a loud misconfiguration, not a silent skip.
