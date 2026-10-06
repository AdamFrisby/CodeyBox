# CodeyBox: Roslynator C# Static Analysis Auditor

Auditor plugin wrapping [Roslynator](https://github.com/dotnet/roslynator)
(`roslynator analyze`, from `roslynator.dotnet.cli`): it analyzes the
audited repository's MSBuild projects and reports each diagnostic as an
audit finding with the diagnostic id (e.g. `CS0219`, `CA1852`) and
`file:line` location. C# static analysis — compiler diagnostics plus the
analyzers enabled for the analyzed projects.

Each run is a single tool invocation: `roslynator analyze` streams its
SARIF report to stdout (`--output /dev/stdout --output-format sarif`,
console chatter suppressed with `--verbosity quiet`) for parsing. A scan
failure (missing binary, unloadable project, unparseable output) is
infrastructure — never a pass.

## What it reports

- One finding per roslynator result. The title carries the rule id and the
  first line of the message; the description carries the tool, rule,
  tool-reported level, location, and the full message. `Location` is
  `path:startLine`; artifact URIs are absolute `file://` paths as the tool
  reports them (see the absolute-location caveat below).
- **Gate behaviour: hybrid / severity-driven — not blocking on every
  finding.** Roslynator SARIF levels go through a declared map, never raw:
  `error` (and `fatal`, `critical`, `high`, `fail`, `failure`) →
  `Error` (fails the audit); `warning` (and `warn`, `medium`,
  `moderate`) → `Warning` (advisory); `note`, `none`, `info`,
  `informational`, `information`, `suggestion`, `hint`, `low` → `Info`
  (informational); anything unrecognised → `Warning`.
  `MinimumSeverity` can only drop findings, so the intended way to harden
  the gate is lowering `MinimumSeverity` to `warning`/`error` — the
  tool-side `--severity-level` floor moves with it (see Configuration).
- The tool-side severity floor (`--severity-level`) is derived from
  `MinimumSeverity`: `info` → `--severity-level info` (report everything
  the tool reports by default), `warning` → `--severity-level warning`,
  `error` → `--severity-level error` (verified: `--severity-level error`
  drops a `CS0219` warning to `0 diagnostics found`, exit `0`). An
  operator `--severity-level` in `ExtraArguments` overrides the derived
  value.

## What it cannot see

- **Clean trees produce no report.** Roslynator only writes the report file
  when it has diagnostics: a clean analysis exits `0` with the console log
  (`0 diagnostics found`) on stdout. The auditor reads that as zero
  findings (a pass) — the exit code is the verdict, not the parser.
- **Repositories without an analyzable project.** `roslynator analyze`
  loads MSBuild projects from the working directory (or from
  `ProjectPath`); with none it exits `2` (`Could not find MSBuild project
  or solution file`) and the run is infrastructure (deterministic), not a
  pass. Trees whose NuGet packages are not restored likewise fail closed
  as infrastructure — analysis needs the packages (and a .NET SDK) in the
  sandbox.
- **Findings under excluded prefixes, when reported relatively.**
  `ExcludePaths` is a finding filter — roslynator still analyzes those
  files, but findings under `vendor/`, `third_party/`, `node_modules/`,
  `obj/`, `bin/`, `artifacts/`, `dist/`, `build/`, `out/`, `coverage/` are
  dropped. Override `ExcludePaths` to re-include them. `obj/` and `bin/`
  matter here: analysis sees generated files beside the sources, and
  findings located there are build-output noise. Tool-imposed limit:
  roslynator reports absolute `file://` artifact URIs, so the shared SARIF
  parser preserves sandbox-absolute paths and a repo-relative prefix entry
  cannot match them. The defaults stay (they filter any relative paths and
  document intent); narrow the scan itself with the tool's own `--include`
  / `--exclude` globs in `ExtraArguments`.
- **Suppressed diagnostics.** Roslynator honors suppression authored
  inside the audited repository — `#pragma warning disable`,
  `[SuppressMessage]`, `.editorconfig` severity lines, `NoWarn` — and the
  audit subject writes that repository. No roslynator flag defeats all of
  these (verified: `--report-suppressed-diagnostics` does not resurface
  `#pragma`-disabled compiler diagnostics), so repo-authored suppression
  narrows what the tool reports. Operators who need a ruleset independent
  of the repository restrict the reported set via `IncludedRules`
  (finding-level exact-match filter) or pass `--supported-diagnostics` /
  `--severity-level` in `ExtraArguments`.
- **Analyzer packages the repository does not reference.** By default the
  scan runs the analyzers enabled for the analyzed projects (SDK analyzers
  plus any referenced packages such as `Roslynator.Analyzers`). It injects
  nothing else — unless you configure `AnalyzerAssemblies` (or pass
  `--analyzer-assemblies` in `ExtraArguments`), which loads additional
  analyzer DLLs into the analysis; that is the Unity preset below.
  Findings are whatever the project's own analysis surface plus the
  configured assemblies report.

## Unity analyzer preset (Microsoft.Unity.Analyzers)

Unity-specific C# diagnostics ([Microsoft.Unity.Analyzers](https://github.com/microsoft/Microsoft.Unity.Analyzers),
rules `UNT*` such as `UNT0001` *empty Unity message*) run through this same
auditor — there is no separate Unity assembly or parser. The preset is two
knobs used together:

