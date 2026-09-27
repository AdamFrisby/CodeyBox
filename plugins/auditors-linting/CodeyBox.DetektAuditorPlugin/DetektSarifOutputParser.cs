using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.DetektAuditorPlugin;

/// <summary>
/// Stream adapter, not a new format: detekt's console reports and its
/// "issues found" summary are printed to stdout, so the auditor pins the
/// SARIF report on stderr (<c>--report sarif:/dev/stderr</c>) to keep the
/// document parseable. This parser hands stderr to the shared
/// <see cref="SarifToolOutputParser"/> unchanged; anything detekt logs to
/// stderr before the report (analysis errors carry stack traces) breaks the
/// parse, which the base reports as infrastructure — fail-closed, never a
/// pass.
/// </summary>
public sealed class DetektSarifOutputParser : IExternalToolOutputParser
{
    private readonly SarifToolOutputParser _inner = new();

    /// <inheritdoc />
    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Stderr))
            throw new ExternalToolParseException(
                $"Tool '{input.ToolName}' produced no SARIF report on stderr.");
        return _inner.Parse(input with { Stdout = input.Stderr });
    }
}
