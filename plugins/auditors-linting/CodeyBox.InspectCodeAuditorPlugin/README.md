# CodeyBox: ReSharper InspectCode Static Analysis Auditor

Auditor plugin wrapping [ReSharper InspectCode](https://www.jetbrains.com/help/resharper/InspectCode.html)
(`inspectcode`): it analyzes the audited repository's Visual Studio solution
and reports each inspection as an audit finding with the inspection id
(e.g. `NotAccessedVariable.Compiler`, `UnusedMember.Global`,
`.CSharpErrors`) and `file:line` location. C#/.NET static analysis only —
whatever the discovered or configured solution contains.

Each run is a single tool invocation: the analysis streams SARIF to stdout
for parsing. A scan failure (missing binary, no solution file, unparseable
output) is infrastructure — never a pass.

## What it reports

- One finding per InspectCode result. The title carries the rule id and the
  first line of the message; the description carries the tool, rule,
  tool-reported level, location, and the full message. `Location` is
  `path:startLine`; artifact URIs are solution-relative (see the
  multi-solution caveat below).
- **Gate behaviour: hybrid / severity-driven — not blocking on every
  finding.** InspectCode severities go through a declared map, never raw:
  `error` → `Error` (fails the audit); `warning` → `Warning` (advisory);
  `note`, `none`, `suggestion`, `hint`, `info` → `Info` (informational);
  anything unrecognised → `Warning`. `MinimumSeverity` can only drop
  findings, so the intended way to harden the gate is lowering
  `MinimumSeverity` to `warning`/`error` — the tool-side `-e` floor moves
  with it (see Configuration).
- The tool-side severity floor (`-e`) is derived from `MinimumSeverity`:
  `info` → `-e=INFO` (report everything), `warning` → `-e=WARNING`,
  `error` → `-e=ERROR` (verified: `-e=ERROR` returns only error-level
  results). An operator `--severity`/`-e` in `ExtraArguments` overrides the
  derived value.

## What it cannot see

- **Repositories without a solution.** InspectCode analyzes a solution, not
  a file tree. Without `SolutionPath` the auditor discovers the shallowest
  `*.sln`/`*.slnx` within three levels of the repository root; with no match
  the run is infrastructure (deterministic — it names `SolutionPath`), not a
  pass. SDK-style `.slnx` solutions are supported.
- **The non-selected solution.** When several solutions exist only the
  shallowest sorted-first is analyzed; the rest are invisible to the run.
  Multi-solution repositories should pin `SolutionPath` (or register one
  auditor instance per solution, each with its own `SolutionPath`).
- **Findings under excluded prefixes.** `ExcludePaths` is a finding filter —
  InspectCode still analyzes those files, but findings under `vendor/`,
  `third_party/`, `node_modules/`, `obj/`, `bin/`, `artifacts/`, `dist/`,
  `build/`, `out/`, `coverage/` are dropped. Override `ExcludePaths` to
  re-include them. `obj/` and `bin/` matter here: InspectCode analyzes
  generated files beside the sources (e.g. `AssemblyInfo.cs`,
  `*.g.cs`), and findings located there are compiler-output noise.
- **Suppressed inspections, by default.** The scan passes
  `--disable-settings-layers:SolutionShared;SolutionPersonal`, so
  repo-authored `*.sln.DotSettings` severities (including `DO_NOT_SHOW`)
  are inert — verified: a DotSettings demoting three inspections removed
  them from the report, and the flag restored all of them. Operators who
  deliberately trust repo-authored suppression set
  `TrustRepositorySuppression` in scoped config.
- **`.editorconfig` severity overrides.** `resharper_*=none` lines in the
  audited tree are still honored: the settings-layer flag does not cover
  EditorConfig, and there is no InspectCode switch that does. A repository
  that demotes inspections via `.editorconfig` weakens this audit; treat
  `.editorconfig` inspection lines in the audited diff as gate-relevant.
  For a fully operator-owned gate, pin an out-of-repo `SettingsPath`.
- **Build-coupled inspections, by default.** The scan passes `--no-build`:
  the audit sandbox has no network for a NuGet restore, so a default build
  would fail there. Some inspections (notably around source generators)
  need the build to fire; set `BuildSolution` when the sandbox image carries
  the .NET SDK and restored packages (or a self-contained tree).
- **Anything a `--format` override breaks.** The parser reads the default
  SARIF document from stdout. Never pass `--format`/`-f` or
  `--output`/`-o` via `ExtraArguments`: any other format (or a diverted
  output file) leaves stdout without SARIF and the run fails closed as
  infrastructure.
- **Solution-relative locations.** Artifact URIs resolve against the analyzed
  solution's directory. For a root-level solution that is the repository
  root; for a solution in a subdirectory (e.g. `src/App.sln`) locations are
  relative to that subdirectory. Prefer a root solution or `SolutionPath`
  accordingly.

## Exit codes and failure classification

InspectCode's convention (verified against 2026.2.2 — do not assume the
eslint/gitleaks convention holds here):

