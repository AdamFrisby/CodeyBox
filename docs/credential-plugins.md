# Credential-Provider Plugin SDK

CodeyBox lets third parties register custom `ICredentialProvider` implementations
for HashiCorp Vault, AWS SSM, Azure KeyVault, Doppler, 1Password CLI, Pulumi ESC,
internal corporate vaults, and any other secret-management backend. Plugins compose
with the existing plugin foundation; write a class, drop a DLL, restart the host.

---

## Contents

1. [Why credential plugins](#why-credential-plugins)
2. [Chain order rationale](#chain-order-rationale)
3. [Writing a credential plugin](#writing-a-credential-plugin)
4. [Time-bound credentials and ExpiresAt](#time-bound-credentials-and-expiresat)
5. [Per-project priority](#per-project-priority)
6. [Plugin configuration via IPluginHost.ScopedConfig](#plugin-configuration-via-ipluginhostscopedconfig)
7. [Credential redaction](#credential-redaction)
8. [Sample: JSON-file mock vault](#sample-json-file-mock-vault)

---

## Why credential plugins

Today, every secret must be an environment variable (`CODEYBOX_CLAUDE_API_KEY`,
`CODEYBOX_GITHUB_TOKEN`, etc.) and the orchestrator process must be restarted to
rotate. Real-world ops shops keep secrets in proper vaults that issue short-lived
credentials on demand. A credential plugin is the clean answer: it implements
`ICredentialProvider`, reads from your vault, and returns a fresh `AgentCredential`
on every pickup.

---

## Chain order rationale

The orchestrator resolves credentials from a **ChainedCredentialProvider**. The
chain is always built in this order:

```
BUILT-IN-OAUTH → PLUGINS → BUILT-IN-ENV
```

1. **ClaudeOAuthFileCredentialProvider** — reads Claude's OAuth token from
   `~/.claude/.credentials.json` (or the path in `CODEYBOX_CLAUDE_OAUTH_FILE`).
   Re-reads on every pickup so a host-side token rotation propagates without a
   restart. Only covers `AgentKind.Claude`.

2. **Plugin providers** — all installed and allowlisted credential plugins, in
   discovery order (or per-project priority order; see below). Plugins go after
   the Claude-specific OAuth file so operators who have that file never lose their
   working setup when a vault plugin is installed.

3. **EnvironmentCredentialProvider** — catch-all fallback reading host env vars
   (`CODEYBOX_CLAUDE_API_KEY`, `CODEYBOX_COPILOT_TOKEN`, etc.). Always last so
   env vars serve as the operator's baseline when no plugin is configured.

**Operators with no credential plugins see zero behaviour change.** The chain
collapses to the pre-plugin OAuth-file → env-var behaviour.

---

## Writing a credential plugin

### 1. Create the project

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <!-- Reference PluginSdk (which pulls Core). Never reference Orchestrator or Api. -->
    <PackageReference Include="CodeyBox.PluginSdk" Version="1.*" />
  </ItemGroup>
</Project>
```

### 2. Implement `ICredentialProvider`

```csharp
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Logging;

[CodeyBoxPlugin(
    id: "myorg.vault-creds",
    displayName: "MyOrg HashiCorp Vault",
    minHostApiVersion: "1.0")]
public sealed class VaultCredentialProvider : ICredentialProvider
{
    private readonly IHttpClientFactory _http;
    private readonly IPluginHost _host;

    public VaultCredentialProvider(IHttpClientFactory http, IPluginHost host)
    {
        _http = http;
        _host = host;
    }

    public async Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        // Read Vault address and auth token from scoped config.
        var vaultAddress = _host.ScopedConfig["VaultAddress"] ?? "https://vault.example.com";

        // Only handle Claude in this example.
        if (agent != AgentKind.Claude)
            return null;    // null = fall through to next provider

        // Issue a short-lived secret from Vault for this agent.
        var secret = await FetchFromVaultAsync(vaultAddress, agent, ct);
        if (secret is null)
            return null;

        return new AgentCredential(
            agent,
            EnvironmentVariables: new Dictionary<string, string>
            {
                ["CLAUDE_CODE_OAUTH_TOKEN"] = secret.Value,
            },
            Files: new Dictionary<string, string>())
        {
            // Tell the chain how long to cache this credential.
            // After expiry, the next pickup re-fetches from Vault.
            ExpiresAt = secret.Expiry,
        };
    }

    private async Task<(string Value, DateTimeOffset Expiry)?> FetchFromVaultAsync(
        string address, AgentKind agent, CancellationToken ct)
    {
        // Replace with real Vault HTTP call.
        await Task.CompletedTask;
        return null;
    }
}
```

### 3. Register the plugin

Add the DLL path and allow the plugin ID in `appsettings.json`:

```json
{
  "CodeyBox": {
    "Plugins": {
      "AssemblyPaths": ["/etc/codeybox/plugins/MyOrg.VaultCreds.dll"],
      "Allowlist": ["myorg.vault-creds"]
    },
    "Plugins:myorg.vault-creds": {
      "VaultAddress": "https://vault.example.com",
      "RoleId": "..."
    }
  }
}
```

---

## Time-bound credentials and ExpiresAt

Short-lived vault credentials should set `AgentCredential.ExpiresAt`:

```csharp
return new AgentCredential(agent, envVars, files)
{
    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
};
```

When `ExpiresAt` is set, the `ChainedCredentialProvider` **caches** the
credential up to that instant. Subsequent pickups within the validity window are
served from cache without calling the plugin. Once expired, the next pickup calls
the plugin again for a fresh credential.

When `ExpiresAt` is null (the default, and the value returned by all built-in
providers), the chain calls through every time — no caching — so live rotations
(e.g. OAuth-file token refresh) are picked up without a restart.

**Recommendations for vault plugins:**

- Set `ExpiresAt` a few minutes before the actual vault expiry to account for
  clock skew and pickup latency.
- Return `null` (not an error) for agents your plugin doesn't cover. The chain
  falls through to the next provider.
- Do not log raw credential values. Serilog's `SensitiveDataRedactionEnricher`
  provides a last line of defence but should not be relied upon as the primary
  protection.

---

## Per-project priority

When multiple credential plugins are installed, a project can declare which ones
it wants and in what order:

```json
{
  "CodeyBox": {
    "Projects": [
      {
        "Id": "my-app",
        "CredentialProviderPriority": ["myorg.vault-creds", "myorg.aws-ssm"]
      }
    ]
  }
}
```

- Only the listed plugin IDs are included in the project's plugin slot.
- They are tried in the order listed.
- Plugins installed but not listed are excluded (e.g. `myorg.1password` in the
  example above is skipped for this project).
- An empty `CredentialProviderPriority` (the default) includes all discovered
  plugins in global discovery order.

The built-in OAuth-file and env-var providers are always present regardless of
the priority list.

Priority filtering is applied automatically by the orchestrator when it calls
`IProjectAwareCredentialProvider.GetAsync(agent, priority, ct)`. Plugin authors
implementing `ICredentialProvider` do not need to perform any ordering themselves.

---

## Plugin configuration via `IPluginHost.ScopedConfig`

Each plugin receives an `IPluginHost` that exposes a configuration section scoped
to `CodeyBox:Plugins:<plugin-id>`. Use this instead of injecting `IConfiguration`
directly so your plugin's settings are namespace-isolated from the host and from
other plugins.

```csharp
// Operator sets: CodeyBox:Plugins:myorg.vault-creds:VaultAddress
var address = _host.ScopedConfig["VaultAddress"];
var timeout = int.Parse(_host.ScopedConfig["TimeoutSeconds"] ?? "30");
```

Plugins do **not** get access to other plugins' configuration sections or to the
host's built-in credential provider settings. Isolation is enforced by the scoped
key prefix.

---

## Credential redaction

The `SensitiveDataRedactionEnricher` intercepts structured Serilog log-event
properties and redacts recognized secret patterns before they reach the log sink.
It does **not** actively process `AgentCredential` objects — it only acts on values
that are accidentally emitted as log properties. This enricher:

- Replaces the value of any log property whose name contains "Token", "Secret",
  "Password", "Authorization", or "ApiKey" with `***`.
- Redacts values matching known secret patterns (GitHub PATs, Anthropic keys,
  Gemini API keys, etc.) regardless of property name.

Plugin authors must still follow the rule: **do not log raw secrets**. The
enricher is a defence-in-depth backstop, not the primary control.

---

## Sample: JSON-file mock vault

`samples/CodeyBox.SampleVaultCredentialPlugin/` contains a complete, buildable
sample that reads credentials from a local JSON file in lieu of a real Vault
server. It demonstrates:

- The `[CodeyBoxPlugin]` attribute and `ICredentialProvider` implementation.
- Reading vault address and credentials from `IPluginHost.ScopedConfig`.
- Returning `AgentCredential` with `ExpiresAt` for time-bound caching.
- Returning `null` for unsupported agents so the chain falls through.

To adapt for a real vault:

1. Replace the JSON-file read in `SampleVaultCredentialProvider.ReadVaultFileAsync`
   with an HTTP call to your Vault's secret engine API.
2. Parse the vault's lease duration into `ExpiresAt`.
3. Add authentication (AppRole, Kubernetes SA, etc.) via `IPluginHost.ScopedConfig`.

See [`docs/plugins.md`](plugins.md) for the general plugin authoring guide.
