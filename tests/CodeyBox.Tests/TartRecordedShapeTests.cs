using CodeyBox.TartSandboxPlugin;

namespace CodeyBox.Tests;

/// <summary>
/// Recorded-shape fixtures for the Tart CLI contract. A live integration
/// test cannot run in CI (it needs an Apple Silicon macOS host with Tart,
/// pulls a ~25 GB image, and needs guest SSH), so these fixtures pin the
/// shapes the provider parses: `tart list --format json` (array of
/// name/state objects; extra fields ignored) and the plain-text fallback.
/// Any single-field production mutation in the parsers flips these red.
/// See <see cref="TartIntegrationTests"/> for the live counterpart and its
/// documented gate.
/// </summary>
public sealed class TartRecordedShapeTests
{
    private static string Fixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "tart", name);
        if (!File.Exists(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), "Fixtures", "tart", name);
        }

        return File.ReadAllText(path);
    }

    [Fact]
    public void ListJson_ParsesRecordedShape_IgnoringExtraFields()
    {
        var entries = TartShellCommand.ParseListJson(Fixture("tart-list.json"));

        Assert.Equal(3, entries.Count);
        Assert.Equal("codeybox-a1b2c3d4-1a2b3c", entries[0].Name);
        Assert.Equal("running", entries[0].State);
        Assert.Equal("codeybox-e5f6a7b8-9d8e7f", entries[1].Name);
        Assert.Equal("stopped", entries[1].State);
    }

    [Fact]
    public void ListJson_AcceptsObjectEnvelope()
    {
        var entries = TartShellCommand.ParseListJson(
            """{"vms": [{"name": "codeybox-x", "state": "running"}]}""");

        var entry = Assert.Single(entries);
        Assert.Equal("codeybox-x", entry.Name);
        Assert.Equal("running", entry.State);
    }

    [Fact]
    public void ListJson_RejectsNonArray()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => TartShellCommand.ParseListJson("""{"nope": 1}"""));
    }

    [Fact]
    public void ListText_ParsesRecordedShape_SkippingHeader()
    {
        var entries = TartShellCommand.ParseListText(Fixture("tart-list.txt"));

        Assert.Equal(3, entries.Count);
        Assert.Equal("codeybox-a1b2c3d4-1a2b3c", entries[0].Name);
        Assert.Equal("running", entries[0].State);
        Assert.Equal("stopped", entries[1].State);
    }

    [Fact]
    public void ListEntries_DriveManagedFiltering()
    {
        var entries = TartShellCommand.ParseListJson(Fixture("tart-list.json"));
        var managed = entries.Where(e => e.Name.StartsWith("codeybox-", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, managed.Count);
        Assert.DoesNotContain(managed, e => e.Name == "someone-else");
    }
}
