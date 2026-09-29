using System.Xml;
using System.Xml.Linq;

namespace CodeyBox.TestSelectionProducer;

/// <summary>
/// XXE-safe XML load for untrusted repo files (csproj, slnx). DTD processing
/// and external entities are disabled at the reader, matching
/// <c>CoberturaParser</c>.
/// </summary>
internal static class SafeXml
{
    public static XDocument Load(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
        };
        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, settings);
        return XDocument.Load(reader);
    }

    public static XDocument LoadFile(string path, int maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException($"XML file '{path}' does not exist.", path);
        if (info.Length > maxBytes)
        {
            throw new TestSelectionBaselineProduceException(
                $"XML file '{path}' exceeds the {maxBytes}-byte cap.");
        }

        var xml = File.ReadAllText(path);
        if (xml.Length > maxBytes)
        {
            throw new TestSelectionBaselineProduceException(
                $"XML file '{path}' exceeds the {maxBytes}-byte cap.");
        }

        return Load(xml);
    }
}
