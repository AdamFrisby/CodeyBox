using System.Text.Json;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Reads the Claude OAuth credentials from a JSON file on every <see
/// cref="GetAsync"/> call. Designed for the host's
/// <c>~/.claude/.credentials.json</c>, which the local <c>claude</c> CLI
/// refreshes in-place. Re-reading on each pickup picks up rotated tokens
/// without an orchestrator restart.
///
/// <para>File format expected:</para>
/// <code>
/// { "claudeAiOauth": { "accessToken": "sk-ant-oat01-...", "refreshToken": "..." } }
/// </code>
///
/// <para>The provider surfaces two env vars when the file parses:</para>
/// <list type="bullet">
///   <item><description>The legacy sandbox env var (default
///   <c>CLAUDE_CODE_OAUTH_TOKEN</c>) carrying just the access_token, for
///   flows that authenticate via Bearer token (API-key style).</description></item>
///   <item><description><c>CODEYBOX_CLAUDE_OAUTH_JSON</c> carrying the full
///   file contents (including refresh_token) so that
///   <see cref="CodeyBox.Agents.Claude.ClaudeAgentRunner"/> can materialise
///   <c>~/.claude/.credentials.json</c> inside the sandbox. The in-VM CLI
///   then auto-rotates as needed instead of 401-ing when the host rotates
///   the access_token mid-run.</description></item>
/// </list>
///
/// Only handles <see cref="AgentKind.Claude"/>; returns null for other agents
/// so a chained env-var provider can supply them.
/// </summary>
public sealed class ClaudeOAuthFileCredentialProvider : ICredentialProvider
{
    public const string OAuthJsonEnvVar = "CODEYBOX_CLAUDE_OAUTH_JSON";

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

        // Read the raw bytes once so the JSON we ship to the sandbox is
        // identical to what we parse — avoiding a torn read if the host CLI
        // rotates the file mid-call.
        string rawContents;
        try
        {
            rawContents = await File.ReadAllTextAsync(_filePath, ct);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Failed to read Claude OAuth file {Path}; falling through", _filePath);
            return null;
        }

        string token;
        try
        {
            using var doc = JsonDocument.Parse(rawContents);
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
            _log?.LogWarning(ex, "Failed to parse Claude OAuth file {Path}; falling through", _filePath);
            return null;
        }

        if (string.IsNullOrEmpty(token))
            return null;

        var env = new Dictionary<string, string>
        {
            [_sandboxEnvVar] = token,
            [OAuthJsonEnvVar] = rawContents,
        };
        return new AgentCredential(AgentKind.Claude, env, new Dictionary<string, string>());
    }
}
