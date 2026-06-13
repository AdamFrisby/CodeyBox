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
/// { "claudeAiOauth": { "accessToken": "sk-ant-oat01-...", "refreshToken": "...", "expiresAt": 1234567890 } }
/// </code>
///
/// <para>The provider surfaces two env vars when the file parses:</para>
/// <list type="bullet">
///   <item><description>The legacy sandbox env var (default
///   <c>CLAUDE_CODE_OAUTH_TOKEN</c>) carrying just the access_token, for
///   flows that authenticate via Bearer token (API-key style).</description></item>
///   <item><description><c>CODEYBOX_CLAUDE_OAUTH_JSON</c> carrying a
///   <em>sanitised</em> bundle (access_token + expires_at only — the
///   refresh_token is stripped) so that
///   the Claude runner can materialise
///   <c>~/.claude/.credentials.json</c> inside the sandbox.</description></item>
/// </list>
///
/// <para><b>Why the refresh_token is stripped:</b> Anthropic's OAuth refresh
/// tokens are single-use. Shipping the refresh_token into every VM races the
/// host-side <c>claude</c> CLI: whichever party redeems it first invalidates
/// the other party's copy, producing intermittent 401s that the router treats
/// as agent-unavailable for the full observed-failure window. By keeping the
/// refresh_token host-side only, the host CLI is the sole party that can
/// refresh, so two parallel refresh attempts cannot collide. A VM iteration
/// that outlives the access_token's expiry will fail with a 401 (handled as a
/// transient/auth failure rather than as a quota-exhaustion signal — see
/// Claude quota failure detection); a fresh
/// iteration then picks up the host's currently-fresh token via the normal
/// credential pipeline.</para>
///
/// Only handles <see cref="AgentKind.Claude"/>; returns null for other agents
/// so a chained env-var provider can supply them.
/// </summary>
public sealed class ClaudeOAuthFileCredentialProvider : ICredentialProvider, IDisposable
{
    public const string OAuthJsonEnvVar = "CODEYBOX_CLAUDE_OAUTH_JSON";

    private readonly CredentialFileSource _source;
    private readonly string _sandboxEnvVar;
    private readonly ILogger<ClaudeOAuthFileCredentialProvider>? _log;
    private readonly bool _ownsSource;
    private bool _disposed;

    public ClaudeOAuthFileCredentialProvider(
        string filePath,
        string sandboxEnvVar,
        ILogger<ClaudeOAuthFileCredentialProvider>? log = null,
        bool watch = true)
        : this(new CredentialFileSource(filePath, log, watch), sandboxEnvVar, log, ownsSource: true)
    {
    }

    public ClaudeOAuthFileCredentialProvider(
        CredentialFileSource source,
        string sandboxEnvVar,
        ILogger<ClaudeOAuthFileCredentialProvider>? log = null)
        : this(source, sandboxEnvVar, log, ownsSource: false)
    {
    }

    private ClaudeOAuthFileCredentialProvider(
        CredentialFileSource source,
        string sandboxEnvVar,
        ILogger<ClaudeOAuthFileCredentialProvider>? log,
        bool ownsSource)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _sandboxEnvVar = sandboxEnvVar ?? throw new ArgumentNullException(nameof(sandboxEnvVar));
        _log = log;
        _ownsSource = ownsSource;
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

        if (!CredentialFileTokenExtractor.TryBuildClaudeSanitisedBundle(
            rawContents,
            out var token,
            out var sanitisedBundle))
        {
            _log?.LogWarning(
                "Claude OAuth file {Path} could not be parsed into a sanitised sandbox bundle; falling through",
                _source.FilePath);
            return Task.FromResult<AgentCredential?>(null);
        }

        var env = new Dictionary<string, string>
        {
            [_sandboxEnvVar] = token,
            [OAuthJsonEnvVar] = sanitisedBundle,
        };
        return Task.FromResult<AgentCredential?>(new AgentCredential(AgentKind.Claude, env, new Dictionary<string, string>()));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsSource)
            _source.Dispose();
    }
}
