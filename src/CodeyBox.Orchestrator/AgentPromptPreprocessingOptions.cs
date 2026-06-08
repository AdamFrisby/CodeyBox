namespace CodeyBox.Orchestrator;

/// <summary>
/// Hot-reloadable prompt-preprocessing options.
/// </summary>
public sealed class AgentPromptPreprocessingOptions
{
    /// <summary>
    /// Repo-relative rules file prepended to every agent prompt when present.
    /// Defaults to the canonical agent-discoverable root file.
    /// </summary>
    public string ProjectRulesPath { get; set; } = "AGENTS.md";
}
