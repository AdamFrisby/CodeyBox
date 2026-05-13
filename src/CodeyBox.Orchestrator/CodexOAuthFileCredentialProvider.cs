using System.Text.Json;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Reads an explicitly configured Codex CLI subscription-mode auth file on
/// every <see cref="GetAsync"/> call and exposes
/// its contents as the env var <c>CODEX_AUTH_JSON</c> in the credential bundle.
///
/// <para>Unlike Claude (which accepts an OAuth token directly via
/// <c>CLAUDE_CODE_OAUTH_TOKEN</c>), the codex CLI hard-reads
/// <c>~/.codex/auth.json</c> in its target user's home and offers no env-var
/// alternative. The Codex agent runner picks up the env var, materialises the
/// file inside the sandbox at <c>~/.codex/auth.json</c>, then invokes codex.</para>
///
/// <para>Re-reading on each pickup picks up token rotations from the host's
/// codex CLI without an orchestrator restart.</para>
///
/// Only handles <see cref="AgentKind.Codex"/>; returns null for others so a
/// chained env-var provider can supply API-key based auth.
/// </summary>
public sealed class CodexOAuthFileCredentialProvider : ICredentialProvider
{
    private readonly string _filePath;
    private readonly ILogger<CodexOAuthFileCredentialProvider>? _log;

    public CodexOAuthFileCredentialProvider(
        string filePath,
        ILogger<CodexOAuthFileCredentialProvider>? log = null)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _log = log;
    }

    public async Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        if (agent != AgentKind.Codex)
            return null;

        if (!File.Exists(_filePath))
        {
            _log?.LogDebug("Codex OAuth file not found at {Path}; falling through", _filePath);
            return null;
        }

        string raw;
        try
        {
            raw = await File.ReadAllTextAsync(_filePath, ct);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Failed to read Codex OAuth file {Path}; falling through", _filePath);
            return null;
        }

        if (!CodexAuthJsonCredential.TryCreate(raw, out var credential))
        {
            _log?.LogWarning("Codex OAuth file {Path} has neither tokens.access_token nor OPENAI_API_KEY; falling through", _filePath);
            return null;
        }

        // Mount the host directory containing auth.json into the sandbox at the
        // canonical codex-CLI path. This makes the in-VM and host views of
        // auth.json the same file, so when the in-VM codex CLI refreshes the
        // OAuth pair, the rotated tokens land on the host immediately and
        // every subsequent sandbox starts with the fresh refresh_token instead
        // of a stale snapshot. Without this, the second sandbox in a series
        // hits "refresh_token already used" and the family is invalidated
        // server-side — breaking every codex caller until `codex login` runs.
        // Sandbox providers that don't support host-path bind-mounts ignore
        // this entry; the CODEX_AUTH_JSON env var still materialises the file
        // as a fallback (see CodexAgentRunner.PrepareSandboxAsync).
        var hostDir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(hostDir) && Directory.Exists(hostDir))
        {
            credential = credential with
            {
                Mounts = [new SandboxMount
                {
                    SandboxPath = CodexHomeSandboxPath,
                    HostPath = hostDir,
                    ReadOnly = false,
                }],
            };
        }
        return credential;
    }

    /// <summary>
    /// In-VM path where the codex CLI reads <c>auth.json</c>. The mount target
    /// matches <c>$HOME/.codex</c> for the default multipass instance user
    /// (ubuntu, uid 1000) and the bubblewrap sandbox HOME convention.
    /// </summary>
    internal const string CodexHomeSandboxPath = "/home/ubuntu/.codex";
}

/// <summary>
/// Reads a pre-materialised Codex auth JSON blob from <c>CODEX_AUTH_JSON</c>.
/// This supports deployments where the host process receives the auth file as
/// an environment secret rather than at <c>~/.codex/auth.json</c>.
/// </summary>
public sealed class CodexAuthJsonEnvironmentCredentialProvider : ICredentialProvider
{
    private const string DefaultEnvironmentVariable = "CODEX_AUTH_JSON";
    private readonly string _environmentVariable;
    private readonly ILogger<CodexAuthJsonEnvironmentCredentialProvider>? _log;

    public CodexAuthJsonEnvironmentCredentialProvider(
        ILogger<CodexAuthJsonEnvironmentCredentialProvider>? log = null)
        : this(DefaultEnvironmentVariable, log)
    {
    }

    public CodexAuthJsonEnvironmentCredentialProvider(
        string environmentVariable,
        ILogger<CodexAuthJsonEnvironmentCredentialProvider>? log = null)
    {
        if (string.IsNullOrWhiteSpace(environmentVariable))
            throw new ArgumentException("Environment variable name must not be empty", nameof(environmentVariable));
        _environmentVariable = environmentVariable;
        _log = log;
    }

    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        if (agent != AgentKind.Codex)
            return Task.FromResult<AgentCredential?>(null);

        var raw = Environment.GetEnvironmentVariable(_environmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
            return Task.FromResult<AgentCredential?>(null);

        if (!CodexAuthJsonCredential.TryCreate(raw, out var credential))
        {
            _log?.LogWarning("Environment variable {Variable} is not usable Codex auth JSON; falling through", _environmentVariable);
            return Task.FromResult<AgentCredential?>(null);
        }

        return Task.FromResult<AgentCredential?>(credential);
    }
}

internal static class CodexAuthJsonCredential
{
    public static bool TryCreate(string raw, out AgentCredential credential)
    {
        credential = null!;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var hasTokens = doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("tokens", out var t)
                && t.ValueKind == JsonValueKind.Object
                && t.TryGetProperty("access_token", out var at)
                && at.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(at.GetString());
            var hasApiKey = doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("OPENAI_API_KEY", out var k)
                && k.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(k.GetString());
            if (!hasTokens && !hasApiKey)
                return false;
        }
        catch (JsonException)
        {
            return false;
        }

        credential = new AgentCredential(
            AgentKind.Codex,
            new Dictionary<string, string> { ["CODEX_AUTH_JSON"] = raw },
            new Dictionary<string, string>());
        return true;
    }
}
