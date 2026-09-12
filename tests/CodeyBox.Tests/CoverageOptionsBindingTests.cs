using CodeyBox.Audit;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the real config-binding path from <c>CodeyBox:Audit:Coverage</c>
/// into <see cref="AuditSectionOptions"/> — the shape the host wires with
/// <c>Configure&lt;AuditSectionOptions&gt;</c>. Guards against binder breakage on
/// the nested record / exclusion-list shape.
/// </summary>
public sealed class CoverageOptionsBindingTests
{
    [Fact]
    public void BindsModeAndExclusionsFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:Audit:Coverage:Mode"] = "blocking",
                ["CodeyBox:Audit:Coverage:Exclusions:0:File"] = "src/Generated.cs",
                ["CodeyBox:Audit:Coverage:Exclusions:0:Line"] = "42",
                ["CodeyBox:Audit:Coverage:Exclusions:0:LineEnd"] = "45",
                ["CodeyBox:Audit:Coverage:Exclusions:0:Justification"] = "generated",
            })
            .Build();

        var section = new AuditSectionOptions();
        config.GetSection("CodeyBox:Audit").Bind(section);

        Assert.Equal(CoverageMode.Blocking, section.Coverage.ResolveMode());
        var exclusion = Assert.Single(section.Coverage.Exclusions);
        Assert.Equal("src/Generated.cs", exclusion.File);
        Assert.Equal(42, exclusion.Line);
        Assert.Equal(45, exclusion.LineEnd);
        Assert.True(exclusion.Covers("src/Generated.cs", 44));
        Assert.Equal("generated", exclusion.Justification);
    }

    [Fact]
    public void DefaultsToReportOnlyWhenUnset()
    {
        var section = new AuditSectionOptions();
        Assert.Equal(CoverageMode.ReportOnly, section.Coverage.ResolveMode());
    }
}
