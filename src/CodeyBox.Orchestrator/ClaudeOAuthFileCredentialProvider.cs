using System.Text.Json;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Reads the Claude OAuth token from a JSON file on every <see cref="GetAsync"/>
/// call. Designed for the host's <c>~/.claude/.credentials.json</c>, which the
/// local <c>claude</c> CLI refreshes in-place. Re-reading on each pickup picks
/// up rotated tokens without an orchestrator restart.
///
/// <para>File format expected:</para>
/// <code>
/// { "claudeAiOauth": { "accessToken": "sk-ant-oat01-..." } }
/// </code>
///
/// Only handles <see cref="AgentKind.Claude"/>; returns null for other agents
/// so a chained env-var provider can supply them.
/// </summary>
public sealed class ClaudeOAuthFileCredentialProvider : ICredentialProvider
{
    private readonly string _filePath;
    private readonly string _sandboxEnvVar;
    private readonly ILogger<ClaudeOAuthFileCredentialProvider>? _log;

    public ClaudeOAuthFileCredentialProvider(
        string filePath,
        string sandboxEnvVar,
        ILogger<ClaudeOAuthFileCredentialProvider>? log = null)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _sandboxEnvVar = sandboxEnvVar ?? throw new ArgumentNullException(nameof(sandboxEnvVar));
        _log = log;
    }

    public async Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        if (agent != AgentKind.Claude)
            return null;

        if (!File.Exists(_filePath))
        {
            _log?.LogDebug("Claude OAuth file not found at {Path}; falling through", _filePath);
            return null;
        }

        string token;
        try
        {
            await using var stream = File.OpenRead(_filePath);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                !oauth.TryGetProperty("accessToken", out var tokenEl) ||
                tokenEl.ValueKind != JsonValueKind.String)
            {
                _log?.LogWarning("Claude OAuth file {Path} missing .claudeAiOauth.accessToken; falling through", _filePath);
                return null;
            }
            token = tokenEl.GetString() ?? "";
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Failed to read Claude OAuth file {Path}; falling through", _filePath);
            return null;
        }

        if (string.IsNullOrEmpty(token))
            return null;

        var env = new Dictionary<string, string> { [_sandboxEnvVar] = token };
        return new AgentCredential(AgentKind.Claude, env, new Dictionary<string, string>());
    }
}