1. `ProjectPath` — the Unity-generated `.csproj`/`.sln` with the correct
   Unity references (Unity with Visual Studio Tools for Unity ≥ 4.3.2
   auto-includes the analyzers via `<Analyzer Include="..." />` in generated
   projects).
2. `AnalyzerAssemblies` — repository-relative `.dll` path(s) of the pinned
   analyzer package, e.g. `tools/analyzers/Microsoft.Unity.Analyzers.dll`
   from NuGet package `Microsoft.Unity.Analyzers` **1.28.0** (latest stable
   verified 2026-10-06 via the NuGet flatcontainer index). Declare the same
   DLL even when the generated project already Includes it, so coverage stays
   verifiable instead of trusting repository-authored project content.

The auditor validates each entry (relative, no `..`, must end `.dll`, at
most 16), probes it present in the audited tree, and passes it as structured
`--analyzer-assemblies <dll>` argv (one pair per assembly, before the
project positional). When `ExtraArguments` already supplies
`--analyzer-assemblies`, the knob defers to the operator spelling. Unity
diagnostics then flow through the same SARIF parser and severity map as
every other diagnostic (`UNT0001` warnings are advisory; only
error-severity diagnostics fail the audit).

Coverage is explicit, never silent: requesting `UNT*` via `IncludedRules`
without an analyzer assembly, enabling the preset without `ProjectPath`, or
naming an assembly absent from the tree fails closed as deterministic
infrastructure (`AuditUnavailableException`) — missing Unity coverage is
never a pass.

Limits: the auditor reports analyzer diagnostics; it does not perform full
Unity compilation or editor validation, and needs no Unity account, license,
or editor provisioning. A missing generated project, analyzer DLL, or Unity
reference set is coverage-unavailable by design.
- **Anything an `--output` / `--output-format` / `--verbosity` override
  breaks.** The parser reads the SARIF document from stdout. Never pass
  `--output`/`-o`, `--output-format`, or `--verbosity`/`-v` via
  `ExtraArguments`: a diverted report file (or any other format, or
  console chatter interleaved before the report) leaves stdout without a
  leading SARIF document and the run fails closed as infrastructure.

## Exit codes and failure classification

Roslynator's convention (verified against 1.0.0 — do not assume the
eslint/gitleaks convention holds here):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Analyzed clean — no report file is written; stdout carries only the console log | Pass (zero findings). If stdout carries a SARIF payload anyway (operator `--return-success-on-diagnostics`), it still parses — the exit code alone can never silence the gate |
| `1` | Diagnostics found — SARIF report on stdout | Verdict (`Passed = false` when any finding maps to `Error`) |
| `1` without a SARIF payload | Diagnostics run that lost its report | Infrastructure (`AuditUnavailableException`) — fails closed, never a pass |
| `2` | Could not run (verified: unknown flag; missing project file — `Project or solution file not found`; no project under the working directory — `Could not find MSBuild project or solution file`) | Infrastructure (`AuditUnavailableException`) |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

A missing `roslynator` is always an infrastructure failure naming the tool
— never a passing audit.

