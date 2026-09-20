# Plugin SDK

CodeyBox supports third-party plugins that implement the same `CodeyBox.Core`
interfaces as built-in components. Plugins load at host startup from paths you
configure; the orchestrator discovers them by reflection and registers them into
the DI container automatically. No forking required.

## Contents

1. [How it works](#how-it-works)
2. [Writing a plugin](#writing-a-plugin)
3. [Configuration reference](#configuration-reference)
4. [API-version contract](#api-version-contract)
5. [Plugin lifecycle](#plugin-lifecycle)
6. [Threat model](#threat-model)
7. [Publishing to NuGet](#publishing-to-nuget)

## How it works

```
Operator drops MyOrg.CustomAuditor.dll into /etc/codeybox/plugins/
       │
       ▼
PluginLoader scans all *.dll in PackageDirectories + AssemblyPaths
       │
       ├─ Inspects [CodeyBoxPlugin] attributes from metadata only (no code runs)
       ├─ Validates Enabled (disabled → assembly never loaded)
       ├─ Validates Allowlist
       ├─ Validates MinHostApiVersion
       ├─ Validates [CodeyBoxPluginRequiresTool] declarations (fail closed)
       └─ Registers types under their CodeyBox.Core interface(s)
              │
              ▼
    Orchestrator DI picks up the new IAuditor / IUpstreamRemote / …
    via the existing IEnumerable<TInterface> injection pattern
```

Each plugin assembly is loaded into a dedicated named
`AssemblyLoadContext` (`Plugin:<assembly-name>`) for isolation. The host's
`CodeyBox.Core` and `CodeyBox.PluginSdk` assemblies are always resolved from
the host's own context so that type-identity checks succeed.

## Writing a plugin

### 1. Create the project

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <!-- Only reference PluginSdk (which pulls Core transitively). -->
    <!-- Never reference CodeyBox.Orchestrator or CodeyBox.Api. -->
    <PackageReference Include="CodeyBox.PluginSdk" Version="1.*" />
  </ItemGroup>
</Project>
```

### 2. Implement a Core interface

Every extension point in CodeyBox is a `CodeyBox.Core` interface:
`IAuditor`, `IUpstreamRemote`, `ICredentialProvider`,
`IAgentPromptPreprocessor`, `ISandboxProvider`, etc.
Implement whichever one(s) your plugin contributes. The contracts are unchanged
from built-in implementations — your plugin is just another singleton in the
same DI container. Sandbox providers carry an additional trust model (the host
owns every isolation claim about a plugin backend); read
[`docs/extending/sandbox-plugins.md`](sandbox-plugins.md) before implementing one.

Prompt preprocessors implement `IAgentPromptPreprocessor` and run before every
agent prompt is handed to a runner. The host applies them in three segments:
built-in first, plugin preprocessors ordered by `Order`, then built-in last.
Use a small `Order` value to run earlier within the plugin segment.

Periodic telemetry samplers implement `IMetricSampler` (added in host API
`1.2`). The host's `MetricSamplerHost` drives every registered sampler on
its own loop and re-reads the sampler's `Enabled` / `Interval` each tick so
the sampler can honour hot-reloaded configuration without a host restart.
A sampler owns its own persistence — pick a SQLite file under the
orchestrator dataroot, push to OTLP, write to an in-memory ring buffer,
whatever fits the metric. See [`docs/extending/statistics-plugin.md`](statistics-plugin.md)
for the first shipping sampler (per-agent quota snapshots persisted to a
dedicated SQLite file with a `GET /quota/history` query surface). The
sampler-host contract guarantees that one sampler throwing does NOT block
others: each loop catches and logs.

### 3. Decorate with `[CodeyBoxPlugin]`

```csharp
using CodeyBox.Core;
using CodeyBox.PluginSdk;

[CodeyBoxPlugin(
    id: "myorg.my-auditor",          // unique, reverse-domain style
    displayName: "My Org Auditor",
    minHostApiVersion: "1.0")]       // minimum host version you require
public sealed class MyAuditor : IAuditor
{
    public string Name => "myorg-auditor";
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        // Run your tool, parse output, return findings.
        return Task.FromResult(new AuditResult(Passed: true, Findings: []));
    }
}
```

One class = one plugin ID. Multiple classes in the same assembly carrying
different IDs are each treated as independent plugins.

### 4. Async initialization (optional)

Implement `IPluginInitializer` if you need to perform async work at host
startup (opening connections, reading config, validating credentials):

```csharp
public sealed class MyAuditor : IAuditor, IPluginInitializer
{
    public async Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        // context.ScopedConfig  → IConfigurationSection at CodeyBox:Plugins:myorg.my-auditor:
        // context.Logger        → ILogger pre-named "Plugin:myorg.my-auditor"
        // context.HostApiVersion → e.g. "1.0"

        var timeout = context.ScopedConfig.GetValue("TimeoutSeconds", 30);
        context.Logger.LogInformation("Initialized with timeout {Timeout}s", timeout);
    }
}
```

If `InitializeAsync` throws, the host logs the error and re-throws from
`IHostedService.StartAsync`, causing the .NET Generic Host to **abort the
process**. Catch exceptions inside `InitializeAsync` if you want the host to
remain running despite a plugin failure.

### 5. Disposal (optional)

Implement `IAsyncDisposable` to receive a disposal callback when the host
shuts down. The DI container calls `DisposeAsync` automatically on singletons
that implement it.

```csharp
public sealed class MyAuditor : IAuditor, IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        // Close connections, flush buffers, etc.
    }
}
```

### 6. Reading configuration

Your plugin reads its settings under `CodeyBox:Plugins:<plugin-id>:` in
the host's `appsettings.json`:

```json
{
  "CodeyBox": {
    "Plugins": {
      "myorg.my-auditor": {
        "TimeoutSeconds": 60,
        "RulesPath": "/etc/codeybox/myorg-rules.toml"
      }
    }
  }
}
```

Access these during `InitializeAsync` via `context.ScopedConfig`, or inject
`IConfiguration` and call `.GetSection("CodeyBox:Plugins:myorg.my-auditor:")`.

## Configuration reference

Bind from `CodeyBox:Plugins` in `appsettings.json`:

```json
{
  "CodeyBox": {
    "Plugins": {
      "AssemblyPaths": [
        "/etc/codeybox/plugins/MyOrg.CustomAuditor.dll"
      ],
      "PackageDirectories": [
        "/etc/codeybox/plugins"
      ],
      "Allowlist": [
        "myorg.custom-auditor",
        "myorg.custom-upstream"
      ],
      "Enabled": [
        "myorg.custom-auditor",
        "myorg.custom-upstream"
      ]
    }
  }
}
```

| Key | Type | Description |
|---|---|---|
| `AssemblyPaths` | `string[]` | Absolute paths to specific DLL files. |
| `PackageDirectories` | `string[]` | Directories scanned for `*.dll` (non-recursive). |
| `Allowlist` | `string[]` | Plugin IDs allowed to load. Empty = load nothing. `["*"]` = load all (not recommended). |
| `Enabled` | `string[]` | Plugin IDs switched on. A plugin loads only when it is **both allowlisted and enabled**; an allowlisted-but-disabled plugin stays unloaded — its assembly is never loaded, its types never registered, its instances never constructed. `["*"]` = enable all (not recommended: every future plugin would switch itself on by being present). |
| `FailOnInitializationError` | `bool` | Abort host startup when a plugin's initialisation throws (default `true`). Set `false` to log-and-continue without the failed plugin. Load failures are never fatal. See [Failure policy](#failure-policy). |
| `StartupReportMaxEntries` | `int` | Max plugin entries listed individually in the startup summary log (default `20`); beyond this the summary collapses to counts. Full inventory stays on `GET /plugins/status`. |

**Important:** an empty `Allowlist` is the safe default — no plugins load unless
the operator explicitly opts in. This is intentional.

## Enablement and defaults

Enablement is a separate axis from the allowlist. Both gates must pass:

- **Allowlist** answers "is this assembly permitted to load at all?"
- **Enabled** answers "is this plugin switched on?"

**Default: disabled.** A plugin that is not named in `Enabled` does nothing
until an operator turns it on — with a large catalogue this is the difference
between an opt-in capability and an unusable default install.

The one exception is backward compatibility: the four bundled plugins that
predate the switch — `codeybox.file-size-limits`, `codeybox.statistics`,
`codeybox.quota-reset-notifier`, `codeybox.opencode-go-quota` — are enabled
by default so deployments that allowlisted them keep working with no config
change. Everything else — including the bundled
`codeybox.dotnet-test-runner` / `codeybox.pytest-test-runner` entries and any
plugin added later — is disabled until the operator names it in `Enabled`.
Explicitly configuring `"Enabled": []` disables everything, including the
four defaults.

Changing `Enabled` requires a host **restart**. Unloading a live plugin is
not safe (assemblies live in non-collectible load contexts; instances are
already constructed as DI singletons), so a runtime edit is logged as
restart-required and otherwise ignored. Per-plugin settings under
`CodeyBox:Plugins:<plugin-id>:` remain hot-reloadable as before.

## Declaring external tools

A plugin that shells out to an external binary must declare it with the
repeatable `[CodeyBoxPluginRequiresTool]` attribute — the binary it invokes
and how an operator would provision it:

```csharp
[CodeyBoxPluginRequiresTool(
    "dotnet",
    AptPackage = "dotnet-sdk-10.0",
    InstallHint = "install the .NET SDK from your toolchain feed")]
public sealed class MyTestRunner : ITestRunnerAuditor
{
}
```

| Member | Meaning |
|---|---|
| `Binary` (required) | Bare executable name (`dotnet`, `pytest`). No paths, no whitespace, no shell metacharacters — anything else fails closed and the plugin is skipped at load. |
| `AptPackage` (optional) | Debian package name the host installs into sandbox baselines for enabled plugins. Omit when the tool is not apt-installable. |
| `InstallHint` (optional) | Operator-facing guidance shown in startup warnings and bake failures. Display text only — never executed. |

This is plugin metadata, not operator configuration, and the host treats it
as untrusted input: names are validated against a strict allowlist before
they reach any sink, and the host constructs every baseline command itself
(see below). A plugin can never inject an arbitrary command through its
tool declaration.

### Baseline provisioning

For **enabled** plugins only, the host translates validated tool
requirements into sandbox baseline contributions:

- **Verification** — a host-owned presence probe appended after the
  agent-CLI probes (`BaselineVerificationCommands`). The bake fails if the
  binary is not on sandbox PATH.
- **Installation** — a single host-constructed `apt-get install` line
  appended after operator `ExtraRuncmd` (only for tools declaring
  `AptPackage`; tools without one are verify-only and the operator
  provisions them via their own baseline steps).

Both lists join the Incus/Multipass baseline-identity hashes, so a change in
the enabled set produces a fresh baseline ref and the stale image is
reaped through the normal orphan/grace path. The baseline for an operator
who enables three auditors carries the tooling for exactly those three.

### Startup report

At startup the host logs every plugin's enabled/disabled/loaded state and
probes the host `PATH` for each enabled plugin's binaries. An enabled plugin
with an unmet requirement is reported loudly (`plugin.tool_unmet` audit
event plus a warning naming the missing binary and its install hint) —
before it can fail inside a sandbox.

The report covers every configured path, not just every discovered plugin:
for each assembly path the host records whether the file was found, whether
it loaded, which plugin ids it contributed, and which host contracts
(`IAuditor`, `IMetricSampler`, …) they registered under. Every skip and
failure carries its reason — file missing, assembly unloadable, version or
contract mismatch, not allowlisted, no `[CodeyBoxPlugin]` type found,
initialisation threw. A configured plugin that does not end up loaded always
produces a warning naming it and why; with dozens of plugins configured the
log stays a bounded summary (see `StartupReportMaxEntries`) while the full
inventory remains queryable below.

A plugin built against a different version of the host contracts
(`CodeyBox.Core`/`CodeyBox.PluginSdk`) than the running host — typically a
host rebuild that did not rebuild the plugin projects — is detected
specifically and reported as a stale-contracts build ("was built against a
different version of the host contracts …; rebuild the plugin against the
current host"), not as a generic load failure.

The same inventory is served at runtime, without reading logs or restarting:

```
GET /plugins/status
→ { "loaded": [{ "pluginId": "…", "displayName": "…",
                 "assemblyPath": "…", "contracts": ["IAuditor"] }],
     "discovery": [{ "pluginId": "…", …, "loaded": true,
                     "skipReason": "None", "contracts": ["IAuditor"] }],
     "assemblies": [{ "assemblyPath": "…", "found": true, "loaded": true,
                      "pluginIds": ["…"], "contracts": ["IAuditor"],
                      "skipReason": "None" }] }
```

`GET /plugins` stays auditor-only for the dashboard and project-config
reference; `/plugins/status` lists every loaded plugin under any contract.

### Failure policy

Silent absence is a failure mode: an auditor that never ran must never look
like an audit that passed. The host therefore treats the two failure kinds
differently, and the choice is explicit:

| Failure kind | Behaviour | Configurable? |
|---|---|---|
| Load failure: file missing, unloadable assembly, stale host contracts, no `[CodeyBoxPlugin]` type, gate rejection | Non-fatal. Loud warning + audit event (`plugin.assembly_failed` / `plugin.stale_contracts`); host starts without the plugin. | No — a notifier must never take the host down, and an auditor's absence is already loud. |
| Initialisation failure: `IPluginInitializer.InitializeAsync` (or its DI resolution) throws | Fatal by default: error + `plugin.initialization_failed` audit event, then host startup aborts. A half-initialised auditor that silently never runs is worse than a host that refuses to start. | Yes — `CodeyBox:Plugins:FailOnInitializationError` (default `true`). Set `false` to log-and-continue without the failed plugin; its failure stays on the startup report and `/plugins/status`. |

The policy does not differ by contract today: every contract fails closed on
initialisation errors and stays up on load failures. If a future contract
genuinely needs its own policy, it gets its own knob — the default stays
fail-closed.

## API-version contract

Every plugin declares the minimum host API version it requires via
`minHostApiVersion` on `[CodeyBoxPlugin]`. The host rejects plugins that
require a version newer than `CodeyBoxApiVersion.Current` (currently `"1.3"`).

### Version bump rules

| Change type | Action |
|---|---|
| Breaking: interface renamed, parameter added/removed, contract changed | Bump major (`2.0`) |
| Additive: new optional interface, new Core type | Bump minor (`1.1`) |
| No API-surface change (docs, comments, internal refactor) | No bump |

### Compatibility guarantee

A plugin built against host `1.0` will keep working on any `1.x` host
(same major, any minor ≥ 0). It will NOT load on `2.0` (different major).

## Plugin lifecycle

```
Host startup
    │
    ├─ AddCodeyBoxPlugins() (pre-DI-build)
    │      Discover assemblies → validate → register types → freeze container
    │
    ├─ PluginInitializationService.StartAsync()
    │      Emit plugin.loaded audit events
    │      Call IPluginInitializer.InitializeAsync() on each plugin type
    │
    ├─ [host processes work items]
    │      Agent prompt preprocessing:
    │      built-in first → plugin preprocessors by Order → built-in last
    │
    └─ Host shutdown
           DI container disposes singletons → IAsyncDisposable.DisposeAsync()
```

**No hot-reload of membership in v1.** Plugin changes require a host restart.
Per-plugin settings remain hot-reloadable; only the enabled set is
restart-gated (see [Enablement and defaults](#enablement-and-defaults)).

## Threat model

### What plugins can do

Plugins run **in-process** with full host privileges. A plugin:

- Can read any file, environment variable, or memory accessible to the host process.
- Can open arbitrary network connections (subject to OS firewall rules).
- Can call any .NET API.
- Can register itself under Core interfaces and influence pipeline decisions.

This is intentional: plugins are trusted code. Treat plugin authors the same
way you treat authors of the orchestrator itself.

### What plugins cannot do (by default)

- Access credentials via `ICredentialProvider` — the `IPluginHost` does NOT
  expose it. A plugin that needs secrets must read them from its own
  configuration section or take `ICredentialProvider` as a DI dependency
  (which the operator must configure).
- Claim enforced network-egress isolation from a sandbox provider — every
  plugin-contributed kind is classified `NotEnforced` by the host and refused
  wherever enforced egress is required. See
  [`docs/extending/sandbox-plugins.md`](sandbox-plugins.md) for the trust model.
- Load without appearing in the allowlist — every plugin ID must be explicitly
  listed in `Plugins.Allowlist`.
- Load while disabled — every plugin ID must also be switched on in
  `Plugins:Enabled`. A disabled plugin's assembly is never loaded: the host
  inspects assembly metadata without executing any plugin code and only then
  decides whether the file may be loaded at all.
- Load without an audit-tier event — the host emits `plugin.loaded` for every
  successfully loaded plugin.

### Operator guidance for evaluating third-party plugins

Before adding a plugin to your allowlist:

1. **Review the source.** Plugins are in-process; there is no sandbox boundary.
2. **Pin the version.** Use a specific DLL, not a directory that auto-updates.
3. **Check `minHostApiVersion`.** A plugin claiming a very old version but
   shipping new binaries may be attempting version-confusion attacks.
4. **Monitor `plugin.loaded` audit events.** Alert on unexpected plugin IDs.
5. **Prefer plugins from vendors you have a support agreement with.**

### Not mitigated

A loaded plugin runs in-process with full host trust. There is no signature
check before loading, no per-plugin process or assembly sandbox, and no
capability declaration a plugin can be held to. The allowlist is the control:
only load plugins you would run as your own code.

## Publishing to NuGet

When you're ready to share your plugin:

1. Set `<IsPackable>true</IsPackable>` and populate NuGet metadata
   (`PackageId`, `Description`, `RepositoryUrl`) in your `.csproj`.
2. Run `dotnet pack -c Release`.
3. Publish to NuGet.org or your private feed: `dotnet nuget push`.
4. Operators install by running `dotnet publish` of your package into their
   plugins directory, or by using a deployment tool that resolves it from NuGet.

The `CodeyBox.PluginSdk` package itself follows this same pattern.

