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

    public override string ToString() => Value;
}
