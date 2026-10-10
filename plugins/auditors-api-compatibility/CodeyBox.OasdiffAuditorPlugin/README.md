# CodeyBox: Oasdiff OpenAPI Breaking-Changes Auditor

Auditor plugin wrapping [`oasdiff breaking`](https://github.com/oasdiff/oasdiff)
(`oasdiff`): it compares the candidate OpenAPI document in the audited working tree
against an approved baseline document and reports breaking changes as CodeyBox
audit findings. Both documents pass `oasdiff validate` before the comparison runs,
so a document that is not an OpenAPI spec can never read as a clean comparison.

## What it reports

- One finding per `oasdiff breaking --format json` entry. The title carries the
  tool's stable check identifier (e.g. `response-success-status-removed`,
  `api-path-removed-without-deprecation`); the description carries the tool, rule,
  tool-reported severity, location, OpenAPI path and operation, and the change text.
  `Location` is the candidate document plus the tool-reported line (`path:line`)
  where the tool supplies one — the revision origin for changed content, the base
  origin for removed content. Findings never carry a line number the tool did not
  report.
- **Gate behaviour: blocking on definite breaking changes.** Tool level `ERR`
  (a consumer following the old contract can stop working) maps to
  `AuditSeverity.Error` and **fails the audit**; level `WARN` (a potential break
  the definition cannot decide alone) maps to `AuditSeverity.Warning` and is
  advisory. There is no advisory mode for definite breaks; narrow scope with
  `FailOn`, `IncludedRules` / `ExcludedRules`, `ExcludePaths`, or `MinimumSeverity`
  instead.

## What it cannot see

- **Non-OpenAPI changes.** Only the two configured documents are compared;
  application code, generated clients, and gateway configuration are not.
- **Compatible evolution.** Added endpoints, added optional parameters, and widened
  schemas are not breaking and produce no findings — only `ERR`/`WARN` entries do.
- **Consumer usage.** A reported difference is a contract risk, not proof that a
  consumer breaks: the diff knows nothing about real operation usage.
- **Excluded paths.** `ExcludePaths` drops findings by repository-relative file;
  the default list is empty because the tool reads exactly the two configured
  documents and never walks the tree.
- **Rule tuning in the audited repo.** Ignore files (`--err-ignore`,
  `--warn-ignore`) and custom severity levels (`--severity-levels`) are rejected
  in `ExtraArguments` on purpose: an in-repo file must not soften the check the
  audit applies to the change set carrying it. Post-hoc rule filtering through
  `IncludedRules` / `ExcludedRules` is applied by the audit host, outside the
  subject's reach.
- **Remote references when offline.** Specs or `$ref`s naming remote URLs need
  network egress to those hosts from the audit sandbox; without it the load or
  the validation probe fails closed as infrastructure. Pass
  `--allow-external-refs=false` in `ExtraArguments` when auditing untrusted specs
  to stop the loader resolving external references.

## Exit codes and failure classification

`oasdiff`'s exit convention (verified against oasdiff 1.33.0 — it is **not** the
common 0/1/2 convention):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Ran clean at the `--fail-on` threshold (`[]`, or WARN-only entries with the default `ERR` threshold) | Verdict (Pass, or advisory findings) |
| `1` | Ran and found breaking changes at or above the `--fail-on` threshold (JSON report on stdout) | Verdict (`Passed = false`, findings reported) |
| `1` (no report) | Findings exit with an unparseable or empty report — contradicts the output contract | Infrastructure (`AuditUnavailableException`) |
| `0` (with threshold entries) | Clean exit carrying at-or-above-threshold entries — contradicts the output contract | Infrastructure (`AuditUnavailableException`) |
| `100` | Could not run: usage error (unknown flag) | Infrastructure (`AuditUnavailableException`) |
| `102` | Could not run: a spec failed to load (missing file, unparseable content) | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure (`AuditUnavailableException`) |
| anything else | Unknown convention or signal termination | Infrastructure (`AuditUnavailableException`) |

The `validate` pre-probes classify the same way: exit `0` lets the scan proceed,
any other exit is coverage unavailable naming the document.

