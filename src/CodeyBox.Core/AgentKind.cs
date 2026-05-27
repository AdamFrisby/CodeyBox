namespace CodeyBox.Core;

/// <summary>
/// Identifier for a registered agent runner. Treated as an opaque string so
/// new agent integrations can be added without recompiling consumers.
/// </summary>
public readonly record struct AgentKind(string Value)
{
    public static AgentKind Claude { get; } = new("claude");
    public static AgentKind Copilot { get; } = new("copilot");
    public static AgentKind Codex { get; } = new("codex");
    public static AgentKind Gemini { get; } = new("gemini");
    public static AgentKind Cursor { get; } = new("cursor");
    public static AgentKind Opencode { get; } = new("opencode");

    public override string ToString() => Value;
}
