using CodeyBox.Audit;

namespace CodeyBox.Tests;

public sealed class CoberturaParserTests
{
    private const string Sample =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.5" version="1.9">
          <sources>
            <source>/work</source>
          </sources>
          <packages>
            <package name="App">
              <classes>
                <class name="App.Foo" filename="src/Foo.cs">
                  <lines>
                    <line number="10" hits="3" />
                    <line number="11" hits="0" />
                  </lines>
                </class>
                <class name="App.Foo.Nested" filename="src/Foo.cs">
                  <lines>
                    <line number="11" hits="2" />
                    <line number="12" hits="0" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>
        """;

    [Fact]
    public void Parse_MergesClassesSharingAFileTakingMaxHits()
    {
        var report = CoberturaParser.Parse(Sample);

        Assert.Equal(new[] { "/work" }, report.Sources);
        var file = Assert.Single(report.Files);
        Assert.Equal("src/Foo.cs", file.FileName);
        Assert.Equal(3, file.LineHits[10]);
        Assert.Equal(2, file.LineHits[11]); // max(0, 2) across the two classes
        Assert.Equal(0, file.LineHits[12]);
    }

    [Fact]
    public void BuildLineMap_RelativeFilenameKeyedAsRepoRelative()
    {
        var report = CoberturaParser.Parse(Sample);

        var map = CoberturaParser.BuildLineMap([report], "/work");

        Assert.True(map.ContainsKey("src/Foo.cs"));
        Assert.Equal(0, map["src/Foo.cs"][12]);
    }

    [Fact]
    public void BuildLineMap_AbsoluteFilenameStrippedToRepoRelative()
    {
        var xml =
            """
            <coverage>
              <sources><source>/work</source></sources>
              <packages><package><classes>
                <class filename="/work/src/Bar.cs">
                  <lines><line number="5" hits="0" /></lines>
                </class>
              </classes></package></packages>
            </coverage>
            """;
        var report = CoberturaParser.Parse(xml);

        var map = CoberturaParser.BuildLineMap([report], "/work");

        Assert.True(map.ContainsKey("src/Bar.cs"));
        Assert.Equal(0, map["src/Bar.cs"][5]);
    }

    [Fact]
    public void BuildLineMap_MergesMultipleReportsMaxHits()
    {
        var reportA = CoberturaParser.Parse(
            "<coverage><sources><source>/work</source></sources><packages><package><classes>" +
            "<class filename=\"src/Foo.cs\"><lines><line number=\"11\" hits=\"0\" /></lines></class>" +
            "</classes></package></packages></coverage>");
        var reportB = CoberturaParser.Parse(
            "<coverage><sources><source>/work</source></sources><packages><package><classes>" +
            "<class filename=\"src/Foo.cs\"><lines><line number=\"11\" hits=\"4\" /></lines></class>" +
            "</classes></package></packages></coverage>");

        var map = CoberturaParser.BuildLineMap([reportA, reportB], "/work");

        Assert.Equal(4, map["src/Foo.cs"][11]);
    }

    [Fact]
    public void Parse_RejectsXxeExternalEntities()
    {
        var malicious =
            "<?xml version=\"1.0\"?>" +
            "<!DOCTYPE coverage [ <!ENTITY xxe SYSTEM \"file:///etc/passwd\"> ]>" +
            "<coverage><sources><source>&xxe;</source></sources></coverage>";

        // DTD processing is prohibited, so the parser throws rather than
        // resolving the external entity.
        Assert.ThrowsAny<System.Xml.XmlException>(() => CoberturaParser.Parse(malicious));
    }
}
