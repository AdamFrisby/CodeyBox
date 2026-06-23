using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Resolves explicitly configured per-instance credentials into the same
/// sandbox bundles used by the legacy per-kind credential providers.
/// </summary>
public static class AgentInstanceCredentialResolver
{
    public static async Task<AgentCredential?> ResolveCredentialAsync(
        AgentMembership member,
        CancellationToken ct = default)
    {
        var reference = member.CredentialReference;
        if (reference is null || !reference.HasAnyReference)
            return null;

        var rawAuth = ResolveAuthJsonFromEnvironment(reference)
            ?? await ReadFileIfConfiguredAsync(reference.FilePath, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(rawAuth)
            && TryBuildCredentialFromAuthJson(member.Agent, rawAuth, reference, out var authCredential))
            return authCredential;

        if (!string.IsNullOrWhiteSpace(reference.TokenEnvironmentVariable))
        {
            var token = Environment.GetEnvironmentVariable(reference.TokenEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(token))
                return BuildCredentialFromToken(member.Agent, token, reference);
        }

        return null;
    }

    public static AgentQuotaCredentials? ResolveQuotaCredentials(
        AgentMembership member,
        Func<AgentQuotaCredentials?> fallback)
    {
        var reference = member.CredentialReference;
        if (reference is null || !reference.HasAnyReference)
            return fallback();

        var rawAuth = ResolveAuthJsonFromEnvironment(reference)
            ?? ReadFileIfConfigured(reference.FilePath);
        if (!string.IsNullOrWhiteSpace(rawAuth)
            && TryExtractQuotaCredentials(member.Agent, rawAuth, out var authCredentials))
            return authCredentials;

        if (!string.IsNullOrWhiteSpace(reference.TokenEnvironmentVariable))
        {
            var token = Environment.GetEnvironmentVariable(reference.TokenEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(token))
                return new AgentQuotaCredentials(token);
        }

        return fallback();
    }

    private static bool TryBuildCredentialFromAuthJson(
        AgentKind agent,
        string raw,
        AgentCredentialReference reference,
        out AgentCredential credential)
    {
        credential = null!;
        if (agent == AgentKind.Claude)
        {
            if (!CredentialFileTokenExtractor.TryBuildClaudeSanitisedBundle(raw, out var token, out var bundle))
                return false;
            var sandboxEnv = string.IsNullOrWhiteSpace(reference.SandboxEnvironmentVariable)
                ? "CLAUDE_CODE_OAUTH_TOKEN"
                : reference.SandboxEnvironmentVariable!;
            credential = new AgentCredential(
                AgentKind.Claude,
                new Dictionary<string, string>
                {
                    [sandboxEnv] = token,
                    [ClaudeOAuthFileCredentialProvider.OAuthJsonEnvVar] = bundle,
                },
                new Dictionary<string, string>());
            return true;
        }

        if (agent == AgentKind.Codex)
            return CodexAuthJsonCredential.TryCreate(raw, out credential);

        if (agent == AgentKind.Gemini)
        {
            var env = new Dictionary<string, string>
            {
                [GeminiOAuthFileCredentialProvider.OAuthCredsEnvVar] = raw,
                [GeminiOAuthFileCredentialProvider.SettingsEnvVar] =
                    ReadFileIfConfigured(reference.SettingsFilePath) ?? DefaultGeminiSettingsJson,
            };
            credential = new AgentCredential(AgentKind.Gemini, env, new Dictionary<string, string>());
            return true;
        }

        if (agent == AgentKind.Cursor)
        {
            credential = new AgentCredential(
                AgentKind.Cursor,
                new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = raw },
                new Dictionary<string, string>());
            return true;
        }

        if (agent == AgentKind.Opencode)
        {
            var env = new Dictionary<string, string> { ["OPENCODE_AUTH_JSON"] = raw };
            if (!string.IsNullOrWhiteSpace(reference.DestinationPath))
                env["OPENCODE_AUTH_DEST_PATH"] = reference.DestinationPath!;
            credential = new AgentCredential(AgentKind.Opencode, env, new Dictionary<string, string>());
            return true;
        }

        if (agent == AgentKind.Antigravity)
        {
            // The agy CLI authenticates from a token bundle written to
            // ~/.gemini/antigravity-cli/antigravity-oauth-token (its
            // fileTokenStorage path when no system keyring is present). Ship the
            // bundle verbatim — the refresh_token MUST be kept so the in-VM agy
            // can refresh the short-lived access_token; the host authenticates
            // from the keyring, a separate store. See TryBuildAntigravityTokenBundle.
            if (!CredentialFileTokenExtractor.TryBuildAntigravityTokenBundle(raw, out var bundle))
                return false;
            credential = new AgentCredential(
                AgentKind.Antigravity,
                new Dictionary<string, string> { [AntigravityConstants.OAuthCredsEnvVar] = bundle },
                new Dictionary<string, string>());
            return true;
        }

