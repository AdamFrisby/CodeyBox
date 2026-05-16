using System.Text.Json;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Reads the Claude OAuth credentials from a JSON file on every <see
/// cref="GetAsync"/> call. Designed for the host's
/// <c>~/.claude/.credentials.json</c>, which the local <c>claude</c> CLI
/// refreshes in-place. Backed by a shared
/// <see cref="CredentialFileSource"/> so the host's file-watcher picks up
/// out-of-band token rotations (operator running <c>claude</c> on the host,
/// scripted refresh, etc.) and every new sandbox is handed the fresh token
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

    private readonly CredentialFileSource _source;
    private readonly string _sandboxEnvVar;
    private readonly ILogger<ClaudeOAuthFileCredentialProvider>? _log;

    public ClaudeOAuthFileCredentialProvider(
        string filePath,
        string sandboxEnvVar,
        ILogger<ClaudeOAuthFileCredentialProvider>? log = null)
        : this(new CredentialFileSource(filePath, log), sandboxEnvVar, log)
    {
    }

    public ClaudeOAuthFileCredentialProvider(
        CredentialFileSource source,
        string sandboxEnvVar,
        ILogger<ClaudeOAuthFileCredentialProvider>? log = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _sandboxEnvVar = sandboxEnvVar ?? throw new ArgumentNullException(nameof(sandboxEnvVar));
        _log = log;
    }

    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        if (agent != AgentKind.Claude)
            return Task.FromResult<AgentCredential?>(null);

        var rawContents = _source.GetRaw();
        if (string.IsNullOrEmpty(rawContents))
        {
            _log?.LogDebug("Claude OAuth file not present or empty at {Path}; falling through", _source.FilePath);
            return Task.FromResult<AgentCredential?>(null);
        }

        string token;
        try
        {
            using var doc = JsonDocument.Parse(rawContents);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                !oauth.TryGetProperty("accessToken", out var tokenEl) ||
                tokenEl.ValueKind != JsonValueKind.String)
            {
                _log?.LogWarning("Claude OAuth file {Path} missing .claudeAiOauth.accessToken; falling through", _source.FilePath);
                return Task.FromResult<AgentCredential?>(null);
            }
            token = tokenEl.GetString() ?? "";
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Failed to parse Claude OAuth file {Path}; falling through", _source.FilePath);
            return Task.FromResult<AgentCredential?>(null);
        }

        if (string.IsNullOrEmpty(token))
            return Task.FromResult<AgentCredential?>(null);

        var env = new Dictionary<string, string>
        {
            [_sandboxEnvVar] = token,
            [OAuthJsonEnvVar] = rawContents,
        };
        return Task.FromResult<AgentCredential?>(new AgentCredential(AgentKind.Claude, env, new Dictionary<string, string>()));
    }
}
