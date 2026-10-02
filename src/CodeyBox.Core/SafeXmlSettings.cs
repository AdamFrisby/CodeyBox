using System.Xml;

namespace CodeyBox.Core;

/// <summary>
/// The one XXE-hardening policy for parsing untrusted XML (repo-authored
/// project/solution files, coverage reports): DTDs prohibited, no external
/// resolver, no entity expansion. Shared by every XML consumer — e.g.
/// <c>CoberturaParser</c> in CodeyBox.Audit and the test-selection baseline
/// producer — so the hardening cannot drift between copies.
/// </summary>
public static class SafeXmlSettings
{
    /// <summary>Returns a fresh, hardened <see cref="XmlReaderSettings"/>.</summary>
    public static XmlReaderSettings Create() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
    };
}
