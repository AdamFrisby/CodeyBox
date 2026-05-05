# Auditor Plugins

Operators can ship custom auditors as standalone NuGet packages (or local class
libraries) without modifying the CodeyBox core or its `Audit.Shell` project.
Third-party auditors implement `IAuditor`, are decorated with `[CodeyBoxPlugin]`,
and are discovered at startup by the plugin loader.

## Prerequisites

- CodeyBox.Core NuGet package (for `IAuditor`, `AuditResult`, `AuditFinding`,
  `AuditContext`, `AuditCapabilities`)
- CodeyBox.PluginSdk NuGet package (for `CodeyBoxPluginAttribute`, `IPluginInitializer`,
  `PluginContext`)

Both packages target `net10.0`. Never reference `CodeyBox.Orchestrator`, `CodeyBox.Api`,
or any other internal CodeyBox package — the SDK surface is explicitly limited to Core
and PluginSdk.

## Project skeleton

```xml
<!-- MyOrg.CodeyBoxPlugin.NoVar/MyOrg.CodeyBoxPlugin.NoVar.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <!-- Pin to a specific version and update intentionally; avoid Version="*". -->
    <PackageReference Include="CodeyBox.Core"      Version="1.0.0" />
    <PackageReference Include="CodeyBox.PluginSdk" Version="1.0.0" />
  </ItemGroup>
</Project>
```

## Implementing the auditor

```csharp
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Logging;

[CodeyBoxPlugin(
    id: "myorg.no-var-keyword",
    displayName: "MyOrg: Ban var keyword",
    minHostApiVersion: "1.0")]
public sealed class NoVarAuditor : IAuditor, IPluginInitializer
{
    // ── IAuditor ──────────────────────────────────────────────────────────────

    /// <summary>Stable name used in logs, findings, and PerAuditorAgent keys.</summary>
    public string Name => "myorg:no-var";

    /// <summary>
    /// Implementation kind for observability. Use "tool" for deterministic
    /// non-LLM auditors, "shell" for shell-script auditors, "llm" for
    /// LLM-backed auditors.
    /// </summary>
    public string Kind => "tool";

    /// <summary>
    /// Declare the minimum capabilities needed. "None" means the auditor
    /// runs in the most restrictive sandbox — no agent credentials or
    /// network access.
    /// </summary>
    public AuditCapabilities Required => AuditCapabilities.None;

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        // Run your checks here. You can:
        // - Read files directly from workingDirectory
        // - Execute tools via sandbox.RunCommandAsync(...)
        // - Call external services (if Required includes Network)
        var findings = new List<AuditFinding>();

        // Example: find lines with "var " in C# files
        foreach (var file in Directory.EnumerateFiles(workingDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var lines = await File.ReadAllLinesAsync(file, ct);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("var ")) continue;
                findings.Add(new AuditFinding(
                    AuditorName: Name,
                    Severity: AuditSeverity.Warning,
                    Title: "var keyword used",
                    Description: $"Use explicit types instead of var: {lines[i].Trim()}",
                    Location: $"{Path.GetRelativePath(workingDirectory, file)}:{i + 1}"));
            }
        }

        return new AuditResult(Passed: findings.Count == 0, Findings: findings);
    }

    // ── IPluginInitializer (optional) ─────────────────────────────────────────

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        // Read plugin-scoped config (see "Per-auditor configuration" below).
        var severity = context.ScopedConfig["Severity"] ?? "warning";
        context.Logger.LogInformation(
            "NoVarAuditor initialized: pluginId={PluginId} severity={Severity}",
            context.PluginId, severity);
        return Task.CompletedTask;
    }
}
```

### The `[CodeyBoxPlugin]` attribute

| Parameter | Type | Description |
|---|---|---|
| `id` | string | Reverse-domain identifier, globally unique (e.g. `myorg.no-var-keyword`). Referenced by `PluginId` in project config. |
| `displayName` | string | Human-readable name shown in the admin dashboard. |
| `minHostApiVersion` | string | Minimum host API version required. Use `"1.0"` for all current releases. |

### `IAuditor` contract

