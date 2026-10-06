# CodeyBox: glslang Shader Validation

Auditor plugin validating configured standalone GLSL/ESSL shader targets
with [glslangValidator](https://github.com/KhronosGroup/glslang): it compiles
the configured files with `glslangValidator --target-env <env> [-S <stage>] …`
and reports each `ERROR`/`WARNING` diagnostic as an audit finding with the
asset path and line. Standalone GLSL/ESSL validation only.

## What it reports

- One finding per `ERROR`/`WARNING` diagnostic line. The title carries the
  asset location and the first line of the message (e.g.
  `shaders/water.vert:12: 'foo' : undeclared identifier`); the description
  carries the tool, rule (`glslang/validation`), tool-reported level,
  location, and the full message.
- **Gate behaviour: hybrid / severity-driven — not blocking on every
  finding.** Level `ERROR` maps to `Error` and fails the audit; `WARNING`
  maps to `Warning` and is advisory. `MinimumSeverity` only drops findings,
  it never raises them — there is no mode in which a warning fails the
  audit. Set `MinimumSeverity: error` for an errors-only gate.
- **Diagnostics in included files.** A `#include`d file's diagnostics are
  reported with the included file's path (relativized the same way);
  narrow them with `ExcludePaths` if they are not the change under audit.
- **Diagnostics without an asset location** (the tool's shader-index form
  `ERROR: 0:12: …`, which carries a line number but names no file) are
  reported as path-less findings so no reported diagnostic is ever dropped.

## What it cannot see

- **Only configured files.** The auditor never walks the tree: every
  validated file is an explicit `ShaderTargets` entry resolved inside the
  audited worktree. An empty target list, a missing target, or an empty
  target file is an infrastructure failure, not a pass.
- **One program per audit.** All targets are compiled and linked together in
  a single `glslangValidator` invocation as one shader program (the tool's
  native multi-file mode). Configure the stages of one program (e.g. a
  `.vert` plus its `.frag`); targets that cannot link as one program
  (independent same-stage entry points, mismatched stage interfaces) fail
  closed as infrastructure with guidance — never as findings against the
  diff, never as a pass.
- **Stages are explicit.** Each target resolves its stage from an explicit
  `path:stage` suffix first, a canonical stage extension second
  (`.vert`, `.tesc`, `.tese`, `.geom`, `.frag`, `.comp`, `.mesh`, `.task`,
  ray-tracing stages), and `DefaultStage` last. A target with no stage from
  any source (notably a bare `.glsl` file with no `DefaultStage`) is a
  deterministic configuration error. A single invocation carries one `-S`
  flag, so a mixed-stage target set additionally requires every target to
  carry a canonical stage extension.
- **No SPIR-V output.** Validation only: `-V`/`-o` are never passed, no
  `.spv` is written, and nothing in the repository is modified. Validating
  emitted SPIR-V binaries is a separate concern (a `spirv-val` backend on
  this same shared family).
- **No Unity ShaderLab/variant, HLSL, or runtime-rendering coverage.**
  `.shader`, `.hlsl`/`.fx`, and other non-GLSL sources are rejected at
  configuration time. ESSL (`#version 300 es` and friends) needs no flag —
  the shading-language version is read from the file contents.
- **No preprocessor/include configuration.** Any `ExtraArguments` entry is
  rejected deterministically: `-D`/`-I`/`-U`, `-E` (preprocess only), `-o`,
  `--stdin`, and every other flag would mask validation, redirect output, or
  widen scope — and a non-flag extra would ride along as an unvalidated file
  operand outside the `ShaderTargets` contract. The supported surface is
  targets, stage, and target environment. Shaders that only compile with
  external defines must make their requirements explicit in the audited
  sources or stay out of this auditor's scope.
- **Repository-controlled tool configuration is never honored.** A `.conf`
  entry is rejected at configuration time rather than passed to the
  validator, where it would silently reprogram the validation.
- **More than `MaxFindings` diagnostics.** Findings beyond `MaxFindings`
  (default 1000) are dropped and the truncation is reported in the raw
  output.

## Exit codes and failure classification

glslangValidator's convention (checked against the validator contract for
the pinned release — **not** assumed from the common table):

