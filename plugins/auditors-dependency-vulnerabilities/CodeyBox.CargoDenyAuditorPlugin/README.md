# CodeyBox: cargo-deny Rust Dependency Policy Auditor

Auditor plugin wrapping [cargo-deny](https://github.com/EmbarkStudios/cargo-deny):
it builds the dependency graph of the audited repository's Rust workspace
(`cargo metadata`) and evaluates it against the dependency policy — advisories
(known-vulnerability / unmaintained / yanked crates), bans (denied or
duplicated crates), licenses, and sources (registry/git allowlists) — reporting
every diagnostic as an audit finding.

## What it reports

- One finding per cargo-deny diagnostic. cargo-deny emits
  `{"type":"diagnostic","fields":{…}}` records (newline-delimited JSON on
  **stderr** — in `--format json` mode all of its structured output goes to
  stderr, not stdout).
- The diagnostic's lint code becomes the rule id — `cargo-deny/<code>`,
  e.g. `cargo-deny/vulnerability`, `cargo-deny/unlicensed`,
  `cargo-deny/duplicate`, `cargo-deny/source-not-allowed` — so
  `IncludedRules`/`ExcludedRules` can select by lint. Diagnostics with no
  code get `cargo-deny/diagnostic`. Advisory findings embed the advisory id
  (e.g. `RUSTSEC-2024-0001`) and title in the message.
- **Severity: mapped, severity-driven gate.** cargo-deny's codespan severity
  vocabulary maps as: `error` → Error, `bug` → Error, `warning` → Warning,
  `note`/`help` → Info, unknown → Warning. Denied lints fail the audit;
  warn-level lints are advisory. Whether a lint is denied is decided by the
  `deny.toml` in force (the repo's by default, or `ConfigPath`).
- **Location: absent, by upstream design.** cargo-deny's JSON labels carry
  `line`, `column`, and `span` (the matched source text) but never the file
  name — the internal file table is not serialized — so findings carry no
  `Location`; label detail is folded into the message text instead.

## What it cannot see

- **File names for diagnostics.** See above — an upstream limitation of the
  JSON format. (The SARIF format resolves file names but skips synthesized
  `Cargo.lock` spans anyway, which is where most crate diagnostics point.)
- **Which check produced a diagnostic.** The record carries no check field;
  the lint code (advisories/bans/licenses/sources families) is the only
  signal.
- **Anything outside the manifest's dependency graph.** The subject is the
  resolved crate graph rooted at `ManifestPath` (default: worktree
  `Cargo.toml`). Non-Rust code, lock-free resolutions never recorded, and
  code dependencies outside the workspace are out of scope.
- **Allowed/suppressed lints.** `--log-level warn` emits only warnings and
  errors; `note`/`help` (allowed lints) never reach the report.

## Exit codes and failure classification

cargo-deny does **not** follow the common "0 = clean, 1 = findings, 2 = could
not run" convention — verified against the 0.20.x source: `check` exits with a
**bitset of the checks that produced errors** (`advisories` 0x1, `bans` 0x2,
`licenses` 0x4, `sources` 0x8), while every run failure — bad flag, unparseable
`deny.toml`, failed `cargo metadata`, unreachable advisory database — also
exits `1` via `anyhow`. The discriminator is the `{"type":"summary",…}` record
written to stderr only when the run completes.

| Exit | stderr | Meaning | Classification |
|---|---|---|---|
| `0` | NDJSON + summary | ran clean (warnings may exist) | pass / advisory findings |
| `1`–`15` | NDJSON + summary | ran, denied lints fired | findings |
| `1`–`15` | no summary | could not run (or truncated output) | infrastructure |
| `2` | clap usage text | bad arguments — inside the bitset range, but no summary record ⇒ infrastructure | infrastructure |
| `101` | panic text | cargo-deny panic | infrastructure |
| `126`/`127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

A missing `cargo-deny` (or `cargo`) binary, a version mismatch, a timeout, and
unparseable output are likewise infrastructure failures naming the tool —
never a passing audit.

## Version pinning and provisioning

The auditor is pinned to **cargo-deny `0.20.2`** (`ExpectedVersion` in scoped
config): the lint set and diagnostic vocabulary change between releases, so an
unpinned binary would change findings under you. `cargo-deny --version` is
probed before every run.

Two tool requirements are declared, both **verify-only** (no `AptPackage` —
no distro package carries cargo-deny):

- `cargo-deny` — provision via `cargo install cargo-deny --locked --version
  0.20.2` or the versioned GitHub release binary, through
  `CodeyBox:MultipassExtraRuncmd` / `CodeyBox:Incus:ExtraRuncmd` or
  `ExecutableProvisions`.
- `cargo` — cargo-deny invokes `cargo metadata` to build the crate graph.
  Not needed when `MetadataPath` supplies pre-generated metadata.

Both reach baseline provisioning only while the plugin is enabled.

## Policy configuration and repository-controlled files

cargo-deny resolves policy from `deny.toml` (or `.deny.toml`,
`.cargo/deny.toml`), discovered beside the manifest and
walking upward. By default the audited repository's own policy is the
contract being verified — weakening it is visible in the diff. An operator
who needs a fixed organizational policy sets `ConfigPath` to a config
provisioned outside the repository (note the upward walk can also pick up a
`deny.toml` above the worktree when none exists inside it — pin `ConfigPath`
to eliminate that). A repository policy that overrides the advisory-database
source (`db-urls`/`db-path`/`git-fetch-with-cli` under `[advisories]`) fails
closed as infrastructure unless `TrustRepositorySuppression: true` is set or
`ConfigPath` pins an operator-owned policy, because it could point the scan
at an empty database and suppress every advisory finding. Likewise a
repository `.cargo/config.toml` (or `.cargo/config`) fails closed: it feeds
the `cargo metadata` graph resolution cargo-deny performs.

`deny.exceptions.toml` (and `.deny.exceptions.toml`,
`.cargo/deny.exceptions.toml`) are cargo-deny's local-override files: they add
exceptions on top of the policy. They are a suppression surface the audit
subject could use to hide a violation, so their presence at the worktree root
(or beside a configured `ManifestPath`) fails the audit as a deterministic
infrastructure error by default. Operators who deliberately trust
repo-authored exceptions set `TrustRepositorySuppression: true`.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.cargo-deny"],
      "Enabled": ["codeybox.cargo-deny"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.cargo-deny" }
```

Only then do the declared `cargo-deny`/`cargo` tool requirements reach
baseline provisioning (presence-verified at bake time; nothing is
apt-installed because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.cargo-deny`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `0.20.2` | Pinned cargo-deny release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigPath` | — (repo's `deny.toml` chain) | `--config` — an operator-owned policy file provisioned in the baseline, replacing repository policy resolution. |
| `ManifestPath` | — (`<worktree>/Cargo.toml`) | `--manifest-path` — the manifest the crate graph is rooted at. Use for repositories whose Rust workspace lives in a subdirectory. |
| `MetadataPath` | — (cargo-deny runs `cargo metadata`) | `--metadata-path` — a pre-generated `cargo metadata` JSON file; removes the `cargo` invocation entirely. |
| `Checks` | all | Comma-separated subset of `advisories`, `bans`, `licenses`, `sources` (or `all`) — cargo-deny's positional check selection. Invalid entries are a deterministic configuration failure. |
| `Offline` | `false` | `--offline` — no network access at all. Requires pre-seeded advisory databases and cargo cache in the baseline. |
| `Locked` | `false` | `--locked` — fail if `Cargo.lock` is missing or would change. |
| `Targets` | — | Comma-separated platform triples for repeatable `--target` — crates gated behind other platforms are dropped from the graph. |
| `InclusionGraphs` | `false` | When false the auditor passes `--hide-inclusion-graph`: the per-diagnostic inverse dependency trees are large. Set true to include them in findings output. |
| `TrustRepositorySuppression` | `false` | Allow repository-authored `deny.exceptions.toml` files instead of failing closed. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids (`cargo-deny/<lint code>`) to keep/drop. |
| `ExcludePaths` | — | Repo-relative paths dropped from findings. **Currently inert**: cargo-deny's JSON diagnostics carry no file paths, so there is nothing to match. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). They land **after** `check`, so only check-subcommand arguments are valid (`-A`/`-W`/`-D` lint overrides, `--feature-depth`, `--show-stats`, extra `WHICH` names); root-level flags belong to the scoped keys above and fail loudly here as exit 2. |
| `TimeoutSeconds` | `300` | Per-run bound — covers `cargo metadata` and the advisory fetch; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation (a stderr overrun that clips the summary record fails closed as infrastructure). |

## Network egress

The auditor declares `AuditCapabilities.Network`: the advisories check fetches
the configured advisory databases (the RustSec `advisory-db` by default) and
`cargo metadata` may reach the registry index. The egress hosts must be in the
deployment's `AuditToolAllowedHosts` list, or the run fails loudly as
infrastructure (the advisory fetch is a hard error — there is no silent
degradation). Fully offline deployments pre-seed the advisory databases under
`$CARGO_HOME` (or set `advisories.db-path`) plus the cargo registry cache, and
set `Offline: true`.

## Default scope

`cargo deny check` on the workspace manifest at the worktree root, all four
checks, whole dependency graph. cargo-deny's subject is the resolved crate
graph — vendored trees (`vendor/`, `third_party/`) are covered by the
`sources` check by design rather than excluded, since a vendored or
git-sourced dependency is exactly the policy question the check answers.
No `ExcludePaths` defaults apply (findings carry no paths). Narrow the graph
with `ManifestPath`, `Targets`, or `--exclude` configuration instead.
