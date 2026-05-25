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
///   <see cref="CodeyBox.Agents.Claude.ClaudeAgentRunner"/> can materialise
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
/// <see cref="CodeyBox.Agents.Claude.ClaudeQuotaFailureDetector"/>); a fresh
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

        if (!TryBuildSanitisedBundle(rawContents, out var token, out var sanitisedBundle))
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

    /// <summary>
    /// Parses a host-side Claude credentials JSON document and produces the
    /// sanitised bundle ClaudeAgentRunner materialises into
    /// <c>~/.claude/.credentials.json</c> inside the sandbox. The bundle
    /// carries the access_token (and the <c>expiresAt</c> hint when present)
    /// but deliberately omits the refresh_token — see the class summary for
    /// the rationale on host-side-only refresh. Returns <c>false</c> for
    /// missing/malformed input so callers can fall through cleanly.
    /// </summary>
    public static bool TryBuildSanitisedBundle(
        string? rawContents,
        out string accessToken,
        out string sanitisedBundle)
    {
        accessToken = "";
        sanitisedBundle = "";
        if (string.IsNullOrEmpty(rawContents))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(rawContents);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                !oauth.TryGetProperty("accessToken", out var tokenEl) ||
                tokenEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            var token = tokenEl.GetString() ?? "";
            if (token.Length == 0)
                return false;
            accessToken = token;
            sanitisedBundle = BuildSandboxBundle(oauth, token);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string? ExtractAccessToken(string? rawContents)
        => TryBuildSanitisedBundle(rawContents, out var accessToken, out _)
            ? accessToken
            : null;

    private static string BuildSandboxBundle(JsonElement oauth, string token)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("claudeAiOauth");
            writer.WriteStartObject();
            writer.WriteString("accessToken", token);
            // Forward expiresAt verbatim (number or string) when present so the
            // in-VM CLI can short-circuit a doomed reuse of a stale token.
            if (oauth.TryGetProperty("expiresAt", out var expiresAt))
            {
                writer.WritePropertyName("expiresAt");
                expiresAt.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