| Member | Description |
|---|---|
| `Name` | Stable identifier used in log messages, audit findings, and `PerAuditorAgent` keys. Use `"myorg:short-name"` convention. |
| `Kind` | One of `"tool"`, `"shell"`, `"llm"`, `"diff-pattern"`. Persisted in observability storage. |
| `Required` | `AuditCapabilities` flags. `None` = most restrictive sandbox; `AgentCredentials | Network` = same sandbox as LLM auditors. |
| `RunAsync` | Produces an `AuditResult(Passed, Findings)`. Return `Passed: false` to trigger rework. |

### Optional interfaces

| Interface | Purpose |
|---|---|
| `IPluginInitializer` | Async startup logic. Called once after DI registration. |
| `IAsyncDisposable` | Clean shutdown. Called by the DI container at host exit. |

## Per-auditor configuration scoping

Plugin auditors receive their config through `PluginContext.ScopedConfig`, which is
an `IConfigurationSection` scoped to:

```
CodeyBox:Plugins:<plugin-id>:
```

Example — setting `Severity` for `myorg.no-var-keyword`:

```json
{
  "CodeyBox": {
    "Plugins": {
      "myorg.no-var-keyword": {
        "Severity": "error"
      }
    }
  }
}
```

Inside `InitializeAsync`:

```csharp
var severity = context.ScopedConfig["Severity"];   // "error"
```

## Building and installing

1. Build the class library: `dotnet build -c Release`
2. Copy the output DLL (and any non-framework dependencies) to the plugins directory,
   e.g. `/etc/codeybox/plugins/`.
3. Add the plugin ID to `CodeyBox:Plugins:Allowlist` in `appsettings.json`:

```json
{
  "CodeyBox": {
    "Plugins": {
      "AssemblyPaths": ["/etc/codeybox/plugins/MyOrg.CodeyBoxPlugin.NoVar.dll"],
      "Allowlist": ["myorg.no-var-keyword"]
    }
  }
}
```

4. Restart the orchestrator. The plugin is discovered and registered at startup.

## Enabling a plugin for a project

In `appsettings.json`, under the project's `Audit.Custom` list:

```json
"Audit": {
  "Custom": [
    { "Kind": "plugin", "PluginId": "myorg.no-var-keyword" }
  ]
}
```

`PluginId` must match the `id` in the `[CodeyBoxPlugin]` attribute exactly.
If the plugin is not loaded (not in the allowlist, or assembly not found), the
composer logs a warning and skips the entry — other auditors continue normally.

Multiple plugin auditors can be enabled together with built-in presets and custom
shell/diff-pattern/llm entries:

```json
"Audit": {
  "Languages": ["csharp"],
  "AuditTypes": ["security"],
  "Custom": [
    { "Kind": "shell", "Name": "unit-tests", "Argv": ["dotnet", "test"] },
    { "Kind": "plugin", "PluginId": "myorg.no-var-keyword" },
    { "Kind": "plugin", "PluginId": "myorg.xml-doc-required" }
  ]
}
```

## Admin dashboard

The admin dashboard **Settings → Plugins** page lists all loaded auditor plugins
with their display names and plugin IDs. Use this page to confirm a plugin was
discovered successfully before adding it to a project config.

The same information is available via the REST API:

```
GET /plugins
→ [{ "pluginId": "myorg.no-var-keyword", "displayName": "MyOrg: Ban var keyword" }]
```

## Sample plugin

A fully working sample is provided at `samples/CodeyBox.SampleAuditorPlugin/`.
It implements a "no TODO comments" auditor and demonstrates:

- `[CodeyBoxPlugin]` decoration
- `IAuditor.RunAsync` with real file walking
- `IPluginInitializer` reading plugin-scoped config
- Local project references for in-tree development (swap to NuGet references for external distribution)

Build it with:

```sh
dotnet build samples/CodeyBox.SampleAuditorPlugin/
```

## Security model

Plugin security is documented in full in [`docs/plugins.md`](plugins.md). Key points:

- Only IDs in `CodeyBox:Plugins:Allowlist` load. An empty allowlist blocks everything.
- Plugins cannot shadow `ICredentialProvider` or `IAgentRunner` — the loader blocks
  these registrations to protect host credentials.
- Each plugin assembly runs in an isolated `AssemblyLoadContext` so dependency
  conflicts with the host are impossible.
- Auditors declared with `Required = AuditCapabilities.None` run in a sandbox with
  no agent credentials mounted. A compromised tool auditor cannot exfiltrate tokens.
