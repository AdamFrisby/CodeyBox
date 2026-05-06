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

        // Validate it parses as JSON with at least the access-token shape we expect.
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
            {
                _log?.LogWarning("Codex OAuth file {Path} has neither tokens.access_token nor OPENAI_API_KEY; falling through", _filePath);
                return null;
            }
        }
        catch (JsonException ex)
        {
            _log?.LogWarning(ex, "Codex OAuth file {Path} is not valid JSON; falling through", _filePath);
            return null;
        }

        var env = new Dictionary<string, string> { ["CODEX_AUTH_JSON"] = raw };
        return new AgentCredential(AgentKind.Codex, env, new Dictionary<string, string>());
    }
}
