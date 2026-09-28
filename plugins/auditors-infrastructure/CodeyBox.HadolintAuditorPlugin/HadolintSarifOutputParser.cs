using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.HadolintAuditorPlugin;

/// <summary>
/// Parses hadolint's <c>-f sarif</c> report (SARIF 2.1.0 on stdout:
/// <c>runs[].results[]</c> with <c>ruleId</c> (<c>DLxxxx</c>/<c>SCxxxx</c>,
/// <c>DL1000</c> for Dockerfile parse errors), <c>level</c>,
/// <c>message.text</c>, and the first physical location's artifact URI plus
/// <c>region.startLine</c>) through the shared
/// <see cref="SarifToolOutputParser"/>, so rule identifiers and file/line
/// locations are preserved wherever the tool supplies them.
///
/// <para>One narrow exception: hadolint prints <c>Please provide a
/// Dockerfile</c> to stdout and exits <c>1</c> with no report when it was
/// invoked with zero file arguments — i.e. the auditor's own discovery
/// found no Dockerfiles and the operator configured no <c>Targets</c>. A
/// tree with nothing checkable is a clean pass with zero findings, not a
/// failure. The sentinel requires the exact diagnostic on an exit-<c>1</c>
/// run whose output carries no SARIF <c>"runs"</c> document; every other
/// report-less run (bad flags, unreadable files, crashes) falls through to
/// the shared parser, which throws <see cref="ExternalToolParseException"/>
/// — reported by the base as infrastructure, never as a pass.</para>
/// </summary>
internal sealed class HadolintSarifOutputParser : IExternalToolOutputParser
{
    /// <summary>
    /// Exact stdout diagnostic hadolint prints (exit 1, no SARIF) when
    /// invoked with zero file arguments.
    /// </summary>
    internal const string NoInputFilesSentinel = "Please provide a Dockerfile";

    private readonly SarifToolOutputParser _inner = new();

    public IReadOnlyList<ExternalToolFinding> Parse(ExternalToolParseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.ExitCode == 1
            && (input.Stdout ?? string.Empty).Contains(NoInputFilesSentinel, StringComparison.Ordinal)
            && !(input.Stdout ?? string.Empty).Contains("\"runs\"", StringComparison.Ordinal))
            return [];
        return _inner.Parse(input);
    }
}
