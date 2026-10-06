# CodeyBox: ILVerify Compiled-Assembly Auditor

Auditor plugin wrapping [ILVerify](https://github.com/dotnet/runtime/tree/main/src/coreclr/tools/ILVerify)
(`ilverify`, from the [`dotnet-ilverify`](https://www.nuget.org/packages/dotnet-ilverify)
NuGet package): it verifies the managed IL of the candidate-produced
assemblies you name against the ECMA-335 rules and reports each
verification failure as an audit finding carrying the verifier error code
(e.g. `StackUnexpected`) and the `assembly` location. Compiled-assembly
correctness — bad stack shapes, type mismatches, invalid tokens — not
source style.

Each run is a single tool invocation:

```
ilverify ./artifacts/publish/App.dll -r ./artifacts/publish/*.dll
```

Candidate assemblies ride as positionals; every configured reference rides
as its own `-r` entry. A scan failure (missing binary, missing candidate
or reference, unparseable output) is infrastructure — never a pass.

## What it reports

- One finding per `[IL]: Error …` line. The title carries the verifier
  error code and the failure message; the description carries the tool,
  code, tool-reported severity, location (assembly, type, method, IL
  offset), and the full message. `Location` is the repository-relative
  assembly path the scan was given (e.g. `artifacts/publish/App.dll`); IL
  offsets are not source lines, so no line number is fabricated.
- **Gate behaviour: blocking on any verification failure.** ILVerify has
  no severity vocabulary — every reported error is a proven IL defect —
  so each maps to `Error` (fails the audit). A clean run passes with zero
  findings. `MinimumSeverity` can only drop findings, so setting it to
  `error` keeps the gate while `warning`/`info` would silence it.
- `IncludedRules` / `ExcludedRules` filter on verifier error codes by
  exact match (e.g. `StackUnexpected`).

## What it cannot see

- **Anything outside the configured assemblies.** There is no discovery:
  the scan verifies exactly `Assemblies`. An assembly you do not name is
  out of scope, not covered — and an empty `Assemblies` value fails closed
  as deterministic infrastructure rather than passing vacuously.
- **Dependencies outside the configured closure.** ILVerify resolves
  references only from `-r` entries; it never infers a closure from the
  host runtime. Point `ReferenceAssemblies` at the complete set matching
  the target framework (typically the build output beside the candidates,
  e.g. `artifacts/publish/*.dll`, plus any framework facades the build
  does not emit). A loader failure — exit `1` (`Error: Assembly or module
  not found: …`), or a `FileLoadErrorGeneric` / failed-to-load line on
  exit `2` — is an incomplete closure, i.e. infrastructure: never a pass,
  never a code defect.
- **Missing binaries.** A configured candidate or literal reference that
  is not in the worktree, or a `ReferenceAssemblies` glob that matches
  nothing, fails closed as deterministic infrastructure. Build the
  candidate before the audit so the configured paths exist.
- **Findings under excluded prefixes.** `ExcludePaths` is a finding
  filter — `vendor/`, `third_party/`, `node_modules/` by default. Build
  output (`bin/`, `artifacts/`, …) is deliberately NOT excluded:
  candidate assemblies live there, so excluding it would silence the
  auditor entirely. Override `ExcludePaths` to re-include a path, or to
  drop a build-output subtree whose failures you own elsewhere.
- **Source-level meaning.** The auditor sees IL, not source: a finding
  names the assembly, type, method, and IL offset. Mapping that back to a
  source line is the rework step's job (rebuild with symbols and compare).
- **Anything an `ExtraArguments` narrowing flag removes.** `--include` /
  `--exclude` regexes and `--ignore-error` patterns narrow what the tool
  reports; they are operator-owned scope changes, not verification. Never
  pass `-h`/`--help`/`-v`/`--version` (they replace the report with help
  or banner text and the run fails closed as infrastructure).

There is no repository-controlled suppression surface for IL
verification: no suppression file, attribute, or config in the audited
repository can hide a verification failure from the tool, so nothing in
the diff can blind this gate short of removing the assembly from
`Assemblies` (visible in operator config, not in the diff).

## Exit codes and failure classification

ILVerify's convention (verified against `dotnet-ilverify 10.0.12` — do
not assume the eslint/gitleaks convention holds here):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Every input verified — one `All Classes and Methods in <assembly> Verified.` line per input on stdout | Pass (zero findings) |
| `0` without the `Verified.` marker | Hijacked or truncated invocation (e.g. `--help` in `ExtraArguments`) | Infrastructure (`AuditUnavailableException`) — fails closed, never a pass |
| `2` | Verification failures — `[IL]: Error …` lines plus a summary on stdout | Verdict (`Passed = false`; every failure maps to `Error`) |
| `2` without `[IL]: Error` lines, or with loader-error lines (`FileLoadErrorGeneric`, `Failed to load assembly …`) | Incomplete reference closure or unreadable input | Infrastructure (`AuditUnavailableException`) — never a pass, never a code defect |
| `1` | Verifier could not start (`Error: Assembly or module not found: …` plus a `VerifierException` stack) | Infrastructure (`AuditUnavailableException`) |
| `134` | Unhandled tool exception (missing input file, unknown flag) | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

## Version pin

`ExpectedVersion` defaults to `10.0.12` (the `dotnet-ilverify 10.0.12`
package reports `10.0.12-servicing…` via `ilverify --version`). A missing
binary, an unrecognised version string, or any other installed version
fails closed as infrastructure. Set `ExpectedVersion` to the release you
provisioned.

The sandbox baseline also needs a .NET runtime (9 or later) beside
`ilverify`: the tool is framework-dependent, and the reference closure
normally comes from a .NET shared framework or SDK.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.ilverify`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `Assemblies` | — (required) | Comma-separated repository-relative candidate assembly paths (e.g. `artifacts/publish/App.dll, artifacts/publish/Worker.dll`). Explicit paths only (no globs), ending in `.dll`/`.exe`, at most 64. Every entry must exist. Unset, overlong, malformed, or missing entries fail closed as deterministic infrastructure. |
| `ReferenceAssemblies` | — (required) | Comma-separated repository-relative reference paths or `*`/`?` globs (e.g. `artifacts/publish/*.dll`). At most 32 entries. Literal entries must exist; glob entries must match at least one file. Each entry becomes its own `-r` argument. |
| `ExpectedVersion` | `10.0.12` | Pinned `ilverify --version` package release line (the banner's `-servicing…` suffix is stripped before comparison); a different installed release fails closed as infrastructure. Set this to the NuGet package version you provisioned. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`). Every ILVerify failure maps to `error`, so only `error` keeps the gate. |
| `IncludedRules` / `ExcludedRules` | — | Exact verifier error codes to keep/drop (e.g. `StackUnexpected`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. Build output is intentionally not excluded. |
| `ExtraArguments` | — | Extra argv appended after the assemblies and `-r` entries (never via a shell). Useful for `-t`/`--tokens`, `--statistics`, `-s`/`--system-module`, or `--include`/`--exclude`/`--ignore-error` narrowing. Never pass `-h`/`--help`/`-v`/`--version` (replaces the verifiable report and breaks the run into infrastructure failure). |
| `TimeoutSeconds` | `300` | Per-run bound (IL verification over a bounded assembly set takes seconds). Exceeding it is infrastructure, not a pass. Probes share a 30s cap. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. A truncated report fails closed through the parser rather than passing. |

## Default scope

Exactly the configured `Assemblies`, verified against exactly the
configured `ReferenceAssemblies` — no discovery, no host-runtime
inference. On top of that, findings under `vendor/`, `third_party/`,
`node_modules/` are dropped by default: problems there belong to upstream
packages, not the change under audit.

## Setup (operator)

The plugin is disabled by default and does nothing until enabled. To use
it (explicit later operator setup — nothing here is live):

1. Provision the baseline: `dotnet-ilverify` at the pinned version
   (`dotnet tool install -g dotnet-ilverify --version 10.0.12`) plus a
   .NET runtime (9+), via `CodeyBox:MultipassExtraRuncmd` /
   `CodeyBox:Incus:ExtraRuncmd` or `ExecutableProvisions`.
2. Enable the plugin: add `codeybox.ilverify` to
   `CodeyBox:Plugins:Enabled`.
3. Configure the scope: set
   `CodeyBox:Plugins:codeybox.ilverify:Assemblies` to the
   candidate-produced assemblies and `:ReferenceAssemblies` to the
   complete closure (a build step that stages candidates plus
   dependencies — e.g. publish output — into one worktree directory
   keeps both lists trivial).
4. Keep `ExpectedVersion` in step with the provisioned tool release.
