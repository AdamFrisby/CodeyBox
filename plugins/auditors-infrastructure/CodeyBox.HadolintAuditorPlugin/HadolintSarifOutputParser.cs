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
        var stdout = input.Stdout ?? string.Empty;
        if (input.ExitCode == 1
            && stdout.Contains(NoInputFilesSentinel, StringComparison.Ordinal)
            && !stdout.Contains("\"runs\"", StringComparison.Ordinal))
            return [];
        var findings = _inner.Parse(input);
        if (findings.Count == 0)
            return findings;
        // The auditor passes scan targets with a "./" prefix so dash-leading
        // names are never option-parsed; hadolint echoes that prefix back in
        // artifact URIs. Strip it so finding locations and ExcludePaths match
        // the repository-relative form every other auditor reports.
        return findings
            .Select(static finding => finding.Path is null
                ? finding
                : finding with { Path = StripDotSlashPrefix(finding.Path) })
            .ToList();
    }

    private static string StripDotSlashPrefix(string path)
    {
        var stripped = path;
        while (stripped.StartsWith("./", StringComparison.Ordinal))
            stripped = stripped[2..];
        return stripped.Length == 0 ? path : stripped;
    }
}
