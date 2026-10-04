using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.RoslynatorAuditorPlugin;

/// <summary>
/// Parses <c>roslynator analyze</c> output when the report rides stdout
/// (<c>--output /dev/stdout --output-format sarif</c>).
///
/// <para>Roslynator only writes the report file when it has diagnostics to
/// report: a clean tree exits <c>0</c> with the human-readable console log
/// (<c>0 diagnostics found</c>) on stdout and no SARIF document at all. That
/// console text is the tool's clean verdict — not a parse failure — so exit
/// <c>0</c> without a SARIF-looking payload yields zero findings. The SARIF
/// payload itself is parsed by the shared <see cref="SarifToolOutputParser"/>
/// (rule id, level, message, artifact URI, start line); this class only
/// decides which of the two shapes stdout carries. A findings-producing exit
/// (<c>1</c>, or <c>0</c> via the operator's
/// <c>--return-success-on-diagnostics</c>) without a SARIF payload throws
/// <see cref="ExternalToolParseException"/>, which the base reports as
/// infrastructure — a diagnostics run that lost its report is never a
/// pass.</para>
/// </summary>
internal sealed class RoslynatorOutputParser : IExternalToolOutputParser
{
    private readonly SarifToolOutputParser _sarif = new();

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Roslynator writes the SARIF document with a UTF-8 byte-order mark;
        // strip it before shape-sniffing so the payload test sees the
        // document, not the marker.
        var stdout = (input.Stdout ?? string.Empty).TrimStart('\uFEFF');
        if (stdout.TrimStart().StartsWith("{", StringComparison.Ordinal))
            return _sarif.Parse(input with { Stdout = stdout });

        if (input.ExitCode == 0)
            return [];

        throw new ExternalToolParseException(
            $"Tool '{input.ToolName}' exited {input.ExitCode} without writing a SARIF report to stdout.");
    }
}
