# CodeyBox: Buf Protobuf Breaking-Changes Auditor

Auditor plugin wrapping [`buf breaking`](https://buf.build/docs/breaking/)
(`buf`): it compares the Protobuf schema in the audited working tree against
a baseline (by default the merge-base of `HEAD` and the work item's base
branch) and reports wire, JSON, and generated-source incompatibilities as
CodeyBox audit findings.

## What it reports

- One finding per `buf breaking` violation, emitted with
  `--error-format=json` (one JSON object per line). The title carries the
  rule identifier (e.g. `FIELD_SAME_TYPE`, `FILE_NO_DELETE`,
  `ENUM_VALUE_NO_DELETE`); the description carries the tool, rule,
  tool-reported severity, location, and problem message.
  `Location` is `path:startLine` where the tool supplies them.
- **Gate behaviour: blocking.** `buf breaking` reports no severity per
  violation — every emitted line is a compatibility break — so every
  finding maps to `AuditSeverity.Error` and **fails the audit**
  (`Passed = false`). There is no advisory mode; narrow scope with
  `IncludedRules` / `ExcludedRules`, `ExcludePaths`, or an operator-owned
  `ConfigPath` instead.

## What it cannot see

- **Non-Protobuf changes.** Only `.proto` files are in scope; application
  code, generated stubs, and descriptors are not compared.
- **Compatible evolution.** Adding fields, messages, enum values, or
  services is wire-safe and produces no findings — only breaks do.
- **Excluded paths.** Files under paths configured in `ExcludePaths`
  (defaults to `vendor/`, `third_party/`, `node_modules/`) are filtered
  out of findings. Findings without a file path (e.g. `FILE_NO_DELETE`,
  which names the deleted file only inside its message) cannot be
  path-excluded and always surface.
- **Rule tuning in the audited repo.** The breaking category (`FILE` /
  `PACKAGE` / `WIRE_JSON` / `WIRE`) and per-rule `except` / `ignore` /
  `ignore_only` tuning come from the repository's own `buf.yaml` (or the
  file `ConfigPath` points at). A change set can therefore soften the
  check applied to it; operators who need the gate independent of
  repo-authored tuning should pin the rule set in an operator-owned config
  file via `ConfigPath`. Post-hoc rule filtering through `IncludedRules`
  / `ExcludedRules` is applied by the audit host, outside the subject's
  reach.
- **Remote baselines when offline.** `--against` inputs that name a remote
  Git URL, a BSR module, or `--against-registry` need network egress to
  those hosts from the audit sandbox; without it the run fails closed as
  infrastructure.

## Exit codes and failure classification

`buf`'s exit convention (verified against buf 1.73.0 — it is **not** the
common 0/1/2 convention):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Ran clean, no breaking changes (empty stdout) | Verdict (Pass) |
| `100` | Ran and found breaking changes (one JSON violation per stdout line) | Verdict (`Passed = false`, findings reported) |
| `100` (no JSON) | Findings exit with an unparseable or empty report — contradicts the output contract | Infrastructure (`AuditUnavailableException`) |
| `1` | Could not run (missing `--against`, unresolvable baseline, no `.proto` files, usage error) with a plain-text `Failure: …` diagnostic | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure (`AuditUnavailableException`) |
| anything else | Unknown convention or signal termination | Infrastructure (`AuditUnavailableException`) |

## Version pinning

The auditor is pinned to **buf `1.73.0`** (`DefaultExpectedVersion`).
A scanner's rule definitions and report shape evolve between releases, so
an unpinned tool would change findings across runs. The auditor probes
`buf --version` before scanning; any version mismatch or missing binary
fails closed as infrastructure. Operators running a different pinned
build configure `ExpectedVersion` in scoped configuration.

The tool requirement is declared **verify-only** (no `AptPackage`): no
distro apt package carries a pinned `buf`. Provision the pinned release
in your sandbox baseline from the upstream release tarball:

```sh
# Baseline provisioning bake step (x86_64 example)
curl -fsSL -o /tmp/buf.tar.gz \
  https://github.com/bufbuild/buf/releases/download/v1.73.0/buf-Linux-x86_64.tar.gz
curl -fsSL -o /tmp/buf-sha256.txt \
  https://github.com/bufbuild/buf/releases/download/v1.73.0/sha256.txt
(cd /tmp && grep 'buf-Linux-x86_64.tar.gz' buf-sha256.txt | sha256sum -c -)
tar -xzf /tmp/buf.tar.gz -C /tmp/bufstage
install -m 0755 /tmp/bufstage/buf/bin/buf /usr/local/bin/buf
buf --version   # must print 1.73.0
```

## Enabling

The plugin is **disabled by default**. It loads only when allowlisted and
enabled:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.buf"],
      "Enabled": ["codeybox.buf"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.buf" }
```

When disabled, the plugin's `buf` (and auxiliary `git`) tool requirement
contributes nothing to sandbox baseline provisioning — no presence probe
and no install step mention `buf`.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.buf`, hot-reloadable per run:

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `1.73.0` | Pinned buf release. A different installed version fails closed as infrastructure. |
| `Against` | merge-base of `HEAD` and the work item's base branch (as `.git#ref=<sha>`) | Explicit baseline input passed to `--against`: any buf input — a `.git#branch=` / `.git#tag=` / `.git#ref=` fragment, a directory, a BSR module, an archive, or an image. |
| `AgainstRegistry` | `false` | When `true`, pass `--against-registry` (compare every named workspace module against its latest BSR commit) instead of `--against`. |
| `ConfigPath` | `null` | Operator-owned buf configuration file passed to `--config`. If omitted, `buf` discovers `buf.yaml` / `buf.work.yaml` from the repository itself. |
| `MinimumSeverity` | `info` | No-op for this auditor: every buf finding is `error`, so no threshold can narrow them. Use `IncludedRules` / `ExcludedRules` / `ExcludePaths` instead. |
| `IncludedRules` / `ExcludedRules` | — | Exact buf rule identifiers to include or drop (e.g. `FIELD_SAME_TYPE`, `FILE_NO_DELETE`). Applied by the host after the scan. |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repository-relative paths dropped from findings. Setting replaces the default list. |
| `ExtraArguments` | — | Extra CLI arguments appended to argv (e.g. `--exclude-path`, `--path`, a positional input directory). |
| `TimeoutSeconds` | `300` | Per-run execution bound. Exceeding it is an infrastructure failure. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/findings bounds; excess is reported as truncated. |

Exactly one baseline source may be configured: `Against`,
`AgainstRegistry`, or an `--against*` flag in `ExtraArguments`. Two at
once is a deterministic configuration failure.

## Default scope

By default, the auditor runs `buf breaking --against
.git#ref=<merge-base>` at the repository root with the working tree as
the input: buf's own workspace discovery decides coverage (every module
in `buf.yaml`, or the `.proto` files under the root when no config
exists), and findings under `vendor/`, `third_party/`, and
`node_modules/` are dropped. This keeps vendored `.proto` copies and
upstream modules from producing noise that trains operators to ignore
auditor output, while the merge-base baseline matches the pipeline's own
three-dot work diff — schema added to the base after the branch point is
not misread as removed. A repository without `.proto` files fails closed
with buf's diagnostic surfaced as infrastructure, so enabling this
auditor on a non-Protobuf project is a loud misconfiguration, never a
silent skip.
