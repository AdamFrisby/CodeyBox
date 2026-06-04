namespace CodeyBox.Core;

/// <summary>
/// The kind of work an item represents. <see cref="Normal"/> items run the
/// full work → audit → merge → upstream pipeline. <see cref="CheckAndAct"/>
/// items run a single agent invocation in a sandbox that evaluates a yes/no
/// question against the target project repo and returns a structured verdict;
/// when the verdict matches the actionable condition, the orchestrator enqueues
/// a follow-up <see cref="Normal"/> work item against the same project.
/// <see cref="AgentControl"/> items are operator control-plane work items that
/// pause or resume one agent kind without launching an agent sandbox.
/// </summary>
public enum JobType
{
    Normal = 0,
    CheckAndAct = 1,
    AgentControl = 2,
}
