# CodeyBox: Kubeconform Kubernetes Manifest Auditor

Auditor plugin wrapping [kubeconform](https://github.com/yannh/kubeconform):
it validates every Kubernetes manifest in the audited repository
(`*.yaml`/`*.yml`/`*.json` documents carrying a `kind` field) against the
JSON schemas for their `apiVersion`/`kind` and reports each problem as an
audit finding.

## What it reports

- One finding per resource kubeconform could not prove valid:
  - `statusInvalid` — the resource violates its kind's schema. The title
    carries a synthesized rule id `kubeconform/<Kind>` (e.g.
    `kubeconform/Deployment`) so `ExcludedRules`/`IncludedRules` can select
    by kind. The description carries the tool, rule, tool status, location,
    the resource signature (`Kind`/`apiVersion`/`name`), kubeconform's
    message, and the failing field paths from `validationErrors`.
  - `statusError` — the resource could not be validated at all: unreadable
    file, YAML parse failure, or a schema that could not be resolved
    (missing CRD schema, unreachable registry). Reported under rule id
    `kubeconform/error`. These are findings, not silent skips — a manifest
    the auditor cannot verify fails the audit.
- `Location` is the file path kubeconform reports (repo-relative for the
  default `.` target). kubeconform does not emit line numbers, so locations
  carry no `:line` suffix; the offending field paths inside the document
  (`/spec/replicas`, …) are in the message instead.
- **Severity: blocking.** kubeconform has no severity vocabulary — a
  resource is valid or it is not — so the declared mapping sends every
  reported status (`statusInvalid`, `statusError`, and any unknown status
  from a foreign build) to `Error`. There is no advisory mode; narrow the
  scope instead (`Targets`, `ExcludedRules`, `ExcludePaths`).

## What it cannot see

- **Documents without a `kind` field.** kubeconform skips them upstream, so
  non-manifest YAML (CI workflows, compose files) is never validated —
  including a manifest-shaped file whose `kind` is only produced by
  templating at apply time.
- **Rendered Helm/Kustomize output.** kubeconform validates what is on disk;
  templates containing `{{ … }}` placeholders usually surface as
  `statusError` (YAML parse failure) rather than as the manifests they
  would render into. Exclude chart/template directories via `ExcludePaths`
  or `Targets`.
- **Semantic correctness.** It checks schema conformance (field names,
  types, required properties) — not whether values are sane, images exist,
  or RBAC is minimal.
- **Kinds with no schema.** Unless the operator provisions schemas for CRDs
  (`SchemaLocations`) or opts into `IgnoreMissingSchemas`, a resource whose
  kind has no resolvable schema is a `statusError` finding, not a pass.
- **Findings under an `ExcludePaths` prefix** are dropped from the report
  (kubeconform still validates them — the filter is post-scan, so excluded
  trees still cost schema lookups). Re-include by overriding `ExcludePaths`.

## Exit codes and failure classification

kubeconform does **not** follow the common "0 = clean, 1 = findings,
2 = could not run" convention — verified against the v0.8.x source: flag
parse errors, an unknown `-output` format, validator-init failures, and a
completed scan with invalid/error resources **all exit `1`**. The
discriminator is the report, not the exit code: a completed scan always
writes `{"resources": […], "summary": {…}}` to stdout; every run failure
writes a plain-text error to stderr and no report.

| Exit | stdout | Meaning | Classification |
|---|---|---|---|
| `0` | JSON report | ran clean | pass |
| `1` | JSON report | ran, invalid/error resources | findings |
| `1` | none / not JSON | could not run (bad flags, validator init, stdin misuse) | infrastructure |
| `126` / `127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

A missing `kubeconform` binary, a version mismatch, a timeout, and
unparseable output are likewise infrastructure failures naming the tool —
never a passing audit.

## Version pinning

The auditor is pinned to **kubeconform `0.8.0`** (`ExpectedVersion` in
scoped config): the validation surface (JSON Schema dialect, status
vocabulary, error taxonomy) changes between releases, so an unpinned binary
would change findings under you. `kubeconform -v` is probed before every
run; a source build (which reports `development`) or any other version is
an infrastructure failure.

The tool requirement is declared **verify-only** — no `AptPackage`: no
distro package carries kubeconform. Provision the pinned upstream release
into the sandbox baseline via `CodeyBox:MultipassExtraRuncmd` /
`CodeyBox:Incus:ExtraRuncmd` or `ExecutableProvisions`, e.g.:

```sh
# baseline bake step (adjust arch; verify against the release checksums)
KUBECONFORM_VERSION=0.8.0
curl -fsSL "https://github.com/yannh/kubeconform/releases/download/v${KUBECONFORM_VERSION}/kubeconform-linux-amd64.tar.gz" -o /tmp/kubeconform.tar.gz
tar -xzf /tmp/kubeconform.tar.gz -C /usr/local/bin kubeconform
kubeconform -v   # must print the pinned version
```

## Schemas and network egress

kubeconform resolves schemas through `-schema-location`. With no location
configured it fetches from the upstream `kubernetes-json-schema` repository
at `raw.githubusercontent.com` — that is why the auditor declares the
`Network` capability. For that path to work in an audit-tool sandbox, the
schema host must be in `CodeyBox:AuditToolAllowedHosts`. Fully offline
deployments instead provision a schema tree into the baseline (the
kubernetes-json-schema layout
`<dir>/<k8s-version>-standalone/<kind>-<group>-<version>.json`, or a custom
template) and set `SchemaLocations` to the directory. If neither is in
place, every resource reports `statusError` and the audit fails — loudly,
never as a pass.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.kubeconform"],
      "Enabled": ["codeybox.kubeconform"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.kubeconform" }
```

Only then does the declared `kubeconform` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared).

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.kubeconform`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `0.8.0` | Pinned kubeconform release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `SchemaLocations` | — (upstream default) | Comma-separated `-schema-location` values: `default`, URLs, local directories, or templates. Repeated per kubeconform's own semantics. |
| `KubernetesVersion` | — (`master`) | `-kubernetes-version` value: `master` or full `x.y.z`. Pin for stable schema revisions — `master` tracks upstream schema changes. |
| `Targets` | `.` | Comma-separated files/folders scanned positionally. Repo-relative paths keep finding locations repo-relative. |
| `Strict` | `false` | `-strict`: reject properties not in the schema and duplicated keys. |
| `IgnoreMissingSchemas` | `false` | `-ignore-missing-schemas`: skip kinds with no resolvable schema instead of reporting `statusError`. Off by default because it turns "no schema" into a silent skip — opt in for CRD-bearing repos whose schemas you do not provision. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. Everything maps to `error`, so this only matters if the mapping changes. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids to keep/drop — `kubeconform/<Kind>` for violations, `kubeconform/error` for unvalidatable resources. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Post-scan filter. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). **Paths only**: kubeconform's Go flag parser stops at the first positional argument, so flag-looking entries (`-strict`, `-skip`, …) cannot reach its parser — the auditor rejects them with a deterministic infrastructure error. Use the scoped keys above for flags. |
| `TimeoutSeconds` | `300` | Per-run bound; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

kubeconform reads **no configuration file from the audited repository** —
there is no repo-controlled suppression surface to gate.

## Default scope

`kubeconform .` over the whole work tree: upstream file discovery already
limits the scan to `*.yaml`/`*.yml`/`*.json` documents that carry a `kind`,
so CI/config YAML is not linted. On top of that, findings under `vendor/`,
`third_party/`, and `node_modules/` are dropped by default — invalid
manifests shipped inside a dependency describe the upstream package, not
the change under audit, and reporting them would train operators to ignore
the auditor. Both are finding-level filters; an operator who also wants the
scan itself narrowed sets `Targets` or `ExcludePaths`.
