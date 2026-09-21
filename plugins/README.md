# Plugins

First-party CodeyBox plugins live here, one .NET project per plugin, grouped by
what the plugin does. The grouping is by purpose — the way someone searches for
a plugin ("the Terraform auditor", "the Linear sync") — so the directory stays
navigable at seventy entries.

## Layout

```
plugins/
  README.md                        ← this convention
  <group>/                          ← purpose group (table below)
    <ProjectName>/                   ← one directory per plugin
      <ProjectName>.csproj
      *.cs
```

Two levels under `plugins/` is the target: `plugins/<group>/<ProjectName>`.
Do not nest deeper — deep paths make the solution file and the registration
lines unwieldy.

## Groups

Auditor groups carry an `auditors-` prefix so auditor plugins (which implement
`IAuditor`) are distinguishable at a glance from the other plugin shapes, which
sit alongside them rather than buried among them.

| Group | Holds |
|---|---|
| `auditors-api-compatibility` | Auditors guarding public API / protocol compatibility |
| `auditors-architecture` | Auditors guarding repo structure (e.g. `CodeyBox.FileSizeLimitsAuditorPlugin`) |
| `auditors-dependency-vulnerabilities` | Auditors scanning dependencies for known vulnerabilities |
| `auditors-documentation` | Auditors checking docs coverage and freshness |
| `auditors-infrastructure` | Auditors checking infrastructure-as-code and deployment descriptors |
| `auditors-licensing` | Auditors checking license headers and dependency licenses |
| `auditors-linting` | Auditors enforcing code style and formatting |
| `auditors-schema` | Auditors validating schemas, migrations, and contracts |
| `auditors-scripting` | Auditors checking shell and scripting hygiene |
| `auditors-secrets` | Auditors scanning for leaked secrets and credentials |
| `auditors-static-analysis` | Auditors running static analysis (compiler warnings, analysers) |
| `credentials` | Secret providers and credential brokers (e.g. `CodeyBox.InfisicalPlugin`) |
| `notifications` | Chat/push notification providers (`INotificationProvider`, e.g. `CodeyBox.SlackPlugin`) |
| `quota` | Quota probes, reset notifiers, and quota usage telemetry (e.g. `CodeyBox.OpencodeGoQuotaPlugin`, `CodeyBox.QuotaResetNotifier`, `CodeyBox.StatisticsPlugin`) |
| `telemetry` | Metric samplers (`IMetricSampler`) outside the quota domain |
| `test-runners` | Test-execution plugins (`ITestRunnerAuditor`, e.g. `CodeyBox.DotnetTestRunnerPlugin`) |
| `upstream` | Upstream-remote forge providers (`IUpstreamRemote`, e.g. `CodeyBox.GiteaUpstreamPlugin`) |
| `work-sync` | External work-tracker sync (e.g. `CodeyBox.LinearWorkSyncPlugin`, `CodeyBox.PlaneWorkSyncPlugin`) |

The authoritative list is the `RecognisedGroups` set in
`tests/CodeyBox.Tests/PluginLayoutConventionTests.cs`, which enforces this
convention. Adding a genuinely new purpose means adding the group there (and to
this table) — not improvising a directory name.

## Naming

- The project directory and the project file share one name: `<ProjectName>/`
  holds `<ProjectName>.csproj`. The name starts with `CodeyBox.` and, for new
  plugins, ends with `Plugin` (e.g. `CodeyBox.TerraformAuditorPlugin`).
  `CodeyBox.QuotaResetNotifier` predates the suffix rule and is grandfathered —
  project names are plugin identity and are never renamed for layout reasons.
- The assembly name and namespaces follow the project name unchanged. Moving a
  plugin must not change project names, assembly names, or plugin ids.

## Project file

Each plugin project file must contain:

- `Sdk="Microsoft.NET.Sdk"` with `<TargetFramework>net10.0</TargetFramework>`,
  `<Nullable>enable</Nullable>`, and `<ImplicitUsings>enable</ImplicitUsings>`.
- A reference to the SDK surface: `ProjectReference` entries for
  `src/CodeyBox.Core` and `src/CodeyBox.PluginSdk` (external authors use the
  equivalent `CodeyBox.PluginSdk` package reference instead). Relative paths
  use one `..\..` per level, e.g. `..\..\..\src\CodeyBox.Core\...` from two
  levels under `plugins/`.
- No reference to `CodeyBox.Orchestrator`, `CodeyBox.Api`, or any other
  internal host package — the SDK surface is Core plus PluginSdk only
  (see `docs/extending/plugins.md`).

## Registration

A new in-repo plugin touches exactly two files beyond its own directory:

1. `CodeyBox.slnx` — one line under the `/plugins/` folder:
   `<Project Path="plugins/<group>/<ProjectName>/<ProjectName>.csproj" />`
2. `tests/CodeyBox.Tests/CodeyBox.Tests.csproj` — one line:
   `<ProjectReference Include="..\..\plugins\<group>\<ProjectName>\<ProjectName>.csproj" />`

## Enforcement

`tests/CodeyBox.Tests/PluginLayoutConventionTests.cs` fails the build's test
run when a plugin drifts: unknown group, directory/file name mismatch, missing
`Plugin` suffix (outside the grandfathered set), wrong target framework,
missing SDK reference, forbidden host reference, or a missing registration
line. Run it with:

```
dotnet test tests/CodeyBox.Tests/CodeyBox.Tests.csproj --filter "FullyQualifiedName~PluginLayoutConvention"
```