## Version pinning

The auditor is pinned to **roslynator `1.0.0.0` as reported by
`roslynator --version`** (`ExpectedVersion` in scoped config). That is the
version string printed by the `roslynator.dotnet.cli` **NuGet package
`1.0.0`** — the install version and the reported version differ in the
fourth component, so pin the reported one. Diagnostics change between
releases, so an unpinned tool would change findings under you: the auditor
probes `roslynator --version` before every run and reports an
infrastructure failure on any other version.

The tool requirements are declared **verify-only** — no `AptPackage`:
roslynator ships as a .NET global tool, not a distro package, and only a
pinned install carries the version this auditor was verified against. The
`dotnet` requirement (a .NET SDK for MSBuild project load) is likewise
verify-only. Provision both in your sandbox baseline (the SDK must be new
enough to load the audited projects):

```sh
# baseline bake step — pinned tool plus a PATH shim
dotnet tool install -g roslynator.dotnet.cli --version 1.0.0
export PATH="$PATH:$HOME/.dotnet/tools"
roslynator --version   # must print 1.0.0.0
dotnet --list-sdks     # a .NET SDK (8+) must be installed for project load
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.roslynator"],
      "Enabled": ["codeybox.roslynator"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.roslynator" }
```

The `roslynator` / `dotnet` tool requirements are only contributed to
baseline provisioning while the plugin is enabled.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.roslynator`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `1.0.0.0` | Pinned `roslynator --version` output; a different installed version fails closed as infrastructure. Set this to the release you provisioned (the reported version, not the NuGet package version). |
| `ProjectPath` | `null` | Repository-relative project/solution path (e.g. `src/App.sln`) analyzed instead of working-directory discovery. Must be relative, without `..` segments, ending in `.sln`, `.slnx` or `.csproj`; a missing file fails closed as deterministic infrastructure. Required by the Unity analyzer preset. |
| `AnalyzerAssemblies` | — | Additional analyzer assemblies as comma-separated repository-relative `.dll` paths (e.g. `tools/analyzers/Microsoft.Unity.Analyzers.dll` for the Unity preset). Validated (relative, no `..`, `.dll` only, at most 16), presence-probed, and passed as structured `--analyzer-assemblies` argv before the project positional; defers when `ExtraArguments` already supplies the flag. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`) — and lower the tool-side `--severity-level` floor to match, unless `ExtraArguments` supplies `--severity-level`. |
| `IncludedRules` / `ExcludedRules` | — | Exact diagnostic ids to keep/drop (e.g. `CS0219`, `CA1852`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `obj/`, `bin/`, `artifacts/`, `dist/`, `build/`, `out/`, `coverage/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan (and only matches repository-relative locations — see above). Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args and the project positional (never via a shell). Useful for `--include`/`--exclude` globs, `--ignored-projects`, `--supported-diagnostics`, `--analyzer-assemblies`, or `--return-success-on-diagnostics` (harmless: the SARIF payload still parses). Never pass `--output`/`-o`/`--output-format`/`--verbosity`/`-v` (would divert or replace the SARIF report the parser expects, or interleave console chatter before it, and break the run into infrastructure failure). |
| `TimeoutSeconds` | `600` | Per-run bound (MSBuild load plus whole-tree analysis over a full repository takes minutes). Exceeding it is infrastructure, not a pass. Discovery and version probes share a 30s cap. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. Large repositories can exceed 1 MiB of SARIF — raise the former (up to 64 MiB) rather than wondering where findings went. |

## Default scope

`roslynator analyze` run in the repository root — the tool discovers and
analyzes the MSBuild projects it finds there; pin `ProjectPath` when the
tree holds several solutions. On top of that, findings under vendored
(`vendor/`, `third_party/`, `node_modules/`) and generated (`obj/`,
`bin/`, `artifacts/`, `dist/`, `build/`, `out/`, `coverage/`) prefixes are
dropped by default: violations there belong to upstream packages or
compiler/build output, not the change under audit — reporting them produces
noise that trains operators to ignore the auditor. Re-include a prefix by
overriding `ExcludePaths`; narrow the analyzed tree with the tool's own
`--include`/`--exclude` globs in `ExtraArguments`.
