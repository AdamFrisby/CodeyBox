namespace CodeyBox.Core;

/// <summary>
/// Sandbox CLI invocation built by an agent runner. Public so optional runner
/// capabilities can describe their resume command shape without depending on
/// <c>CliAgentRunnerBase</c> internals.
/// </summary>
public sealed record AgentInvocation(
    IReadOnlyList<string> Argv,
    IReadOnlyDictionary<string, string>? ExtraEnvironment = null,
    string? Stdin = null);
