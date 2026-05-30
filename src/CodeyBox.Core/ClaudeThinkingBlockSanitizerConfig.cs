namespace CodeyBox.Core;

/// <summary>
/// Hot-reloadable configuration snapshot for the Claude thinking-block
/// transcript sanitizer. Bound from <c>CodeyBox:ClaudeThinkingBlockSanitizer</c>.
///
/// <para>
/// Gates the pre-run transcript sanitisation and the reactive 400 retry
/// path in <see cref="CodeyBox.Agents.Claude.ClaudeAgentRunner"/>. Disable
/// this flag once the upstream Anthropic fix ships.
/// </para>
/// </summary>
public sealed class ClaudeThinkingBlockSanitizerConfig
{
    /// <summary>
    /// Master switch. Default <c>true</c> while the upstream thinking-block
    /// immutability bug (anthropics/claude-code #63335 and friends) is open.
    /// Set <c>false</c> to disable all transcript mutation.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
