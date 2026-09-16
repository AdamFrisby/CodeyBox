namespace CodeyBox.Agents.Prime;

/// <summary>
/// Operator settings for the Prime Agent (<c>prime-agent</c>) runner. Bound
/// from <c>CodeyBox:Prime</c>. Hot-reloadable through
/// <c>IOptionsMonitor</c>: the runner resolves these per dispatch.
/// </summary>
public sealed class PrimeSectionOptions
{
    /// <summary>
    /// Default model provider passed as <c>--provider</c>. Matches the
    /// shipped credential mapping (host <c>CODEYBOX_PRIME_API_KEY</c> to
    /// sandbox-side <c>OPENROUTER_API_KEY</c>); operators fronting a
    /// different provider set this to that provider's id (e.g.
    /// <c>anthropic</c>) and extend the credential mapping with its key
    /// variable. Verified against prime-agent 0.9.5:
    /// <c>--provider openrouter --model nvidia/nemotron-3.5-lightning:free</c>
    /// dispatches against the OpenRouter catalog shown by
    /// <c>prime-agent model list</c>.
    /// </summary>
    public const string DefaultProvider = "openrouter";

    /// <summary>
    /// Model provider passed as <c>--provider</c>. Blank or whitespace omits
    /// the flag and lets the CLI resolve its own startup default.
    /// </summary>
    public string Provider { get; set; } = DefaultProvider;
}
