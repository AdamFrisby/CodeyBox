# CodeyBox: oasdiff OpenAPI Breaking Changes Auditor

Auditor plugin wrapping [oasdiff](https://github.com/oasdiff/oasdiff)'s
`breaking-files` command: each OpenAPI spec under audit is compared against
its own version in a git ref (the merge-base of the work item's base branch
by default), and every reported breaking change becomes an audit finding
with the check id and the spec file/line location.

## What it reports

- One finding per change in the per-spec JSON report — the title carries
  the oasdiff check id (e.g. `api-path-removed-without-deprecation`,
  `request-property-removed`), so `ExcludedRules`/`IncludedRules` can select
  by check; the message carries the operation, API path, the change text,
  and the check's comment when present.
- `Location` is `file:line` from the change's `revisionSource` (where the
  new spec carries the change), falling back to `baseSource`, then to the
  spec path itself. A source may point into a relative-`$ref`'d sibling
  file — that is where the change landed.
- **Severity: blocking on ERR-level findings.** The declared mapping sends
  oasdiff `ERR` (definite breaking changes) to `Error` — this auditor is
  **blocking by default** — and `WARN` (potential breaking changes, the
  lower bound `breaking-files` reports) to `Warning`. `INFO` is mapped to
  `Info` for completeness even though `breaking-files` never emits it.
  Unknown or missing levels default to `Warning`. Raw tool severities never
  reach findings.

## What it cannot see

- **Newly added specs.** A spec absent from the base ref is skipped by
  oasdiff — a new API has no prior version to break. (No prior contract
  exists to violate; review new specs by other means.)
- **Deleted specs.** A spec removed from the worktree has no revision side
  to compare; removing an API wholesale is not representable as a
  breaking-change finding.
- **INFO-level / non-breaking changes.** `oasdiff breaking` reports only
  `ERR` and `WARN` checks — by design. Use `oasdiff changelog` downstream
  for the full list.
- **Behavioural breakage.** The check is over the declared OpenAPI
  contract, not runtime behaviour — a server that silently accepts
  contract-invalid requests still gets flagged, and a spec that lies about
  the service cannot be caught here.
- **Specs outside the resolved scope.** Discovery matches the conventional
  `*openapi*`/`*swagger*` basenames; a spec named `api.yaml` needs
  `SpecPaths`. Specs whose filenames cannot be a `breaking-files` argument
  (containing `:` or starting with `-`) are out of scope.
- **`$ref`s that leave the spec file.** `--allow-external-refs=false` is
  passed by default, and on the worktree (revision) side oasdiff refuses
  *every* non-fragment `$ref` — not just http(s) targets but also in-repo
  relative file refs like `./schemas.yaml#/Pet` — failing the run closed
  (exit 123, infrastructure). Only the baseline side resolves in-repo file
  refs: oasdiff loads `<base>:<path>` through `git show`, so a `$ref` into a
  sibling file at the base ref resolves while the same `$ref` in the
  worktree copy fails. A multi-file (split-`$ref`) OpenAPI spec therefore
  needs the dedicated `AllowExternalRefs` scoped key (see below), which also
  declares the `Network` audit capability so the run is scheduled into a
  network-capable sandbox profile for the fetches it enables — passing the
  flag through `ExtraArguments` is a deterministic failure for exactly that
  reason.
- **Symlinked specs.** A spec path that is a symlink — or sits under a
  symlinked directory — is never handed to oasdiff: a committed link could
  redirect the tool's read outside the audited tree. Such candidates are
  dropped from discovery, and a configured `SpecPaths` entry resolving to
  one is a deterministic failure.

## Baseline selection

Unless configured, the auditor resolves the **merge-base of `HEAD` and
`origin/<BaseBranch>`** (falling back to the bare branch name) — the same
three-dot semantics as the pipeline's diff auditors — and passes it as
`--base`. A spec in the worktree but not in that ref is newly added and
skipped by the tool.

Set the baseline explicitly with exactly one of:

