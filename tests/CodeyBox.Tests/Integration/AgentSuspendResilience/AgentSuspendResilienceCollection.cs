namespace CodeyBox.Tests.Integration.AgentSuspendResilience;

/// <summary>
/// Serialises agent suspend/resume smoke tests — each case provisions a multipass VM.
/// </summary>
[CollectionDefinition("Agent suspend resilience", DisableParallelization = true)]
public sealed class AgentSuspendResilienceCollection;
