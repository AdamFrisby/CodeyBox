# Plugin SDK

CodeyBox supports third-party plugins that implement the same `CodeyBox.Core`
interfaces as built-in components. Plugins load at host startup from paths you
configure; the orchestrator discovers them by reflection and registers them into
the DI container automatically. No forking required.

---

## Contents

1. [How it works](#how-it-works)
2. [Writing a plugin](#writing-a-plugin)
3. [Configuration reference](#configuration-reference)
4. [API-version contract](#api-version-contract)
5. [Plugin lifecycle](#plugin-lifecycle)
6. [Threat model](#threat-model)
7. [Publishing to NuGet](#publishing-to-nuget)
8. [Future work](#future-work)

---

## How it works

```
Operator drops MyOrg.CustomAuditor.dll into /etc/codeybox/plugins/
       │
       ▼
PluginLoader scans all *.dll in PackageDirectories + AssemblyPaths
       │
       ├─ Reads [CodeyBoxPlugin] attributes
       ├─ Validates Allowlist
       ├─ Validates MinHostApiVersion
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

---

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
`IAuditor`, `IUpstreamRemote`, `ICredentialProvider`, `IAgentRunner`, etc.
Implement whichever one(s) your plugin contributes. The contracts are unchanged
from built-in implementations — your plugin is just another singleton in the
same DI container.

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

---

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

**Important:** an empty `Allowlist` is the safe default — no plugins load unless
the operator explicitly opts in. This is intentional.

---

## API-version contract

Every plugin declares the minimum host API version it requires via
`minHostApiVersion` on `[CodeyBoxPlugin]`. The host rejects plugins that
require a version newer than `CodeyBoxApiVersion.Current` (currently `"1.1"`).

### Version bump rules

| Change type | Action |
|---|---|
| Breaking: interface renamed, parameter added/removed, contract changed | Bump major (`2.0`) |
| Additive: new optional interface, new Core type | Bump minor (`1.1`) |
| No API-surface change (docs, comments, internal refactor) | No bump |

### Compatibility guarantee

A plugin built against host `1.0` will keep working on any `1.x` host
(same major, any minor ≥ 0). It will NOT load on `2.0` (different major).

---

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
    │
    └─ Host shutdown
           DI container disposes singletons → IAsyncDisposable.DisposeAsync()
```

**No hot-reload in v1.** Plugin changes require a host restart.

---

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
- Load without appearing in the allowlist — every plugin ID must be explicitly
  listed in `Plugins.Allowlist`.
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

### Future mitigations (out of scope for v1)

- Code-signing verification (check Authenticode / sigstore signature before
  loading).
- Assembly-level sandboxing (separate process, gVisor, WebAssembly).
- Capability declarations (plugins declare required permissions at install time).

---

## Publishing to NuGet

When you're ready to share your plugin:

1. Set `<IsPackable>true</IsPackable>` and populate NuGet metadata
   (`PackageId`, `Description`, `RepositoryUrl`) in your `.csproj`.
2. Run `dotnet pack -c Release`.
3. Publish to NuGet.org or your private feed: `dotnet nuget push`.
4. Operators install by running `dotnet publish` of your package into their
   plugins directory, or by using a deployment tool that resolves it from NuGet.

The `CodeyBox.PluginSdk` package itself follows this same pattern.

---

## Future work

- Code-signing / sigstore integration.
- Per-plugin process isolation.
- Hot-reload support (requires collectible ALCs throughout).
- Plugin marketplace / registry.
