using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Reads Claude credentials from environment variables and exposes them under
/// the in-sandbox variable expected by the Claude CLI for that token type.
/// </summary>
public sealed class ClaudeEnvironmentCredentialProvider : ICredentialProvider
{
    private readonly string _codeyBoxEnvironmentVariable;
    private readonly string _anthropicEnvironmentVariable;

    public ClaudeEnvironmentCredentialProvider(
        string codeyBoxEnvironmentVariable = "CODEYBOX_CLAUDE_API_KEY",
        string anthropicEnvironmentVariable = "ANTHROPIC_API_KEY")
    {
        _codeyBoxEnvironmentVariable = codeyBoxEnvironmentVariable;
        _anthropicEnvironmentVariable = anthropicEnvironmentVariable;
    }

    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        if (agent != AgentKind.Claude)
            return Task.FromResult<AgentCredential?>(null);

        var configured = Environment.GetEnvironmentVariable(_codeyBoxEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var sandboxVariable = LooksLikeAnthropicApiKey(configured)
                ? "ANTHROPIC_API_KEY"
                : "CLAUDE_CODE_OAUTH_TOKEN";
            return Task.FromResult<AgentCredential?>(Credential(sandboxVariable, configured));
        }

        var conventional = Environment.GetEnvironmentVariable(_anthropicEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(conventional))
            return Task.FromResult<AgentCredential?>(Credential("ANTHROPIC_API_KEY", conventional));

        return Task.FromResult<AgentCredential?>(null);
    }

    private static bool LooksLikeAnthropicApiKey(string value) =>
        value.StartsWith("sk-ant-api", StringComparison.OrdinalIgnoreCase);

    private static AgentCredential Credential(string sandboxVariable, string value) =>
        new(AgentKind.Claude,
            new Dictionary<string, string> { [sandboxVariable] = value },
            new Dictionary<string, string>());
}
