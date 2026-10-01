using System.Xml;
using System.Xml.Linq;
using CodeyBox.Core;

namespace CodeyBox.TestSelectionProducer;

/// <summary>
/// XXE-safe XML load for untrusted repo files (csproj, slnx), hardened via
/// the shared <see cref="SafeXmlSettings"/> policy.
/// </summary>
internal static class SafeXml
{
    public static XDocument Load(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, SafeXmlSettings.Create());
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

        try
        {
            return Load(xml);
        }
        catch (XmlException ex)
        {
            throw new TestSelectionBaselineProduceException(
                $"XML file '{path}' is malformed: {ex.Message}", ex);
        }
    }
}