| Channel | Use when |
|---|---|
| `BaseRef` scoped key | Any git ref `git show <ref>:<path>` resolves — branch, tag, or SHA. |
| `--base <ref>` in `ExtraArguments` | Same effect through the shared extra-argv channel. |

Setting both is a deterministic configuration failure. An unresolvable
default (no base branch on the work item, missing ref, no common ancestor)
fails closed as infrastructure — never a pass.

## Exit codes and failure classification

oasdiff does **not** follow the common "0 = clean, 1 = findings, 2 = could
not run" convention — verified against the v1.32.x source:

| Exit | Meaning | Classification |
|---|---|---|
| `0` | ran; no spec carried changes at or above `--fail-on` (the JSON report may still list lower-priority changes, or `[]` per spec) | pass/advisory findings |
| `1` | ran; `--fail-on WARN` tripped — at least one spec has breaking changes | findings |
| `1` with an empty report | exit contradicts the report contract | infrastructure |
| `100` | general execution error | infrastructure |
| `101` | invalid flags | infrastructure |
| `102`/`103`/`104` | spec load / glob / diff failure (e.g. a matched file that is not OpenAPI) | infrastructure |
| `105`/`106`/`107` | print / severity-levels / config-file failure | infrastructure |
| `110`/`111`/`114` | unsupported format / template / color errors | infrastructure |
| `121`/`122`/`123` | ignore-file / flatten / disallowed-external-`$ref` errors | infrastructure |
| `126`/`127` | cannot execute / not found | infrastructure |
| anything else | unknown convention | infrastructure (fails loud, never a pass) |

A missing `oasdiff` binary, a version mismatch, a missing/unresolvable
baseline, zero resolved specs, a timeout, and unparseable output are
likewise infrastructure failures naming the tool — never a passing audit.

## Version pinning

The auditor is pinned to **oasdiff `1.32.1`** (`ExpectedVersion` in scoped
config): the check catalog, JSON report shape, and exit convention change
between releases, so an unpinned binary changes findings under you.
`oasdiff --version` is probed before every run; any other version is an
infrastructure failure.

Requirements are declared **verify-only** — no `AptPackage` — and reach
baseline provisioning only while the plugin is enabled:

- `oasdiff` — no distro package carries it. Install the pinned release into
  the baseline via `CodeyBox:MultipassExtraRuncmd` /
  `CodeyBox:Incus:ExtraRuncmd` or `ExecutableProvisions`, verifying the
  published release checksum before unpacking:

  ```sh
  # baseline bake step (adjust arch; verify against the release checksums.txt)
  OASDIFF_VERSION=1.32.1
  curl -fsSL "https://github.com/oasdiff/oasdiff/releases/download/v${OASDIFF_VERSION}/oasdiff_${OASDIFF_VERSION}_linux_amd64.tar.gz" -o /tmp/oasdiff.tar.gz
  curl -fsSL "https://github.com/oasdiff/oasdiff/releases/download/v${OASDIFF_VERSION}/checksums.txt" -o /tmp/oasdiff-checksums.txt
  (cd /tmp && grep "oasdiff_${OASDIFF_VERSION}_linux_amd64.tar.gz" oasdiff-checksums.txt | sha256sum -c -)
  tar -xzf /tmp/oasdiff.tar.gz -C /usr/local/bin oasdiff
  oasdiff --version   # must print 1.32.1
  ```

  The alternative `go install github.com/oasdiff/oasdiff@v1.32.1` is
  integrity-verified by the Go module checksum database if a Go toolchain
  is already in the baseline.

- `git` — ships in the stock sandbox baseline; declared because
  `breaking-files` reads `<base>:<path>` revisions through `git show`, and
  the default baseline resolution runs `git rev-parse`/`git merge-base`/
  `git ls-files` inside the audited clone.

## Repository-controlled suppression

