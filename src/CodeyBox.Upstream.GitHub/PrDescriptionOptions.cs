using CodeyBox.Core;

namespace CodeyBox.Upstream.GitHub;

/// <summary>
/// Configuration for LLM-generated pull request descriptions.
/// Nest this inside <see cref="GitHubUpstreamOptions"/> as
/// <c>PrDescription</c>. Per-project snapshots of this object are rebuilt
/// from <see cref="ProjectPrDescription"/> on every upstream creation, so
/// edits reload with the project list without a restart.
/// </summary>
public sealed class PrDescriptionOptions
{
    /// <summary>
    /// When false the generator is skipped entirely and the static
    /// <c>BuildPrDescription</c> template is used. Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Selects the generation strategy. <c>Completion</c> (the default) calls a
    /// configured completion endpoint without a sandbox; <c>Agentic</c> runs the
    /// generator agent inside a provisioned sandbox. Default: <c>Completion</c>.
    /// </summary>
    public PrDescriptionStrategy Strategy { get; set; } = PrDescriptionStrategy.Completion;

    /// <summary>
    /// Agent kind to use for summary generation (matches a registered
    /// <see cref="CodeyBox.Core.IAgentRunner.Kind"/>). Default: "claude".
    /// </summary>
    public string GeneratorAgent { get; set; } = "claude";

    /// <summary>Optional model override forwarded to the agent runner.</summary>
    public string? GeneratorModelId { get; set; }

    /// <summary>
    /// Maximum UTF-8 byte size of the diff sent to the LLM. Diffs larger
    /// than this are truncated from the middle so both the first and last
    /// hunks are preserved; a "[… N bytes truncated …]" marker is inserted.
    /// Default: 32 768 bytes (32 KB).
    /// </summary>
    public int MaxDiffBytes { get; set; } = 32_768;

    /// <summary>
    /// Hard deadline for the entire generation round-trip (sandbox
    /// creation + agent response). PR creation never blocks longer than
    /// this; on expiry the generator falls back to the static template.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Container / VM image reference used when provisioning the minimal
    /// sandbox for the generator agent. Must match an image that has the
    /// generator agent CLI installed. Typically the same image used for
    /// work and merge phases. Required only by the <c>Agentic</c> strategy;
    /// ignored by the <c>Completion</c> strategy.
    /// </summary>
    public string SandboxImageReference { get; set; } = string.Empty;

    /// <summary>
    /// Hosts the generator sandbox is allowed to reach for LLM API calls.
    /// Default: Anthropic API endpoint. Override when using a proxy or a
    /// different agent (e.g. Gemini).
    /// </summary>
    public IReadOnlyList<string> AgentAllowedHosts { get; set; } = ["api.anthropic.com"];

    /// <summary>
    /// Absolute http(s) completion endpoint for the <c>Completion</c> strategy.
    /// Exactly this URL is POSTed to. Required only by the <c>Completion</c>
    /// strategy; ignored by the <c>Agentic</c> strategy.
    /// </summary>
    public string CompletionEndpoint { get; set; } = string.Empty;

    /// <summary>Model id sent in the completion request body.</summary>
    public string CompletionModel { get; set; } = string.Empty;

    /// <summary>Wire protocol for the completion call. Default: OpenAiChatCompletions.</summary>
    public CompletionWireApi CompletionWireApi { get; set; } = CompletionWireApi.OpenAiChatCompletions;

    /// <summary>
    /// Optional API key for the completion call. When unset, the first
    /// non-empty <see cref="CompletionApiKeyEnvVars"/> environment variable is used.
    /// </summary>
    public string? CompletionApiKey { get; set; }

    /// <summary>Environment variables searched (in order) for the completion API key.</summary>
    public IReadOnlyList<string> CompletionApiKeyEnvVars { get; set; } = ["CODEYBOX_PR_DESCRIPTION_API_KEY"];

    /// <summary>Provider max-output-tokens hint for the completion call. Default: 1024.</summary>
    public int CompletionMaxOutputTokens { get; set; } = 1024;

    /// <summary>
    /// Anthropic API version header sent when <see cref="CompletionWireApi"/> is
    /// AnthropicMessages. Default: "2023-06-01".
    /// </summary>
    public string CompletionAnthropicVersion { get; set; } = "2023-06-01";
}
