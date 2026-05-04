// SAMPLE CREDENTIAL-PROVIDER PLUGIN — demonstrates the CodeyBox credential plugin pattern.
//
// This plugin reads secrets from a local JSON file that mimics a vault server response.
// For a real vault integration, replace JsonFileVaultClient with HTTP calls to your
// vault's secret engine API.
//
// How to use:
// 1. Build this project.
// 2. Add the DLL path to CodeyBox:Plugins:AssemblyPaths in appsettings.json.
// 3. Add "sample.vault-creds" to CodeyBox:Plugins:Allowlist.
// 4. Configure the vault path under CodeyBox:Plugins:sample.vault-creds.
// 5. Restart the orchestrator — the chain picks up the plugin automatically.
//
// See docs/credential-plugins.md for the full author guide.

using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Logging;

namespace CodeyBox.SampleVaultCredentialPlugin;

/// <summary>
/// Sample credential provider that reads secrets from a JSON file on disk.
/// Demonstrates the full credential plugin pattern including:
/// <list type="bullet">
///   <item>Reading per-plugin config from <see cref="IPluginHost.ScopedConfig"/>.</item>
///   <item>Returning <see langword="null"/> for unsupported agents (chain fallthrough).</item>
///   <item>Setting <see cref="AgentCredential.ExpiresAt"/> for time-bound caching.</item>
/// </list>
/// </summary>
[CodeyBoxPlugin(
    id: "sample.vault-creds",
    displayName: "Sample JSON-File Vault",
    minHostApiVersion: "1.0")]
public sealed class SampleVaultCredentialProvider : ICredentialProvider, IPluginInitializer
{
    private readonly IPluginHost _host;
    private string _vaultFilePath = string.Empty;

    public SampleVaultCredentialProvider(IPluginHost host)
    {
        _host = host;
    }

    // ── IPluginInitializer ────────────────────────────────────────────────────

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        // Read the vault file path from scoped config:
        //   CodeyBox:Plugins:sample.vault-creds:VaultFilePath
        var rawPath = context.ScopedConfig["VaultFilePath"]
            ?? Path.Combine(AppContext.BaseDirectory, "sample-vault.json");

        // Canonicalize the path and confirm it stays within the base directory.
        // Operators adapting this sample should keep this guard: if VaultFilePath is
        // ever surfaced through a higher-level API, path-traversal prevention is
        // required. Path.GetFullPath resolves ".." segments and symlinks.
        var basePath = Path.GetFullPath(AppContext.BaseDirectory);
        var fullPath = Path.GetFullPath(rawPath, AppContext.BaseDirectory);
        if (!fullPath.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !fullPath.Equals(basePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"VaultFilePath '{rawPath}' resolves outside the plugin base directory '{basePath}'. " +
                "Set VaultFilePath to a path within the plugin's base directory.");
        }

        _vaultFilePath = fullPath;

        context.Logger.LogInformation(
            "SampleVaultCredentialProvider initialized: vaultFilePath={Path}",
            _vaultFilePath);

        return Task.CompletedTask;
    }

    // ── ICredentialProvider ───────────────────────────────────────────────────

    public async Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        VaultFile? vault;
        try
        {
            vault = await ReadVaultFileAsync(ct);
        }
        catch (IOException ex)
        {
            // Log only safe fields (path, error type) — never the exception message,
            // which may contain file content.
            _host.Logger.LogWarning(
                "SampleVaultCredentialProvider: failed to read vault file {Path} ({Kind}); falling through",
                _vaultFilePath, ex.GetType().Name);
            return null;
        }
        catch (JsonException)
        {
            // JsonException.Message can contain a snippet of the offending JSON text,
            // which may include a credential value. Log only the path and kind.
            _host.Logger.LogWarning(
                "SampleVaultCredentialProvider: vault file {Path} contains invalid JSON; falling through",
                _vaultFilePath);
            return null;
        }

        if (vault is null)
            return null;

        // Look up the entry for this agent kind.
        var agentKey = agent.Value;
        if (!vault.Secrets.TryGetValue(agentKey, out var entry))
        {
            // No entry for this agent — let the chain fall through to the next provider.
            return null;
        }

        // A zero or negative TtlSeconds would produce an ExpiresAt already in the
        // past, causing a cache miss on every call. Treat it as a misconfiguration
        // and fall through so the chain tries the next provider.
        if (entry.TtlSeconds <= 0)
        {
            _host.Logger.LogWarning(
                "SampleVaultCredentialProvider: vault entry for '{Agent}' has TtlSeconds={Ttl} (must be > 0); skipping",
                agentKey, entry.TtlSeconds);
            return null;
        }

        var expiry = DateTimeOffset.UtcNow.Add(entry.Ttl);

        return new AgentCredential(
            Agent: agent,
            EnvironmentVariables: new Dictionary<string, string>(entry.EnvironmentVariables),
            Files: new Dictionary<string, string>(entry.Files))
        {
            // Signal to ChainedCredentialProvider to cache this credential until
            // it expires. After expiry, the next pickup re-reads the vault file.
            // In a real plugin, parse the vault's lease_duration here.
            ExpiresAt = expiry,
        };
    }

    private async Task<VaultFile?> ReadVaultFileAsync(CancellationToken ct)
    {
        if (!File.Exists(_vaultFilePath))
        {
            _host.Logger.LogDebug(
                "SampleVaultCredentialProvider: vault file not found at {Path}; skipping",
                _vaultFilePath);
            return null;
        }

        await using var stream = File.OpenRead(_vaultFilePath);
        return await JsonSerializer.DeserializeAsync<VaultFile>(stream, cancellationToken: ct);
    }
}

/// <summary>
/// JSON schema for the sample vault file.
/// </summary>
/// <example>
/// <code>
/// {
///   "secrets": {
///     "claude": {
///       "ttlSeconds": 900,
///       "environmentVariables": {
///         "CLAUDE_CODE_OAUTH_TOKEN": "sk-ant-oat01-..."
///       },
///       "files": {}
///     },
///     "codex": {
///       "ttlSeconds": 3600,
///       "environmentVariables": {
///         "OPENAI_API_KEY": "sk-..."
///       },
///       "files": {}
///     }
///   }
/// }
/// </code>
/// </example>
internal sealed class VaultFile
{
    public Dictionary<string, VaultEntry> Secrets { get; set; } = new();
}

internal sealed class VaultEntry
{
    public int TtlSeconds { get; set; } = 900;

    public TimeSpan Ttl => TimeSpan.FromSeconds(TtlSeconds);

    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();

    public Dictionary<string, string> Files { get; set; } = new();
}