        // Crock: deliberately falls through to the global credential provider
        // chain (CrockEnvironmentCredentialProvider). The crock runner credential
        // is a TWO-PIECE bundle — the CROCK_CONFIG_JSON env var AND a bind-mount
        // exposing the host-side `crock daemon` Unix socket's parent directory —
        // and only the global provider has access to the sandbox-side
        // CrockSandboxOptions needed to add that bind-mount. Per-instance
        // CredentialReference still affects the QUOTA probe (see
        // TryExtractQuotaCredentials below) so two crock members with distinct
        // Anthropic keys probe with the correct token; the runner-side env+mount
        // bundle is a sandbox-global concern.
        return false;
    }

    private static AgentCredential BuildCredentialFromToken(
        AgentKind agent,
        string token,
        AgentCredentialReference reference)
    {
        var sandboxEnv = reference.SandboxEnvironmentVariable;
        if (string.IsNullOrWhiteSpace(sandboxEnv))
            sandboxEnv = DefaultTokenSandboxEnvironmentVariable(agent, token);

        return new AgentCredential(
            agent,
            new Dictionary<string, string> { [sandboxEnv] = token },
            new Dictionary<string, string>());
    }

    private static string DefaultTokenSandboxEnvironmentVariable(AgentKind agent, string token)
    {
        if (agent == AgentKind.Claude)
            return token.StartsWith("sk-ant-api", StringComparison.OrdinalIgnoreCase)
                ? "ANTHROPIC_API_KEY"
                : "CLAUDE_CODE_OAUTH_TOKEN";
        if (agent == AgentKind.Codex)
            return "OPENAI_API_KEY";
        if (agent == AgentKind.Gemini)
            return "GEMINI_API_KEY";
        if (agent == AgentKind.Copilot)
            return "GH_TOKEN";
        return "AGENT_TOKEN";
    }

    private static bool TryExtractQuotaCredentials(
        AgentKind agent,
        string raw,
        out AgentQuotaCredentials credentials)
    {
        credentials = new AgentQuotaCredentials(null);
        if (agent == AgentKind.Claude)
        {
            var token = CredentialFileTokenExtractor.ExtractClaudeAccessToken(raw);
            if (string.IsNullOrWhiteSpace(token)) return false;
            credentials = new AgentQuotaCredentials(token);
            return true;
        }

        if (agent == AgentKind.Codex)
        {
            var tokens = CredentialFileTokenExtractor.ExtractCodexAccessTokens(raw);
            if (string.IsNullOrWhiteSpace(tokens.AccessToken)) return false;
            credentials = new AgentQuotaCredentials(tokens.AccessToken, tokens.AccountId);
            return true;
        }

        if (agent == AgentKind.Gemini)
        {
            var token = CredentialFileTokenExtractor.ExtractGeminiAccessToken(raw);
            if (string.IsNullOrWhiteSpace(token)) return false;
            credentials = new AgentQuotaCredentials(token);
            return true;
        }

        if (agent == AgentKind.Cursor)
        {
            var token = CredentialFileTokenExtractor.ExtractCursorAccessToken(raw);
            if (string.IsNullOrWhiteSpace(token)) return false;
            credentials = new AgentQuotaCredentials(token);
            return true;
        }

        if (agent == AgentKind.Antigravity)
        {
            // The agy CLI ships a Google OAuth creds JSON of the same shape as
            // gemini-cli; reuse the Gemini extractor so a single per-instance
            // file works for either CLI.
            var token = CredentialFileTokenExtractor.ExtractGeminiAccessToken(raw);
            if (string.IsNullOrWhiteSpace(token)) return false;
            credentials = new AgentQuotaCredentials(token);
            return true;
        }

        if (agent == AgentKind.Crock)
        {
            // CrockCode's config.json carries an Anthropic API key under
            // `anthropic_api_key`. The Crock quota probe is keyed by token, so a
            // per-instance file (two crock members with distinct keys) MUST
            // resolve to its own AgentQuotaCredentials rather than silently
            // collapsing onto the shared CODEYBOX_CROCK_CONFIG_JSON env-var key
            // (which would defeat per-instance routing and pool-with-yourself).
            var token = CredentialFileTokenExtractor.ExtractCrockAnthropicApiKey(raw);
            if (string.IsNullOrWhiteSpace(token)) return false;
            credentials = new AgentQuotaCredentials(token);
            return true;
        }

        return false;
    }

    private static string? ResolveAuthJsonFromEnvironment(AgentCredentialReference reference)
    {
        if (string.IsNullOrWhiteSpace(reference.AuthJsonEnvironmentVariable))
            return null;
        var raw = Environment.GetEnvironmentVariable(reference.AuthJsonEnvironmentVariable);
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private static async Task<string?> ReadFileIfConfiguredAsync(string? path, CancellationToken ct)
    {
        var resolved = ExpandUserHome(path);
        if (resolved is null || !File.Exists(resolved))
            return null;
        return await File.ReadAllTextAsync(resolved, ct).ConfigureAwait(false);
    }

    private static string? ReadFileIfConfigured(string? path)
    {
        var resolved = ExpandUserHome(path);
        return resolved is not null && File.Exists(resolved)
            ? File.ReadAllText(resolved)
            : null;
    }

    private static string? ExpandUserHome(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var trimmed = path.Trim();
        if (!trimmed.StartsWith("~/", StringComparison.Ordinal))
            return trimmed;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            trimmed[2..]);
    }

    private const string DefaultGeminiSettingsJson = """
        {
          "security": {
            "auth": {
              "selectedType": "oauth-personal"
            }
          }
        }
        """;
}
