# CodeyBox: Kyverno Policy Auditor

Auditor plugin wrapping the [Kyverno CLI](https://kyverno.io/docs/kyverno-cli/)
(`kyverno apply`): it evaluates the candidate Kubernetes manifests in the
audited repository against operator-owned, validate-only Kyverno policies
and reports each rule violation as an audit finding.

## What it reports

- One finding per non-passing policy-report result:
  - `fail` — a validate rule fired. Rule id
    `kyverno/<policy>/<rule>` (e.g.
    `kyverno/require-labels/require-app-label`) so
    `ExcludedRules`/`IncludedRules` can select by policy rule.
    Severity `Error`: fails the audit.
  - `warn` — an audit-type finding. Same rule-id shape.
    Severity `Warning`: advisory, does not fail the audit.
  - `error` — the rule could not be evaluated (unsupported policy,
    missing context, engine failure). Reported, never dropped:
    unevaluated coverage fails the audit (`Error`).
  - `skip` — the rule chose not to evaluate. Reported as `Error`:
    uncovered resources must never read as clean.
  - Anything unrecognised from a foreign build is reported as `Error`
    (fail closed).
- `pass` results are dropped. A clean evaluation passes.
- **Locations carry resource identity, not file paths.** The PolicyReport
  records `kind`/`name`/`namespace`/`apiVersion`, not which file a resource
  came from, so finding messages identify the resource by coordinates
  (e.g. `Deployment 'web' (namespace default) [apps/v1]: …`) and locations
  carry no `:line` suffix. Narrow the scan itself with `Targets`.
- **Severity: hybrid.** Failures and unevaluated coverage fail the audit;
  warnings are advisory. `MinimumSeverity` only drops findings, never
  raises them.

## What it cannot see

- **Which file a violating resource came from.** See above — scope with
  `Targets` instead of hunting by path.
- **Mutate/generate/verifyImages behaviour.** The auditor never enables
  it (no `--output`, `--stdin`, `--target-resources`, `--registry`,
  `--generate-exceptions`), and `PolicyPaths` must point at validate-only
  policies. A bundle containing mutate/generate rules is outside the
  contract this auditor reports on — keep such policies out of the pinned
  set.
- **Candidate-supplied suppressions.** No `--exception(s)` or
  `--exceptions-with-*` flag is ever passed, so `PolicyException`
  resources in the candidate tree are not honored by the scan; the
  operator's policy set is the whole gate.
- **Live-cluster state.** No `--cluster`, `--kubeconfig`, `--context`,
  image-registry, or API-context flags — rules depending on cluster state
  report `error` (a finding) rather than silently passing.
- **A vacuous scan.** Zero evaluated results (no policies matched, no
  resources loaded) fails closed as infrastructure — never a pass. A
  genuinely clean scan carries its passing count in `summary.pass`.

## Exit codes and failure classification

`kyverno apply` exits `0` when every evaluated rule passed and non-zero
when policy failures were found — but the same non-zero exit covers run
failures (bad flags, unreadable policy/resource paths), which write plain
text instead of the JSON policy report. The discriminator is the report,
not the exit code: a completed scan always writes the policy report to
stdout; every run failure writes a plain-text error and no report.
`--warn-exit-code` (which would introduce a separate warning exit) is
rejected by construction — no flag-looking `ExtraArguments` are accepted —
so warning-only evaluations keep the default convention.

| Exit | stdout | Meaning | Classification |
|---|---|---|---|
| `0` | JSON policy report | ran clean (or advisory-only) | pass |
| `1` | JSON policy report | ran, with failures/errors/skips | findings |
| `1` | none / not JSON | could not run (bad flags, bad paths) | infrastructure |
| `126` / `127` | — | cannot execute / not found | infrastructure |
| anything else | — | unknown convention | infrastructure (fails loud, never a pass) |

A missing `kyverno` binary, a version mismatch, missing `PolicyPaths`,
an in-tree policy path, a timeout, and unparseable output are likewise
infrastructure failures naming the tool — never a passing audit.

## Version pinning

The auditor is pinned to **kyverno `1.19.1`** (`ExpectedVersion` in
scoped config): the rule surface (supported rule types, JMESPath version,
report shape) changes between releases, so an unpinned binary would change
findings under you. `kyverno version` is probed before every run; any other
version is an infrastructure failure. Set `ExpectedVersion` to the release
you provisioned.

The tool requirement is declared **verify-only** — no `AptPackage`: no
distro package carries kyverno. Provision the pinned upstream release
into the sandbox baseline via `CodeyBox:MultipassExtraRuncmd` /
`CodeyBox:Incus:ExtraRuncmd` or `ExecutableProvisions`, e.g.:

```sh
# baseline bake step (adjust arch; verify against the release checksums)
KYVERNO_VERSION=1.19.1
curl -fsSL "https://github.com/kyverno/kyverno/releases/download/v${KYVERNO_VERSION}/kyverno-cli_v${KYVERNO_VERSION}_linux_x86_64.tar.gz" -o /tmp/kyverno.tar.gz
tar -xzf /tmp/kyverno.tar.gz -C /usr/local/bin kyverno
kyverno version   # must print the pinned version
```

## Offline guarantee and network egress

The auditor declares **no** network capability. The scan
(`kyverno apply --policy … --resource … --policy-report --output-format json
--continue-on-fail`) touches only local files; every flag that could reach
past the sandbox (cluster, kubeconfig, registry, remote git, payload URLs)
is unreachable — none is ever emitted, and no flag-looking
`ExtraArguments` entry is accepted.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.kyverno"],
      "Enabled": ["codeybox.kyverno"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.kyverno" }
```

Only then does the declared `kyverno` tool requirement reach baseline
provisioning (presence-verified at bake time; nothing is apt-installed
because no `AptPackage` is declared). It is **not** part of any active
audit, notification, or work-sync configuration — operator setup is
explicit:

1. Provision the pinned `kyverno` release into the sandbox baseline
   (above).
2. Author validate-only Kyverno policies into an operator-owned directory
   **outside any audited worktree** (e.g. `/opt/codeybox/policies/` baked
   into the baseline or mounted read-only).
3. Set `CodeyBox:Plugins:codeybox.kyverno:PolicyPaths` to that directory.
4. Enable the plugin (gates above) and add the project audit entry.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.kyverno`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `PolicyPaths` | — (required) | Comma-separated policy files/directories, each a repeatable `--policy`. Must resolve outside the audited worktree; in-tree paths fail closed. Unset fails closed — an unevaluated tree never reads as clean. |
| `Targets` | `.` | Comma-separated candidate manifests (files/folders, each a repeatable `--resource`). Must be repo-relative paths inside the worktree — absolute paths and `..` segments are rejected deterministically. |
| `ExpectedVersion` | `1.19.1` | Pinned kyverno release; any other installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity. `warn` findings are `warning`; everything else reported is `error`. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids to keep/drop — `kyverno/<policy>/<rule>` (e.g. `kyverno/require-labels/require-app-label`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Post-scan filter; PolicyReport results carry no file paths, so scope with `Targets` instead. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args (never via a shell). **Resource paths only**: every flag-looking entry (`-…`) is rejected with a deterministic infrastructure error, because kyverno would parse it as a live flag (cluster access, mutation outputs, exceptions, API context). Use the scoped keys above for flags. |
| `TimeoutSeconds` | `300` | Per-run bound; exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

The audited repository supplies only the manifests under audit. It cannot
narrow the policy set: policies, variables, exceptions, and API context all
come from operator configuration outside the tree.

## Default scope

`kyverno apply` over `Targets` (default `.`) against the pinned
`PolicyPaths`, reporting every violation (`--continue-on-fail`) as
structured JSON (`--policy-report --output-format json`).