| Exit | Meaning | Classification |
|---|---|---|
| `0` | Analysis completed — clean or with issues; the verdict is in the SARIF document | Verdict (`Passed = false` when any finding maps to `Error`) |
| `1` | Could not run (verified: missing solution file — `Unable to find target solution file`) | Infrastructure (`AuditUnavailableException`) |
| `3` | Could not run (verified: solution with no files to inspect) | Infrastructure (`AuditUnavailableException`) |
| `0` with non-SARIF stdout | Console chatter or usage text instead of a report (e.g. an operator `--verbosity` override defeating `--verbosity=OFF`) | Infrastructure — the SARIF parser fails closed |
| `126` / `127` | Binary not executable or not found | Infrastructure |
| anything else | Unknown convention | Infrastructure (fails loud, never a pass) |

There is deliberately no separate "found something" exit: only `0` is
findings-producing. A missing `inspectcode` is always an infrastructure
failure naming the tool — never a passing audit.

## Version pinning

The auditor is pinned to **ReSharper Command Line Tools `2026.2.2`**
(`ExpectedVersion` in scoped config). Inspection implementations change
between releases, so an unpinned tool would change findings under you: the
auditor probes `inspectcode --version` before every run and reports an
infrastructure failure on any other version.

The tool requirement is declared **verify-only** — no `AptPackage`: the
Command Line Tools ship as a zip / .NET tool, not a distro package, and only
a pinned install carries the version this auditor was verified against.
Provision the pinned release in your sandbox baseline (needs the .NET SDK on
the image for analysis):

```sh
# baseline bake step — option A: official zip (provides `inspectcode` directly)
INSPECTCODE_VERSION=2026.2.2
curl -fsSL -o /tmp/rct.zip "https://download.jetbrains.com/resharper/dotUltimate.${INSPECTCODE_VERSION}/JetBrains.ReSharper.CommandLineTools.${INSPECTCODE_VERSION}.zip"
unzip -q /tmp/rct.zip -d /opt/resharper-clt && rm /tmp/rct.zip
ln -sf /opt/resharper-clt/inspectcode /usr/local/bin/inspectcode
inspectcode --version   # must print 2026.2.2
```

```sh
# baseline bake step — option B: .NET global tool plus a PATH shim
INSPECTCODE_VERSION=2026.2.2
dotnet tool install -g JetBrains.ReSharper.GlobalTools --version "${INSPECTCODE_VERSION}"
printf '#!/bin/sh\nexec jb inspectcode "$@"\n' > /usr/local/bin/inspectcode
chmod +x /usr/local/bin/inspectcode
inspectcode --version   # must print 2026.2.2
```

## Enabling

The plugin is **disabled by default** — it loads only when named in both
gates:

```json
{
  "CodeyBox": {
    "Plugins": {
      "Allowlist": ["codeybox.inspectcode"],
      "Enabled": ["codeybox.inspectcode"]
    }
  }
}
```

and per project under `Audit.Custom`:

```json
{ "Kind": "plugin", "PluginId": "codeybox.inspectcode" }
```

The `inspectcode` tool requirement is only contributed to baseline
provisioning while the plugin is enabled.

## Configuration

Scoped under `CodeyBox:Plugins:codeybox.inspectcode`, resolved per run
(hot-reloadable):

| Key | Default | Meaning |
|---|---|---|
| `ExpectedVersion` | `2026.2.2` | Pinned Command Line Tools release; a different installed version fails closed as infrastructure. Set this to the release you provisioned. |
| `SolutionPath` | `null` | Repository-relative `.sln`/`.slnx` path (e.g. `src/App.sln`) analyzed instead of discovery. Must be relative, without `..` segments; a missing file fails closed as deterministic infrastructure. |
| `SettingsPath` | `null` | Operator-owned `.DotSettings` file passed to `--settings` — the fully operator-owned ruleset. Ignored when `ExtraArguments` already supplies `--settings`. Prefer an absolute path outside the audited repository. |
| `TrustRepositorySuppression` | `false` | When `false` (default) the scan passes `--disable-settings-layers:SolutionShared;SolutionPersonal`, so solution/project settings authored in the audited tree cannot demote or hide inspections. When `true`, the repo's settings layers apply. (`.editorconfig` severities apply either way — see above.) |
| `BuildSolution` | `false` | When `false` (default) the scan passes `--no-build` so it runs hermetically without a NuGet restore. When `true`, passes `--build` for build-coupled inspections. Ignored when `ExtraArguments` supplies `--build`/`--no-build`. |
| `MinimumSeverity` | `info` | Drop mapped findings below this severity (`info`, `warning`, `error`) — and lower the tool-side `-e` floor to match, unless `ExtraArguments` supplies `--severity`/`-e`. |
| `IncludedRules` / `ExcludedRules` | — | Exact inspection ids to keep/drop (e.g. `UnusedMember.Global`, `NotAccessedVariable.Compiler`). |
| `ExcludePaths` | `vendor/`, `third_party/`, `node_modules/`, `obj/`, `bin/`, `artifacts/`, `dist/`, `build/`, `out/`, `coverage/` | Repo-relative paths dropped from findings — exact path, or directory prefix when trailing `/`. Filters reported findings, not the scan. Setting it replaces the default list. |
| `ExtraArguments` | — | Extra argv appended after the built-in args and the solution positional (never via a shell). Useful for `--swea`/`--no-swea`, `--project=…`, `--include/--exclude`, or `--properties:…`. Never pass `--output`/`-o`/`--format`/`-f` (would divert or replace the SARIF report the parser expects and break the run into infrastructure failure). |
| `TimeoutSeconds` | `600` | Per-run bound (solution-wide analysis over a full repository takes minutes). Exceeding it is infrastructure, not a pass. Discovery probes share a 30s cap. |
| `MaxOutputBytesPerStream` / `MaxFindings` | `1 MiB` / `1000` | Output/result caps; overruns are reported as truncation. Large repositories can exceed 1 MiB of SARIF — raise the former (up to 64 MiB) rather than wondering where findings went. |

## Default scope

The shallowest solution within three levels of the repository root is
analyzed — the repository's own declaration of analyzable scope; that is
usually the root solution. On top of that, findings under vendored
(`vendor/`, `third_party/`, `node_modules/`) and generated (`obj/`,
`bin/`, `artifacts/`, `dist/`, `build/`, `out/`, `coverage/`) prefixes are
dropped by default: violations there belong to upstream packages or
compiler/build output, not the change under audit — reporting them produces
noise that trains operators to ignore the auditor. Re-include a prefix by
overriding `ExcludePaths`; analyze a different solution by setting
`SolutionPath`.
