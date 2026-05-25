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
/// alternative. The Codex agent runner picks up the env var and materialises a
/// private copy inside the sandbox at <c>~/.codex/auth.json</c> before
/// invoking codex.</para>
///
/// <para>Re-reading on each pickup picks up token rotations from the host's
/// codex CLI without an orchestrator restart.</para>
///
/// Only handles <see cref="AgentKind.Codex"/>; returns null for others so a
/// chained env-var provider can supply API-key based auth.
/// </summary>
public sealed class CodexOAuthFileCredentialProvider : ICredentialProvider, IDisposable
{
    private readonly CredentialFileSource _source;
    private readonly ILogger<CodexOAuthFileCredentialProvider>? _log;
    private readonly bool _ownsSource;
    private bool _disposed;

    public CodexOAuthFileCredentialProvider(
        string filePath,
        ILogger<CodexOAuthFileCredentialProvider>? log = null,
        bool watch = true)
        : this(new CredentialFileSource(
            filePath ?? throw new ArgumentNullException(nameof(filePath)), log, watch), log, ownsSource: true)
    {
    }

    public CodexOAuthFileCredentialProvider(
        CredentialFileSource source,
        ILogger<CodexOAuthFileCredentialProvider>? log = null)
        : this(source, log, ownsSource: false)
    {
    }

    private CodexOAuthFileCredentialProvider(
        CredentialFileSource source,
        ILogger<CodexOAuthFileCredentialProvider>? log,
        bool ownsSource)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _log = log;
        _ownsSource = ownsSource;
    }

    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        if (agent != AgentKind.Codex)
            return Task.FromResult<AgentCredential?>(null);

        var raw = _source.GetRaw();
        if (string.IsNullOrWhiteSpace(raw))
        {
            _log?.LogDebug("Codex OAuth file not present or empty at {Path}; falling through", _source.FilePath);
            return Task.FromResult<AgentCredential?>(null);
        }

        if (!CodexAuthJsonCredential.TryCreate(raw, out var credential))
        {
            _log?.LogWarning("Codex OAuth file {Path} has neither tokens.access_token nor OPENAI_API_KEY; falling through", _source.FilePath);
            return Task.FromResult<AgentCredential?>(null);
        }

        return Task.FromResult<AgentCredential?>(credential);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsSource)
            _source.Dispose();
    }

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
            var tokens = CredentialFileTokenExtractor.ExtractCodexAccessTokens(doc.RootElement);
            var hasTokens = !string.IsNullOrEmpty(tokens.AccessToken);
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
