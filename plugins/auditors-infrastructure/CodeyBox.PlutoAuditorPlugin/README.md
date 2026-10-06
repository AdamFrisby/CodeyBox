# CodeyBox: Pluto Kubernetes Deprecations Auditor

Auditor plugin wrapping [Pluto](https://github.com/FairwindsOps/pluto)
(`pluto detect-files`): it scans the Kubernetes manifests in the audited
repository for apiVersions that are deprecated or removed on the configured
target Kubernetes version, and reports each use as an audit finding —
explaining a candidate deployment failure before it occurs.

## What it reports

One finding per object using a known-deprecated apiVersion (pluto already
drops every clean object before writing its report):

- `pluto/removed` — the apiVersion no longer exists on the target (e.g.
  `extensions/v1beta1` on `v1.22+`). **Severity: error.** A manifest
  carrying it cannot be applied to the target cluster.
- `pluto/replacement-unavailable` — deprecated, and no replacement
  apiVersion is available on the target: there is no upgrade path.
  **Severity: error.**
- `pluto/deprecated` — deprecated but still served on the target, with a
  replacement available. **Severity: warning**: visible in the report
  without failing an otherwise clean audit.
- `pluto/api` — an item whose deprecation flags the parser could not
  determine (a report shape newer than the pinned tool). **Severity:
  error**: an unfamiliar build fails the audit, never passes it.
- `Location` is the manifest path pluto reports (`filePath`),
  repo-relative for the default `.` target. pluto emits no line numbers,
  so locations carry no `:line` suffix.
- The description carries the tool, rule, tool level, location, the object
  signature (`Kind 'name'` + apiVersion + namespace), the target version
  pluto confirmed in its report, the deprecated-in/removed-in versions, and
  the replacement apiVersion when one exists.

## What it cannot see

- **Rendered Helm/Kustomize output.** `detect-files` reads what is on disk;
  chart templates are not rendered (the live `detect-helm` /
  `detect-api-resources` subcommands are never invoked — this auditor is
  fully offline and declares no network capability). A values-templated
  `apiVersion` is checked literally, not as rendered.
- **Custom apiVersions.** Only versions in pluto's embedded catalogue (plus
  the target's component defaults) are known. The `--additional-versions`
  knob is intentionally not exposed: it would hand the tool a
  repository-controlled file as ground truth.
- **Non-k8s components.** `--target-versions k8s=<TargetKubernetesVersion>`
  overrides the Kubernetes default; every other component keeps pluto's
  embedded default target.
- **How many files were scanned.** pluto's JSON carries no scanned-file
  count, so a tree whose YAML files are all non-manifest documents still
  passes clean once candidate files exist (see below). Non-manifest YAML
  (CI workflows, compose files) never matches a known apiVersion and is
  out of scope by construction.
- **Findings under an `ExcludePaths` prefix** are dropped from the report
  (post-scan filter). Re-include by overriding `ExcludePaths`.

## Exit codes and failure classification

Verified against the pluto source (`GetReturnCode` in `pkg/api/output.go`;
finder/flag failures in `cmd/root.go`): `0` = clean, `2` = deprecations,
`3` = removals, `4` = unavailable replacements (later classes overwrite
earlier ones, so `4` wins when several are present). All three are
findings-producing — a completed scan always writes the JSON report to
stdout.

| Exit | stdout | Meaning | Classification |
|---|---|---|---|
| `0` | JSON report (`target-versions` + zero/one `items`) | ran clean or ran with findings downgraded by flags | pass / findings |
| `2` / `3` / `4` | JSON report | ran, deprecated / removed / replacement-unavailable apiVersions | findings |
| `1` | none / not JSON | could not run (finder error, bad flags, no sub-command) | infrastructure |
| `126` / `127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

A missing `pluto` binary, a version mismatch, a timeout, unparseable
output, a report without its `target-versions` block, and a scan root with
no candidate manifests are likewise infrastructure failures naming the
tool — never a passing audit.

## Coverage: no vacuously clean audits

A clean pluto scan omits `items` from its JSON, so "nothing to report" and
"nothing was inspected" share a shape. Two guards close the gap:

1. The parser requires the `target-versions` block — proof the report came
   from a completed run with a target. A truncated document or a non-pluto
   JSON object fails closed.
2. Before the scan runs, the auditor probes the scan root for candidate
   manifests (`*.yaml` / `*.yml` / `*.json`). A tree with none is
   infrastructure ("pluto would inspect nothing"), never a pass.

## Version pinning

The auditor is pinned to **pluto `5.24.4`** (`ExpectedVersion` in scoped
config): the deprecation catalogue, exit codes, and JSON shape change
between releases, so an unpinned binary would change findings under you.
`pluto version` is probed before every run; any other version is an
infrastructure failure.

The tool requirement is declared **verify-only** — no `AptPackage`: no
distro package carries pluto. Provision the pinned upstream release into
the sandbox baseline via `CodeyBox:MultipassExtraRuncmd` /
`CodeyBox:Incus:ExtraRuncmd` or `ExecutableProvisions`, e.g.:

```sh
# baseline bake step (adjust arch; verify against the release checksums)
PLUTO_VERSION=5.24.4
curl -fsSL "https://github.com/FairwindsOps/pluto/releases/download/v${PLUTO_VERSION}/pluto_${PLUTO_VERSION}_linux_amd64.tar.gz" -o /tmp/pluto.tar.gz
tar -xzf /tmp/pluto.tar.gz -C /usr/local/bin pluto
pluto version   # must print the pinned version
```

## Offline and environment

`detect-files` reads local files only. pluto additionally honors
`PLUTO_*` environment variables for the same flags argv sets; the auditor
unsets every documented `PLUTO_*` variable on the scan exec, so explicit
argv is the sole control surface and a baseline environment cannot reshape
the gate. The auditor declares **no network capability**.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.pluto"],
      "Enabled": ["codeybox.pluto"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.pluto" }
```

Only then does the declared `pluto` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared). It is not part of any default audit,
notification, or work-sync configuration: enabling it for a project is a
later, explicit operator step.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.pluto`, resolved per run
(hot-reloadable). `TargetKubernetesVersion` has no default — the audit
fails closed until the operator sets it.

| Key | Default | Meaning |
|---|---|---|
| `TargetKubernetesVersion` | — (REQUIRED) | Intended Kubernetes version, semver with leading `v` (e.g. `v1.29.0`). Becomes `--target-versions k8s=<value>`. Missing or malformed values are deterministic configuration failures. |
| `Targets` | `.` | The single repo-relative directory scanned as pluto's `-d`. Must stay inside the worktree — absolute paths and `..` segments are rejected. Several entries are rejected: `-d` takes one directory, so more would silently narrow the scan. |
| `OnlyShowRemoved` | `false` | `--only-show-removed`: report only apiVersions removed (not merely deprecated) on the target. |
| `ExpectedVersion` | `5.24.4` | Pinned pluto release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. Deprecated findings are `warning`, so raising this to `error` keeps only removals and unavailable replacements. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids to keep/drop — `pluto/deprecated`, `pluto/removed`, `pluto/replacement-unavailable`, `pluto/api`. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Post-scan filter. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). **Nothing pluto consumes**: verdict-shaping flags (`--target-versions`, `--output`, `--directory`, `--additional-versions`, `--only-show-removed`, `--ignore-*`, `--components`, and their short forms) are rejected with a deterministic infrastructure error. Use the scoped keys above. |
| `TimeoutSeconds` | `300` | Per-run bound; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

pluto reads **no configuration file from the audited repository** for
`detect-files` — there is no repo-controlled suppression surface to gate,
and the one version-data channel (`--additional-versions`) is deliberately
unavailable.

## Default scope

`pluto detect-files -d . --target-versions k8s=<version> -o json` over the
whole work tree. Findings under `vendor/`, `third_party/`, and
`node_modules/` are dropped by default — deprecated apiVersions shipped
inside a dependency describe the upstream package, not the change under
audit. An operator who also wants the scan itself narrowed sets the single
`Targets` directory.
