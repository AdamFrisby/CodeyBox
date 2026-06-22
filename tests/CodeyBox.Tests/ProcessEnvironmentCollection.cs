using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Groups tests that depend on process-wide environment variables. Environment
/// mutation is global to the test process, so these tests must not overlap.
/// </summary>
[CollectionDefinition("Process environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
}