## Version pinning

The auditor is pinned to **oasdiff `1.33.0`** (`DefaultExpectedVersion`).
A scanner's check catalogue and report shape evolve between releases, so
an unpinned tool would change findings across runs. The auditor probes
`oasdiff --version` before scanning; any version mismatch or missing binary
fails closed as infrastructure. Operators running a different pinned
build configure `ExpectedVersion` in scoped configuration.

The tool requirement is declared **verify-only** (no `AptPackage`): no
distro apt package carries a pinned `oasdiff`. Provision the pinned release
in your sandbox baseline from the upstream release tarball:

```sh
# Baseline provisioning bake step (x86_64 example)
curl -fsSL -o /tmp/oasdiff.tar.gz \
  https://github.com/oasdiff/oasdiff/releases/download/v1.33.0/oasdiff_1.33.0_linux_amd64.tar.gz
curl -fsSL -o /tmp/oasdiff-checksums.txt \
  https://github.com/oasdiff/oasdiff/releases/download/v1.33.0/checksums.txt
(cd /tmp && grep 'oasdiff_1.33.0_linux_amd64.tar.gz' oasdiff-checksums.txt | sha256sum -c -)
tar -xzf /tmp/oasdiff.tar.gz -C /tmp/oasdiffstage
install -m 0755 /tmp/oasdiffstage/oasdiff /usr/local/bin/oasdiff
oasdiff --version   # must print 1.33.0
```

## Enabling

The plugin is **disabled by default**. It loads only when allowlisted and
enabled:

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

When disabled, the plugin's `oasdiff` tool requirement
contributes nothing to sandbox baseline provisioning — no presence probe
and no install step mention `oasdiff`.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.oasdiff`, hot-reloadable per run:

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `1.33.0` | Pinned oasdiff release. A different installed version fails closed as infrastructure. |
| `BaseSpec` | — (required) | Approved baseline OpenAPI document (repository-relative `.json`/`.yaml`/`.yml`). |
| `Spec` | — (required) | Candidate revision OpenAPI document (repository-relative `.json`/`.yaml`/`.yml`). |
| `FailOn` | `ERR` | Which severities fail the scan command: `ERR` (definite breaks) or `WARN` (definite plus potential breaks). |
| `MinimumSeverity` | `info` | Findings below this mapped severity are dropped. |
| `IncludedRules` / `ExcludedRules` | — | Exact oasdiff check identifiers to include or drop (e.g. `response-success-status-removed`). Applied by the host after the scan. |
| `ExcludePaths` | — (empty) | Repository-relative paths dropped from findings. Empty by default: the tool reads exactly two files, so there is no vendored tree to exclude. |
| `ExtraArguments` | — | Extra CLI arguments appended to argv (e.g. `--match-path`, `--flatten-allof`, `--allow-external-refs=false`). Contract-altering flags (`--format`, `--fail-on`, `--composed`, ignore/severity files, `--template`, `--color`) are rejected. |
| `TimeoutSeconds` | `300` | Per-run execution bound. Exceeding it is an infrastructure failure. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/findings bounds; excess is reported as truncated. |

An unset `BaseSpec`/`Spec`, a non-local pointer (URL, absolute path, `..` escape,
scheme prefix, non-document extension), or identical pointers are deterministic
configuration failures. Two documents that fail `oasdiff validate` are coverage
unavailable — the scan never runs.

## Default scope

By default, the auditor runs `oasdiff breaking <BaseSpec> <Spec> --format json
--fail-on ERR` at the repository root over exactly the two configured documents:
the tool never walks the repository, so vendored specs, generated clients, and
upstream modules cannot produce findings that train operators to ignore auditor
output — which is why no `ExcludePaths` default ships. Both specs are required
with no guessed default because document layout varies per repository and any
invented default risks a silent no-op diff. A comparison with no breaking changes
passes; a document that is not a valid OpenAPI spec fails closed in validation,
so enabling this auditor on misconfigured pointers is a loud misconfiguration,
never a silent skip.
