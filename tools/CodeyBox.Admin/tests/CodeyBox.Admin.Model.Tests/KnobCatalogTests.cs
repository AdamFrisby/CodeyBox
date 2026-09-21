using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

public sealed class KnobCatalogTests
{
    [Fact]
    public void ParseLines_ReadsKeyValueLinesLaterWins()
    {
        var (knobs, problem) = KnobCatalog.ParseLines("a=1\n\n b = two \na=3");

        Assert.Null(problem);
        Assert.Equal("3", knobs!["a"]);
        Assert.Equal("two", knobs["b"]);
    }

    [Theory]
    [InlineData("novalue")]
    [InlineData("=x")]
    [InlineData("k=")]
    public void ParseLines_RejectsMalformedLines(string text)
    {
        var (knobs, problem) = KnobCatalog.ParseLines(text);

        Assert.Null(knobs);
        Assert.Contains("key=value", problem);
    }

    [Fact]
    public void ParseLines_BlankIsNothing()
    {
        Assert.Null(KnobCatalog.ParseLines(null).Knobs);
        Assert.Null(KnobCatalog.ParseLines("  \n ").Knobs);
    }

    [Fact]
    public void Compose_OmitsDefaultsAndBlanksSendsChosenValues()
    {
        var (knobs, problem) = KnobCatalog.Compose(
            new Dictionary<string, string?> { ["changeScope"] = "surgical", ["plan"] = "off" },
            null);

        Assert.Null(problem);
        Assert.Equal(["changeScope"], knobs!.Keys);
        Assert.Equal("surgical", knobs["changeScope"]);
    }

    [Fact]
    public void Compose_NothingChosen_IsNull()
    {
        var (knobs, _) = KnobCatalog.Compose(new Dictionary<string, string?> { ["changeScope"] = "moderate" }, null);

        Assert.Null(knobs);
    }

    [Fact]
    public void Compose_ExtrasOverrideSelectsAndAreValidatedWhenBuiltIn()
    {
        var ok = KnobCatalog.Compose(
            new Dictionary<string, string?> { ["changeScope"] = "surgical" },
            new Dictionary<string, string> { ["changeScope"] = "refactor", ["custom"] = "x" });
        Assert.Null(ok.Problem);
        Assert.Equal("refactor", ok.Knobs!["changeScope"]);
        Assert.Equal("x", ok.Knobs["custom"]);

        var bad = KnobCatalog.Compose(
            new Dictionary<string, string?>(),
            new Dictionary<string, string> { ["plan"] = "maybe" });
        Assert.Null(bad.Knobs);
        Assert.Contains("Plan first must be one of off, on", bad.Problem);
    }

    [Fact]
    public void CapabilityTags_SplitTrimDedupe()
    {
        Assert.Equal(["gpu", "large-mem"], CapabilityTags.Parse("gpu, GPU; large-mem\n"));
        Assert.Null(CapabilityTags.Parse(" , ;"));
        Assert.Null(CapabilityTags.Parse(null));
    }
}
