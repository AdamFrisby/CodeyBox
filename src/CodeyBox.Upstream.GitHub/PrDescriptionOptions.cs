namespace CodeyBox.Upstream.GitHub;

/// <summary>
/// Configuration for LLM-generated pull request descriptions.
/// Nest this inside <see cref="GitHubUpstreamOptions"/> as
/// <c>PrDescription</c>.
/// </summary>
public sealed class PrDescriptionOptions
{
    /// <summary>
    /// When false the generator is skipped entirely and the static
    /// <c>BuildPrDescription</c> template is used. Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

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
    /// work and merge phases.
    /// </summary>
    public string SandboxImageReference { get; set; } = string.Empty;

    /// <summary>
    /// Hosts the generator sandbox is allowed to reach for LLM API calls.
    /// Default: Anthropic API endpoint. Override when using a proxy or a
    /// different agent (e.g. Gemini).
    /// </summary>
    public IReadOnlyList<string> AgentAllowedHosts { get; set; } = ["api.anthropic.com"];
}
