namespace CodeyBox.Core;

/// <summary>
/// Sandbox CLI invocation built by an agent runner. Consumers call runner
/// methods; argv/environment/stdin construction remains owned by concrete
/// agent implementations.
/// </summary>
public sealed record AgentInvocation(
    IReadOnlyList<string> Argv,
    IReadOnlyDictionary<string, string>? ExtraEnvironment = null,
    string? Stdin = null);
