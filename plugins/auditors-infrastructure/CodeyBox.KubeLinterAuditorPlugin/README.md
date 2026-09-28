# CodeyBox: KubeLinter Kubernetes Manifest Auditor

Auditor plugin wrapping [kube-linter](https://github.com/stackrox/kube-linter)
(KubeLinter): it runs Kubernetes manifest policy analysis over the audited
repository and reports every check violation as an audit finding — security
and best-practice checks such as `run-as-non-root`, `no-read-only-root-fs`,
`latest-tag`, and `unset-*-requirements`.

## What it reports

- One finding per kube-linter report entry, carrying:
  - `ruleId` — the check name (e.g. `latest-tag`), so `IncludedRules` /
    `ExcludedRules` and the `IncludeChecks`/`ExcludeChecks` knobs select by
    check.
  - The diagnostic message plus the object signature
    (`<namespace>/<name> <group>/<version>, Kind=…`) and the check's
    remediation text (in the rule metadata).
  - `Location` — the manifest path kube-linter reports, relative to the
    work tree for in-tree targets, with the object's start line
    (`file.yaml:N`). kube-linter locates objects, not fields: the line is
    the start of the YAML document the object came from.
- **Severity: blocking.** kube-linter has no severity vocabulary — a check
  either fired or it did not — so the declared mapping sends every
  reported violation to `Error` and fails the audit. There is no advisory
  mode; narrow the check set instead (`ExcludeChecks`, `ExcludedRules`, a
  pinned `ConfigFile`, or the repo's own `.kube-linter.yaml`).

## What it cannot see

- **Non-manifest YAML.** Files without Kubernetes objects are loaded but
  contribute no findings — CI workflows and compose files are out of scope
  by construction.
- **Rendered manifests that are never instantiated.** kube-linter evaluates
  the manifests Helm/Kustomize produce with their checked-in values —
  not overrides applied at deploy time (`--set`, environment values files
  outside the chart, downstream patches it cannot resolve).
- **Cluster-only properties.** It inspects manifest text, not the live API
  server: admission-time defaults, CRDs whose schemas live only in the
  cluster, and runtime behaviour are invisible to it.
- **Semantic correctness beyond its check catalogue.** It lints against
  built-in and configured checks — it does not prove images exist, RBAC is
  minimal, or schemas are valid (pair it with the kubeconform auditor for
  schema validation).
- **Findings under an `ExcludePaths` prefix** are dropped from the report
  (kube-linter still scans them — the filter is post-scan). Re-include by
  overriding `ExcludePaths`, or keep them out of the scan entirely with
  `IgnorePaths`.

## Exit codes and failure classification

kube-linter does **not** follow the common "0 = clean, 1 = findings,
2 = could not run" convention — verified by running the pinned `0.8.3`
release: flag-parse errors, a missing scan target, an unreadable `--config`
file, and a completed scan with check violations **all exit `1`**. The
discriminator is the report, not the exit code: a completed scan always
writes the SARIF document to stdout; every run failure writes a plain-text
`Error: …` to stderr and no report.

| Exit | stdout | Meaning | Classification |
|---|---|---|---|
| `0` | SARIF report | ran clean | pass |
| `1` | SARIF report | ran, checks fired (`Error: found N lint errors`) | findings |
| `1` | none / not SARIF | could not run (bad flags, missing target, unreadable config, no valid objects under `--fail-if-no-objects-found`) | infrastructure |
| `0` | none | vacuous scan — zero checks enabled (`Warning: no checks enabled.`) | infrastructure (fails closed) |
| `126` / `127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

A missing `kube-linter` binary, a version mismatch, a timeout, and
unparseable output are likewise infrastructure failures naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **kube-linter `0.8.3`** (`ExpectedVersion` in
scoped config): the built-in check catalogue, default check set, and SARIF
shape change between releases, so an unpinned binary would change findings
under you. `kube-linter version` is probed before every run; any other
version is an infrastructure failure.

The tool requirement is declared **verify-only** — no `AptPackage`: no
distro package carries kube-linter. Provision the pinned upstream release
into the sandbox baseline via `CodeyBox:MultipassExtraRuncmd` /
`CodeyBox:Incus:ExtraRuncmd` or `ExecutableProvisions`, e.g.:

```sh
# baseline bake step (adjust arch; verify against the release checksums)
KUBE_LINTER_VERSION=0.8.3
curl -fsSL "https://github.com/stackrox/kube-linter/releases/download/v${KUBE_LINTER_VERSION}/kube-linter-linux" -o /usr/local/bin/kube-linter
chmod +x /usr/local/bin/kube-linter
kube-linter version   # must print the pinned version
```

## Repository-controlled configuration

kube-linter **auto-loads `.kube-linter.yaml`/`.kube-linter.yml` from the
audited repository root** — check selection, custom checks, and ignore
paths in that file are the tool's normal configuration surface and apply
to the scan. A repo cannot silently blind the auditor: a config that
enables zero checks produces `Warning: no checks enabled.` with no report,
which fails closed as infrastructure. Operators who want a check set the
repository cannot narrow pin one with `ConfigFile` (a path outside the
audited tree) or drive selection with the `IncludeChecks`/`ExcludeChecks`/
`DoNotAutoAddDefaults`/`AddAllBuiltIn` knobs.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.kube-linter"],
      "Enabled": ["codeybox.kube-linter"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.kube-linter" }
```

Only then does the declared `kube-linter` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.kube-linter`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `0.8.3` | Pinned kube-linter release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ConfigFile` | — (repo `.kube-linter.yaml`) | `--config` path. Point it outside the audited tree for a check set the repository cannot narrow. |
| `Targets` | `.` | Comma-separated files/folders scanned positionally. Repo-relative paths keep finding locations repo-relative. |
| `IncludeChecks` | — | Comma-separated check names → repeatable `--include`. Adds to the default set; combine with `DoNotAutoAddDefaults` for exclusive selection. |
| `ExcludeChecks` | — | Comma-separated check names → repeatable `--exclude`. Unknown names are silently ignored by kube-linter (more findings, never fewer). |
| `DoNotAutoAddDefaults` | `false` | `--do-not-auto-add-defaults`: run only explicitly included checks. Zero enabled checks fail closed as infrastructure — pair it with `IncludeChecks`. |
| `AddAllBuiltIn` | `false` | `--add-all-built-in`: run every built-in check, not just the default set; opt out individually with `ExcludeChecks`. |
| `IgnorePaths` | — | Comma-separated doublestar globs matched against absolute paths → repeatable `--ignore-paths` (e.g. `**/vendor/**`). Ignored paths are never loaded — a scan-scope filter, unlike `ExcludePaths`. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. Everything maps to `error`, so this only matters if the mapping changes. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids (check names) to keep/drop — post-scan filter. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Post-scan filter. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). kube-linter uses pflag, so flag entries still parse after the positional targets. `--format`, `--output`, and `--config` are rejected deterministically: the first two break the stdout parsing contract (and `--output` writes into the audited tree), the third duplicates `ConfigFile`. |
| `TimeoutSeconds` | `300` | Per-run bound; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

## Default scope

`kube-linter lint .` over the whole work tree: upstream file discovery
already limits the scan to `*.yaml`/`*.yml` documents plus auto-detected
Helm chart directories, `.tgz` chart archives, and Kustomize roots, all
rendered locally with no network. On top of that, findings under
`vendor/`, `third_party/`, and `node_modules/` are dropped by default —
policy violations shipped inside a dependency describe the upstream
package, not the change under audit, and reporting them would train
operators to ignore the auditor. Both are finding-level filters; an
operator who also wants the scan itself narrowed sets `Targets` or
`IgnorePaths`.
