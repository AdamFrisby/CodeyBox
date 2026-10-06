# CodeyBox: glTF Asset Validator

Auditor plugin wrapping the official Khronos [glTF-Validator](https://github.com/KhronosGroup/glTF-Validator)
(`gltf_validator` CLI): it validates changed `.gltf`/`.glb` assets and reports
structural/conformance findings by asset and JSON-pointer location as CodeyBox audit findings.

## What it reports

- One finding per validator issue. The title carries the stable issue code
  (e.g. `UNDEFINED_PROPERTY`, `NON_RELATIVE_URI`); the description carries the
  tool, rule, tool-reported severity, location, and problem message with its
  JSON pointer (e.g. `/accessors/0`) or GLB byte offset (e.g. `@20`) preserved
  verbatim at the head of the message. `Location` is the repo-relative asset path.
- **Gate behaviour: hybrid / severity-driven.** The validator classifies each issue:
  - `Error`: broken JSON, schema violations, invalid accessor/buffer data, unresolvable references.
    These map to `AuditSeverity.Error` and **block the audit** (`Passed = false`).
  - `Warning`: questionable but loadable content (e.g. non-relative URIs).
    These map to `AuditSeverity.Warning` and are **advisory** (do not block the audit).
  - `Information` / `Hint`: informational notes.
    These map to `AuditSeverity.Info` and are non-blocking.
  Operators adjust the gate with `MinimumSeverity`, `IncludedRules`/`ExcludedRules`
  (exact issue codes), or the validator's own per-code selection via an operator-owned
  `-c/--config` file passed in `ExtraArguments`.

## What it cannot see

- **Visual quality, runtime performance, or engine/import correctness.** The report proves
  structural conformance to the glTF 2.0 specification — not that an asset looks right,
  performs well, or that any particular engine or importer (including Unity) accepts it.
  Findings must never be read as any of those claims.
- **Assets outside the selection.** Only explicitly selected assets are validated:
  `Targets` when configured, otherwise bounded discovery (see below). Enumeration beyond
  `MaxAssets` fails closed instead of silently covering a subset.
- **Oversize assets.** Assets larger than `MaxAssetBytes` fail closed as infrastructure
  (the validator loads the whole asset into memory) instead of being skipped or scanned.
- **Excluded paths.** Findings under `ExcludePaths` (defaults `vendor/`, `third_party/`,
  `node_modules/`) are dropped post-scan.
- **Resource validation when disabled.** `ValidateResources=false` narrows coverage to
  JSON/schema conformance (buffer contents, accessor data, and image decoding are not
  checked); the combined raw output records the setting per run.

## Exit codes and failure classification

Verified against the upstream `lib/cmd_line.dart` (exit `1` on any error found _or_
usage failure; `0` only when the single input validated with no errors):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Scanned with no errors (warnings/infos/hints may still be reported) | Verdict (Pass, or advisory findings) |
| `1` | Errors found (JSON report on stdout) | Verdict (`Passed = false`, findings reported) |
| `1` (no report) | Usage failure (no input, more than one input, directory input with `--stdout`, unknown input) — version/usage banner on stderr, no report | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure (`AuditUnavailableException`) |
| anything else | Unknown convention or signal termination | Infrastructure (`AuditUnavailableException`) |

The discriminator is the report itself: an exit without a parseable
`{"issues":{"messages":[…]}}` document on stdout fails closed through the parser.
Reports flagged `truncated:true` by the validator are still reported; the flag is
surfaced in the per-asset raw-output header.

## Version pinning

The auditor is pinned to **glTF-Validator `2.0.0-dev.3.11`** (`DefaultExpectedVersion`,
the upstream `pubspec.yaml` version; the CLI banner prints
`glTF 2.0 Validator, version <release>`).
The CLI defines no `--version` flag, so the gate probes the bare binary (which prints
the version banner with usage text) and requires the token to be present and exactly
equal to the pin — a missing binary, an unrecognised banner, or a foreign release is
infrastructure naming the tool, never a pass. Operators running a different pinned
build set `ExpectedVersion` in scoped configuration.

The tool requirement is declared **verify-only** (no `AptPackage`): `gltf_validator`
ships as versioned upstream release archives (`gltf_validator-VERSION-PLATFORM`),
not a distro package. Provision the pinned release into the sandbox baseline, e.g.:

```sh
# Baseline provisioning bake step (Linux x64 example)
curl -fsSL -o /tmp/gltf_validator.tar.gz \
  https://github.com/KhronosGroup/glTF-Validator/releases/download/2.0.0-dev.3.11/gltf_validator-2.0.0-dev.3.11-linux64.tar.gz
tar -xzf /tmp/gltf_validator.tar.gz -C /usr/local/bin gltf_validator
gltf_validator   # prints "glTF 2.0 Validator, version 2.0.0-dev.3.11" with usage text
```

## Enabling

The plugin is **disabled by default** and in no active audit, notification, or
work-sync configuration. It loads only when allowlisted and enabled, which requires
explicit later operator setup (provision the binary above first):

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.gltf-validator"],
      "Enabled": ["codeybox.gltf-validator"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.gltf-validator" }
```

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.gltf-validator`, hot-reloadable per run:

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `2.0.0-dev.3.11` | Pinned glTF-Validator release. A different installed version fails closed as infrastructure. |
| `Targets` | — | Comma-separated repo-relative `.gltf`/`.glb` paths to validate. Unset → bounded discovery. Absolute paths, `..` segments, leading-dash names, URI schemes, non-glTF suffixes, directories, and symlinks are rejected deterministically. |
| `MaxAssets` | `25` (ceiling `500`) | Bound on assets validated per run (discovered or configured). Beyond the bound the run fails closed instead of covering a subset. |
| `MaxAssetBytes` | `134217728` (128 MiB; clamped to 4 KiB–2 GiB) | Per-asset size bound. Oversize assets fail closed as infrastructure. |
| `ValidateResources` | `true` | Validate buffer contents, accessor data, and images. `false` narrows coverage to JSON/schema conformance. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). |
| `IncludedRules` / `ExcludedRules` | — | Exact validator issue codes to include or drop (e.g. `NON_RELATIVE_URI`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repository-relative paths dropped from findings. Setting replaces the default list. |
| `ExtraArguments` | — | Extra CLI arguments appended after the asset positional (e.g. `-m`, `-c validator-config.yaml`). `--no-stdout` and `--absolute-path`/`-p` are rejected: the JSON report must ride stdout and locations must stay repo-relative. |
| `TimeoutSeconds` | `300` | Per-invocation execution bound (each asset gets the full bound). Exceeding it is an infrastructure failure naming the asset. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/findings bounds; the combined raw output and global finding list are truncated with a note. |

## Default scope

By default, the auditor discovers `*.gltf`/`*.glb` regular files with a bounded `find`
probe (pruning `.git`, `vendor`, `third_party`, `node_modules`, `dist`, `build`, `out`,
`coverage`), size-gates each asset, and validates them one by one with
`gltf_validator -o <asset>` — the CLI's `--stdout` report mode accepts exactly one
asset, so directory input (which would write `<asset>.report.json` files into the
audited tree) is never used. Findings under the default `ExcludePaths` are dropped
post-scan. Referenced buffers/images resolve inside the audit sandbox via the tool's
own relative-URI loading; remote or absolute URIs are never fetched and surface as
`NON_RELATIVE_URI` findings rather than being muted.
