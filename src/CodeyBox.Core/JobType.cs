namespace CodeyBox.Core;

/// <summary>
/// The kind of work an item represents. <see cref="Normal"/> items run the
/// full work → audit → merge → upstream pipeline. <see cref="CheckAndAct"/>
/// items run a single agent invocation in a sandbox that evaluates a yes/no
/// question against the target project repo and returns a structured verdict;
/// when the verdict matches the actionable condition, the orchestrator enqueues
/// a follow-up <see cref="Normal"/> work item against the same project.
/// </summary>
public enum JobType
{
    Normal = 0,
    CheckAndAct = 1,
}
