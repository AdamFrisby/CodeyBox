# CodeyBox: Conftest Policy-as-Code Auditor

Auditor plugin wrapping [conftest](https://www.conftest.dev/)
(`conftest test --output json`): it evaluates Rego policies against the
audited repository's structured configuration — Kubernetes manifests,
Terraform, Dockerfiles, JSON/YAML/TOML, and the rest of conftest's parser
list — and reports each `deny`/`violation` failure, `warn` warning, and
policy evaluation exception as an audit finding with its rule query
(`metadata.query`, e.g. `data.main.deny`) and file/line location preserved.

## What it reports

- One finding per conftest result. The title carries the rule query, so
  `IncludedRules`/`ExcludedRules` select by query (e.g. `data.main.deny`,
  `data.main.warn`). The query that produced each result is embedded by the
  tool itself (`metadata.query`); results without one report under a
  synthesized `conftest/<category>` rule id.
- `Location` is the evaluated file with a `:line` suffix wherever the tool
  supplies one. conftest reports the evaluated file per check
  (`filename`) plus an optional exact origin (`loc.file`/`loc.line`, from
  the Rego `_loc` metadata, conftest v0.64+): the auditor prefers the
  `loc` file when present and falls back to the evaluated file.
- **Severity: declared mapping — denials fail the audit by default.**
  conftest has no severity levels, only result categories, so the auditor
  assigns the meaning: `failure` (`deny`/`violation` rules) and
  `exception` (a policy that could not be evaluated at all) map to `Error`
  and fail the audit; `warning` (`warn` rules) maps to `Warning`
  (advisory), as does anything unrecognised from a foreign build (visible,
  never silently informational). Raw tool tokens never reach findings; the
  category is preserved in the finding description as proof the value
  flowed through the mapping. `skipped` results are dropped — a skip means
  a policy chose not to evaluate, not a problem with the tree.

## What it cannot see

- **Anything outside conftest's parser list.** Only files with a supported
  config format are evaluated; application source and the `policy/`
  directory itself are out of scope by construction.
- **Runtime semantics.** conftest is static policy evaluation: it cannot
  tell whether endpoints respond, clusters admit the manifests, or a plan
  would go green.
- **Suppressed rules.** The audited repository's `policy/` directory (and
  `Namespace` selection) decides what the tool enforces — that is the
  scanner's own configuration surface. Operators who want a fixed rule set
  pin it with `PolicyPath` (outside the audited tree) and
  `Namespace`/`AllNamespaces`.
- **Policies that need the network.** The auditor runs without network
  egress and never fetches anything during the audit (`--update` is
  rejected); a policy calling `http.send` fails closed as an exception
  finding under the default sandbox.
- **Findings under an `ExcludePaths` prefix** are dropped from the report
  (conftest still scans them — the filter is post-scan). Re-include by
  overriding `ExcludePaths`.
- **Repositories without policies or without supported config files.**
  conftest exits non-zero without a report ("no policies found", "no files
  found") — that is an infrastructure failure, not a pass. Enable this
  auditor only on projects that carry conftest policies.

## Exit codes and failure classification

Verified by running the pinned binary (0.70.1) across a matrix of clean,
failure-bearing, warnings-only, missing-policy, and bad-flag invocations
(consistent with `ExitCode`/`ExitCodeFailOnWarn` in `output/result.go`):

| Exit | stdout | Meaning | Classification |
|---|---|---|---|
| `0` | JSON check-result array | ran clean — no failures (warnings and exceptions may still be present; warnings never affect the exit code) | findings-producing |
| `1` | JSON check-result array | ran, policy failures found | findings |
| `1` | none / plain text | could not run (no policies found, bad flags, unreadable input, unusable policy path — the cobra error path) | infrastructure |
| `2` | — | only produced with `--fail-on-warn`, which the auditor rejects | infrastructure |
| `126` / `127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

The discriminator is the report, not the exit code: exits `0` and `1` are
findings-producing, and an exit without a parseable JSON check-result
array fails closed as infrastructure through the parser. A missing
`conftest` binary, a version mismatch, a timeout, and unparseable output
are likewise infrastructure failures naming the tool — never a passing
audit. A scanner that silently passes because it did not run is the worst
outcome available, and this auditor has no path that produces it.

## Version pinning

The auditor is pinned to **conftest `0.70.1`** (`ExpectedVersion` in
scoped config): the supported config formats, Rego version, and report
shape change between releases, so an unpinned binary would change findings
under you. `conftest --version` (which prints `Conftest: x.y.z` plus the
OPA version; the pin reads the Conftest version) is probed before every
run; any other version is an infrastructure failure.

The tool requirement is declared **verify-only** — no `AptPackage`: no
distro package carries conftest. Provision the pinned upstream release
into the sandbox baseline via `CodeyBox:MultipassExtraRuncmd` /
`CodeyBox:Incus:ExtraRuncmd` or `ExecutableProvisions`, e.g.:

```sh
# baseline bake step (adjust arch; verify against the release checksums)
CONFTEST_VERSION=0.70.1
curl -fsSL "https://github.com/open-policy-agent/conftest/releases/download/v${CONFTEST_VERSION}/conftest_${CONFTEST_VERSION}_Linux_x86_64.tar.gz" -o /tmp/conftest.tar.gz
tar -xzf /tmp/conftest.tar.gz -C /tmp conftest
install -c -m 0755 /tmp/conftest /usr/local/bin/conftest
conftest --version   # must print the pinned version
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.conftest"],
      "Enabled": ["codeybox.conftest"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.conftest" }
```

Only then does the declared `conftest` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.conftest`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `0.70.1` | Pinned conftest release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `PolicyPath` | — (repo `policy/`) | Comma-separated `--policy` paths. Point outside the audited tree for a rule set the repository cannot narrow. |
| `Namespace` | — (tool default `main`) | Comma-separated `--namespace` selection; conftest glob wildcards (e.g. `k8s.*`) are passed through. Ignored when `AllNamespaces` is true. |
| `AllNamespaces` | `false` | `--all-namespaces`: test every namespace the policy bundle defines instead of the `Namespace` selection. |
| `Targets` | — (whole tree `.`) | Comma-separated repo-relative files/directories, passed as positional arguments. Entries must be repo-relative (no absolute paths, no `..`). |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule queries (`metadata.query` values such as `data.main.deny`) to keep/drop (finding-level filter, applied after the scan). |
| `ExcludePaths` | `.terraform/`, `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Post-scan filter. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell), e.g. `--combine`, `--data` paths, `--ignore` patterns, or `--parser`. **Managed by the auditor:** `--output`/`-o` (the JSON report is the parsing contract), `--fail-on-warn`/`--no-fail`/`--quiet`/`--suppress-exceptions` (they rewrite the exit-code and report contract), and `--update`/`-u` (the auditor never downloads policies during the audit) are rejected with a deterministic infrastructure error. |
| `TimeoutSeconds` | `300` | Per-run bound; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Default scope

`conftest test --output json .` from the work-tree root: the tool walks
the tree itself and evaluates only files with a supported config format,
so application source and the `policy/` directory are out of scope by
construction. The finding-level `ExcludePaths` default additionally drops
`.terraform/` (modules downloaded by `terraform init` — upstream code, not
the change under audit), `vendor/`, `third_party/`, and `node_modules/`.
An operator who wants the scan itself narrowed to specific files or
directories uses `Targets`; an operator who wants conftest's own ignore
expression uses `--ignore` via `ExtraArguments`.
