# CodeyBox: Clippy Rust Linter

Auditor plugin wrapping [Clippy](https://doc.rust-lang.org/clippy/) (Rust
analysis): it runs `cargo-clippy clippy --message-format=json --all-targets`
in the audited worktree and reports each diagnostic as an audit finding with
the lint code (e.g. `clippy::needless_return`, `E0308`) and `file:line`
location. Rust analysis only.

## What it reports

- One finding per clippy/rustc diagnostic with at least one span. The title
  carries the rule code and the first line of the message (e.g.
  "clippy::unnecessary_literal_unwrap: used `unwrap()` on `Some` value");
  the description carries the tool, rule, tool-reported level, location, and
  the full message. `Location` is `path:startLine`.
- **Compiler errors** (`E0308` mismatched types, etc.) are findings too —
  level `error`, so they fail the audit rather than vanishing as tool noise.
- **Gate behaviour: severity-driven — not blocking by default.** Clippy lints
  default to `warning`, which maps to `Warning` (advisory — reported, but the
  audit passes). `error` maps to `Error` and fails the audit. To make every
  lint blocking, deny warnings in the crate (`[lints.clippy]`) or pass
  `-- --deny warnings` in `ExtraArguments`. `MinimumSeverity` only drops
  findings, it never raises them.
- Duplicate diagnostics emitted for several targets (a binary and its test
  harness both compiling `src/main.rs` under `--all-targets`) are reported
  once.

## What it cannot see

- **Non-Rust files.** Clippy checks the crate graph rooted at `Cargo.toml` —
  everything else produces no findings. A repository with no Rust targets is
  a clean pass (artifacts plus `build-finished`, no diagnostics), not an
  error.
- **Suppressed lints.** Clippy honors `#[allow(...)]` attributes,
  `clippy.toml`, and `Cargo.toml [lints]` tables authored inside the audited
  tree — and the audit subject writes that repository. There is no flag that
  makes `#[allow]` inert, so suppressions are honored and documented as a
  limitation. Expect fewer findings than a local run with stricter levels
  when the repo weakens its own lints; lint-config changes are visible in the
  audited diff.
- **Files clippy never compiles.** `#[cfg]`-gated modules for other targets,
  build-script-generated code, and anything outside the crate graph produce
  no findings.
- **More than `MaxFindings` diagnostics.** Findings beyond `MaxFindings`
  (default 1000) are dropped and the truncation is reported in the raw
  output.

## Exit codes and failure classification

`cargo-clippy`'s convention (verified against clippy 0.1.99 / cargo 1.99.0 —
**not** assumed from the common "0 clean / 1 findings / 2 error" table):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Ran (clean or with warnings — warnings do not fail the build) | Verdict (pass when no `Error` findings; `Warning` findings are advisory) |
| `101` with diagnostics on stdout | Ran with error-level diagnostics (denied lints, type errors) | Verdict (`Passed = false`) |
| `101` with empty stdout | Could not run (missing `Cargo.toml`, toolchain failure) | Infrastructure (`AuditUnavailableException`) |
| `1` | Could not run (cargo usage error, e.g. unknown flag — no JSON) | Infrastructure |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `cargo-clippy` is always an infrastructure failure naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **clippy `0.1.99`** (`ExpectedVersion` in scoped
config — the `clippy 0.1.x` banner from `cargo-clippy --version`). A linter's
rules change between releases, so an unpinned tool would change findings under
you: the auditor probes `cargo-clippy --version` before every run and reports
an infrastructure failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`: clippy
ships with the Rust toolchain, and no distro package carries a version pin.
Provision the pinned release in your sandbox baseline **only when this plugin
is enabled**:

```sh
# baseline bake step (example: stable toolchain carrying clippy 0.1.99)
rustup toolchain install stable --component clippy
cargo-clippy --version   # must print clippy 0.1.99
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates, and baseline provisioning verifies `cargo-clippy` only in that state:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.clippy"],
      "Enabled": ["codeybox.clippy"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.clippy" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.clippy`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `0.1.99` | Pinned clippy release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ManifestPath` | `null` | Path passed to `--manifest-path` — the `Cargo.toml` the scan is rooted at. Unset scans the worktree root manifest. |
| `Offline` | `false` | When `true` the scan passes `--offline` (no network) and the auditor declares no network capability. Pre-seed the cargo cache into the baseline for offline runs. |
| `AllTargets` | `true` | When `true` (default) the scan passes `--all-targets` (lib, bins, tests, benches, examples). Set to `false` for lib/bins only. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). Only drops — never raises advisory warnings to blocking. |
| `IncludedRules` / `ExcludedRules` | — | Exact lint codes to keep/drop (e.g. `clippy::needless_return`, `E0308`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `target/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--lib`, `--tests`, `--features`, `--locked`, or `-- --deny warnings` (clippy-driver flags after the `--` separator). A repeated `--message-format` replaces the JSON report the parser expects and breaks the run into infrastructure failure. The built-in `--all-targets`, `--offline`, and `--manifest-path` defer to the operator's own setting. |
| `TimeoutSeconds` | `300` | Per-run bound. Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Default scope

`cargo-clippy clippy --message-format=json --all-targets` in the worktree
root (or at `ManifestPath`): the whole crate graph including tests, benches,
and examples. The finding-level `ExcludePaths` backstop drops vendored
(`vendor/`, `third_party/`) and generated (`target/`) prefixes: problems
there belong to upstream packages or build output, not the change under
audit — reporting them produces noise that trains operators to ignore the
auditor. Re-include a path by overriding `ExcludePaths`.

## Blocking behaviour

This auditor is **not blocking by default**: `Warning` findings are advisory
and the audit passes with them. Only `Error` findings (denied lints,
compiler errors) fail the audit. This is stated here and in the class
documentation so operators do not assume a lint gate that is not there.
