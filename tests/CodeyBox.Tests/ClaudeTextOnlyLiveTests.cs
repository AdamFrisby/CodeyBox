using CodeyBox.Agents.Claude;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Acceptance criterion 1 (end-to-end reproducer): under conditions where the
/// regular claude CLI works, the text-only <c>/v1/messages</c> path must also
/// return 2xx (not the HTTP 404 that broke the pickup-time rebase resolver).
///
/// <para>This hits the live Anthropic API, so it is opt-in: set
/// <c>CODEYBOX_CLAUDE_OAUTH_TOKEN</c> or <c>ANTHROPIC_API_KEY</c> to run it.
/// Without a credential the test is skipped (the mocked tests in
/// <see cref="ClaudeTextOnlyTests"/> cover request shape and alias resolution
/// offline). Running it against a working credential is the reproducer the
/// original bug report asked for: it exercises the real alias→canonical-id
/// resolution and proves the POST is accepted.</para>
/// </summary>
public sealed class ClaudeTextOnlyLiveTests
{
    private const string OAuthEnv = "CODEYBOX_CLAUDE_OAUTH_TOKEN";
    private const string ApiKeyEnv = "ANTHROPIC_API_KEY";

    [LiveClaudeFact]
    public async Task RunTextOnlyAsync_AgainstLiveApi_Returns2xxNot404()
    {
        var oauth = Environment.GetEnvironmentVariable(OAuthEnv);
        var apiKey = Environment.GetEnvironmentVariable(ApiKeyEnv);

        var env = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(oauth))
            env["CLAUDE_CODE_OAUTH_TOKEN"] = oauth;
        else
            env["ANTHROPIC_API_KEY"] = apiKey!;
        var credential = new AgentCredential(AgentKind.Claude, env, new Dictionary<string, string>());

        var runner = new ClaudeAgentRunner();

        // Undated CLI alias — the exact id the rebase resolver passes and the one
        // that 404'd against the raw Messages API before alias resolution.
        var result = await runner.RunTextOnlyAsync(
            "Reply with the single word: ok",
            credential,
            modelId: "claude-opus-4-8");

        Assert.True(result.Success, result.Summary);
        Assert.DoesNotContain("HTTP 404", result.Summary);
        Assert.False(string.IsNullOrWhiteSpace(result.Output), "expected non-empty model output");
    }

    private sealed class LiveClaudeFactAttribute : FactAttribute
    {
        public LiveClaudeFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OAuthEnv))
                && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyEnv)))
            {
                Skip = $"Set {OAuthEnv} or {ApiKeyEnv} to run the live Claude text-only reproducer.";
            }
        }
    }
}
