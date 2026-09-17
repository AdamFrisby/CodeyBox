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
    public static AgentKind Antigravity { get; } = new("antigravity");
    public static AgentKind Crock { get; } = new("crock");
    public static AgentKind Pi { get; } = new("pi");
    public static AgentKind Prime { get; } = new("prime");
    public static AgentKind Aider { get; } = new("aider");
    public static AgentKind Goose { get; } = new("goose");
    public static AgentKind Vibe { get; } = new("vibe");
    public static AgentKind CavemanCode { get; } = new("caveman");
    public static AgentKind Autohand { get; } = new("autohand");
    public static AgentKind Cline { get; } = new("cline");
    public static AgentKind Kilo { get; } = new("kilo");
    public static AgentKind Omp { get; } = new("omp");
    public static AgentKind Continue { get; } = new("continue");

    public override string ToString() => Value;
}
