using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// ACP bridge fixtures redirect a process-wide static emitter and install
/// process-wide bridge hooks, so they must not overlap with the parallel
/// suite.
/// </summary>
[CollectionDefinition("ACP bridge", DisableParallelization = true)]
public sealed class AcpBridgeCollection
{
}
