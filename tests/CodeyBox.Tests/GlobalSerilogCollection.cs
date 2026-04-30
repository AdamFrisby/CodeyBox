using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// xUnit collection grouping every test class that mutates the static
/// <see cref="Serilog.Log.Logger"/>: <see cref="AuditLogTests"/> sets it
/// directly to wire its test sink, and any test that builds a
/// <c>WebApplicationFactory&lt;Program&gt;</c> indirectly sets it via
/// Program.cs's Serilog bootstrap. Tests in this collection run
/// sequentially with respect to each other so they never observe each
/// other's logger and assertions stay deterministic. They still run in
/// parallel with non-Serilog tests in other collections.
/// </summary>
[CollectionDefinition("GlobalSerilog", DisableParallelization = true)]
public sealed class GlobalSerilogCollection
{
}