| Exit | Meaning | Classification |
|---|---|---|
| `0`, no diagnostics | Ran valid | Verdict (pass — inputs were verified present and nonempty first) |
| `0` with diagnostics | Ran, advisory warnings | Verdict (advisory unless an `ERROR` line is present) |
| `1` with `ERROR`/`WARNING` lines | Ran with errors | Verdict (`Passed = false` — `Error` findings fail) |
| `1` without diagnostics | Could not run: missing input, bad flags — usage text, never a diagnostic line | Infrastructure (`AuditUnavailableException`) |
| program-level link failure, no per-asset diagnostic | Targets do not link as one program | Infrastructure with guidance — configure one linkable program per audit |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `glslangValidator` is always an infrastructure failure naming the
tool — never a passing audit.

## Version pinning

The auditor is pinned to **glslang `14.3.0`** (`ExpectedVersion` in scoped
config — `glslangValidator --version` prints `Glslang Version: 14.3.0` as
its first version token, which the shared extraction pins). A scanner's
checks change between releases, so an unpinned tool would change findings
under you: the auditor probes the version before every run and reports an
infrastructure failure on any other version.

The tool requirement declares **`AptPackage = "glslang-tools"`**, so
baseline provisioning installs it via apt **only when this plugin is
enabled** (and always verifies its presence). The distro package tracks the
distro default release, which may differ from the pin — keep the
provisioned release and `ExpectedVersion` in step (or provision the exact
upstream release binary): after a baseline change that moves
glslangValidator, update `ExpectedVersion` to the new release, otherwise
the auditor fails closed until you do. To re-verify a new release, run
`glslangValidator --version` plus clean/error invocations over
`--target-env` and `-S` and confirm the diagnostic shape above before
updating the pin.

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates, and baseline provisioning installs/verifies `glslangValidator` only
in that state:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.glslang"],
      "Enabled": ["codeybox.glslang"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.glslang" }
```

Operator setup is explicit and later: provision the pinned release into the
sandbox baseline (apt `glslang-tools` or the versioned upstream binary via
`CodeyBox:MultipassExtraRuncmd` / `CodeyBox:Incus:ExtraRuncmd` or
`ExecutableProvisions`), set `ExpectedVersion` to match, and configure
`ShaderTargets` (plus `DefaultStage`/`TargetEnvironment` as needed). Until
then the auditor is inert: nothing is provisioned, no audit runs, and no
notification or work-sync path references it.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.glslang`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `14.3.0` | Pinned glslang release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `ShaderTargets` | — (required) | Comma-separated repo-relative shader files, each `path` or `path:stage` (e.g. `shaders/water.vert,shaders/common.glsl:frag`). At least one required; duplicates are folded; more than 128 entries fails closed. |
| `DefaultStage` | — | Fallback stage (`vert`, `frag`, …) for targets with no suffix and no canonical extension. Required when any target (e.g. a bare `.glsl` file) otherwise resolves to no stage. |
| `TargetEnvironment` | `vulkan1.0` | Shader target environment passed to `--target-env` (e.g. `vulkan1.3`, `opengl`). Always passed explicitly. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). `error` gives an errors-only gate; nothing escalates warnings to failures. |
| `IncludedRules` / `ExcludedRules` | — | Exact rule ids to keep/drop (the only rule id is `glslang/validation`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Setting it replaces the default list. |
| `ExtraArguments` | — | No supported spelling: any entry fails closed (flag-looking extras could mask validation or widen scope; non-flag extras would ride as unvalidated file operands outside the `ShaderTargets` contract). |
| `TimeoutSeconds` | `300` | Per-run bound (probes share it under a 30 s cap). Exceeding it is infrastructure, not a pass. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. |

The auditor runs under `AuditCapabilities.None` (no agent credentials, no
network): anything the tool cannot resolve locally (missing includes,
network imports) fails closed as infrastructure rather than silently
passing.
