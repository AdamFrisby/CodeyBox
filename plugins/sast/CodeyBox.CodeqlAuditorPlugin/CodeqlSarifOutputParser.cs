using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.CodeqlAuditorPlugin;

/// <summary>
/// Parser for the SARIF dialect <c>codeql database analyze</c> emits.
/// Individual results carry no <c>level</c> — CodeQL records each query's
/// severity once per run in <c>tool.driver.rules[]</c> (as
/// <c>defaultConfiguration.level</c>, with the query's own
/// <c>properties["problem.severity"]</c> as fallback) — so this parser
/// recovers the per-result severity token from that rule metadata via the
/// shared <see cref="SarifRuleMetadataOutputParser"/> decorator (falling
/// back to the SARIF default of <c>"warning"</c>) and delegates the shape
/// parsing (rule id, message, location) to <see cref="SarifToolOutputParser"/>.
/// The recovered token is still mapped through the auditor's declared
/// <see cref="ExternalToolSeverityMapping"/>; raw tool levels never reach
/// findings.
/// </summary>
public sealed class CodeqlSarifOutputParser : IExternalToolOutputParser
{
    private readonly SarifRuleMetadataOutputParser _inner;

    public CodeqlSarifOutputParser(int maxResults = SarifToolOutputParser.DefaultMaxResults)
        => _inner = new SarifRuleMetadataOutputParser(
            new SarifToolOutputParser(maxResults), "problem.severity");

    /// <inheritdoc />
    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
        => _inner.Parse(input);
}