oasdiff auto-loads `<cwd>/.oasdiff.{json,toml,yaml,yml,properties,props,
prop,hcl,tfvars,dotenv,env,ini,json5}` and the legacy `oasdiff.<ext>`
spellings — the auditor runs oasdiff at the worktree root, so a committed
`.oasdiff.yaml` could set `err-ignore`, `warn-ignore`, `severity-levels`,
`match-path`/`unmatch-path`, or `deprecation-days-*` and silently downgrade
or erase findings. By default the presence of **any** of those files fails
the audit closed as deterministic infrastructure; set
`TrustRepositorySuppression` to `true` to trust repo-authored oasdiff
config. An operator-supplied `--config <path>` in `ExtraArguments` is
honored either way (inside oasdiff it outranks the env var and the cwd
lookup).

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.oasdiff"],
      "Enabled": ["codeybox.oasdiff"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.oasdiff" }
```

Only then do the declared `oasdiff`/`git` tool requirements reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.oasdiff`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `1.32.1` | Pinned oasdiff release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `BaseRef` | merge-base of `origin/<BaseBranch>` | Git ref the specs are compared against (`--base`). Set here **or** as `--base` in `ExtraArguments` — setting both is a deterministic failure. |
| `SpecPaths` | discovered | Comma-separated repo-relative OpenAPI spec paths to compare; overrides discovery. Entries must be plain relative paths to regular, non-symlink files inside the worktree. |
| `TrustRepositorySuppression` | `false` | When `true`, repository-root `.oasdiff.*`/`oasdiff.*` config files are honored. When `false`, their presence fails the audit before the scan runs. |
| `AllowExternalRefs` | `false` | When `true`, passes `--allow-external-refs` so specs can resolve http(s)/external file `$ref`s — **and** declares the `Network` audit capability, so the run lands in a network-capable audit sandbox profile (the project's `AuditTool` network profile must permit the egress the specs' refs need). This is an SSRF/out-of-tree-read surface over untrusted spec content; enable only where that is intended. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity — e.g. `error` keeps only `ERR`-level breaking changes. |
| `IncludedRules` / `ExcludedRules` | — | Exact oasdiff check ids to keep/drop (e.g. `api-path-removed-without-deprecation`). |
| `ExcludePaths` | `vendor/, third_party/, node_modules/` | Repo-relative paths excluded — exact path, or directory prefix when trailing `/`. Applied to spec discovery *and* to reported finding paths (a finding sourced to an excluded `$ref`'d file is filtered too). |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). Useful for `--match-path`, `--unmatch-path`, `--stability-level`, `--deprecation-days-*`, `--severity-levels <file>`, `--err-ignore/--warn-ignore <file>`, `--config <file>`, or a different `--fail-on`/`--base`. Take care: an operator-supplied `--format`/`-f` or `--template` breaks the JSON output contract and is a deterministic configuration failure; `--allow-external-refs` must go through the `AllowExternalRefs` scoped key (it additionally declares the `Network` capability); `--fetch` is likewise rejected (it would make oasdiff run `git fetch origin <base>` — network egress the sandbox profile never declared; keep the base commit in the clone instead); `--severity-levels`/`--err-ignore`/`--warn-ignore` point into the repo at your own trust. |
| `TimeoutSeconds` | `300` | Per-run bound; exceeding it is infrastructure, not a pass. Baseline/discovery probes share it under a 30 s cap. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |
| `FindingsExitCodes` | `0, 1` | The verdict exits; do not change unless oasdiff's convention changes. |

## Default scope

One `breaking-files` run at the repository root over every candidate spec:
git `ls-files` (tracked plus untracked-but-not-ignored files) filtered to
basenames containing `openapi` or `swagger` with a `.yaml`/`.yml`/`.json`
extension — the convention oasdiff's own pre-commit hook ships — minus
`ExcludePaths` (`vendor/`, `third_party/`, `node_modules/` by default, so
upstream contract copies and dependency bundles never produce noise), then
intersected with regular, non-symlink files actually present in the
worktree. The
scope is deliberately narrow: a matched non-spec makes the run fail loudly
(a load error is infrastructure, not a finding), so discovery errs toward
fewer, surer candidates — name any other spec via `SpecPaths`. An unchanged
spec compares `[]` and adds nothing; more than 200 resolved specs is a
deterministic failure pointing at `SpecPaths`.
