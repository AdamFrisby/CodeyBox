using System.Text.Json;
using CodeyBox.Sandbox.Incus;

namespace CodeyBox.Tests;

/// <summary>
/// The instance-inventory parse must survive entries Incus lists while they are mid-create or
/// mid-delete. Aborting the whole listing over one such entry failed unrelated work items — including
/// mid-audit — and broke the reaper sweeps.
/// </summary>
public sealed class IncusInventoryParseTests
{
    [Fact]
    public void EntryWithoutConfig_ParsesWithAnEmptyConfig_RatherThanThrowing()
    {
        // Regression: this threw JsonException("Incus inventory entries must contain a JSON object
        // property named 'config'."), which surfaced as a work-item FAIL during CollectFindingsBatchAsync.
        const string json = """
        [
          {"name":"codeybox-transient","status":"","type":"virtual-machine"},
          {"name":"codeybox-real","status":"Running","type":"virtual-machine",
           "config":{"user.codeybox.managed":"true","user.codeybox.kind":"work"}}
        ]
        """;

        var instances = IncusSandboxProvider.ParseInstances(json);

        Assert.Equal(2, instances.Count);
        Assert.Empty(instances[0].Config);
        // The well-formed entry alongside it is still parsed — one transient entry must not cost the
        // rest of the inventory.
        Assert.Equal("true", instances[1].Config["user.codeybox.managed"]);
    }

    [Fact]
    public void EntryWithNonObjectConfig_IsTreatedAsEmpty()
    {
        const string json = """[{"name":"x","status":"Running","type":"virtual-machine","config":null}]""";

        Assert.Empty(Assert.Single(IncusSandboxProvider.ParseInstances(json)).Config);
    }

    [Fact]
    public void AConfiglessEntryCanNeverBeMistakenForOwned()
    {
        // The safety property that makes skipping sound: ownership is positive-only, requiring
        // managed=true. An empty config therefore cannot match, so tolerating it cannot cause us to
        // act on someone else's instance.
        var instance = Assert.Single(
            IncusSandboxProvider.ParseInstances(
                """[{"name":"not-ours","status":"Running","type":"virtual-machine"}]"""));

        Assert.False(instance.Config.ContainsKey("user.codeybox.managed"));
    }

    [Fact]
    public void MissingName_StillThrows()
    {
        // Shape violations that are NOT transient must still be loud — this guard is unrelated to the
        // mid-create/mid-delete case and should keep failing fast.
        Assert.Throws<JsonException>(() =>
            IncusSandboxProvider.ParseInstances("""[{"status":"Running","type":"virtual-machine"}]"""));
    }

    [Fact]
    public void NonStringConfigValue_StillThrows()
    {
        Assert.Throws<JsonException>(() =>
            IncusSandboxProvider.ParseInstances(
                """[{"name":"x","status":"Running","type":"virtual-machine","config":{"k":123}}]"""));
    }
}
