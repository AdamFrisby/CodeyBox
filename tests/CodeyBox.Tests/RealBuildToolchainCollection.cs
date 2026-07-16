using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Groups tests that drive a real build toolchain in a child process — a full
/// <c>dotnet build</c> or the repository's <c>build.sh</c> — each of which spawns
/// MSBuild/NuGet workers that saturate CPU and disk for the duration of the build.
/// <see cref="CollectionDefinitionAttribute.DisableParallelization"/> keeps these
/// heavy builds from overlapping the rest of the run, whose timing- and
/// subprocess-sensitive tests otherwise flake (timeouts, starved background
/// timers, aborted child processes) when a real build lands beside them in the
/// parallel pool.
/// </summary>
[CollectionDefinition("Real build toolchain", DisableParallelization = true)]
public sealed class RealBuildToolchainCollection
{
}
