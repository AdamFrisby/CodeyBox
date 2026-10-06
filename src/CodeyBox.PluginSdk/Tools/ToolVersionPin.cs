using CodeyBox.Core;

namespace CodeyBox.PluginSdk.Tools;

/// <summary>
/// Declared version pin for an external-tool auditor: the release the
/// invocation and its report shape were verified against. Declaring a pin via
/// <see cref="ExternalToolAuditorBase.VersionPin"/> makes
/// <see cref="ExternalToolAuditorBase.RunAsync"/> probe the tool before every
/// scan and fail closed as <see cref="AuditUnavailableException"/> when the
/// binary is missing, prints an unrecognised version string, or reports a
/// version other than the configured or default expectation — an unpinned
/// scanner changes its findings under the operator.
/// </summary>
/// <param name="PluginId">
/// Plugin id, used in the operator-facing hint that names the scoped-config
/// key (<c>CodeyBox:Plugins:&lt;id&gt;:ExpectedVersion</c>).
/// </param>
/// <param name="ConfiguredExpectedVersion">
/// Resolves the operator-configured <c>ExpectedVersion</c> per invocation so
/// scoped-config edits apply without a restart; may return null or blank to
/// fall back to <paramref name="DefaultExpectedVersion"/>.
/// </param>
/// <param name="DefaultExpectedVersion">
/// Release used when the operator does not configure one.
/// </param>
/// <param name="VersionProbeArguments">
/// Arguments appended after the probed binary (<paramref name="ProbedBinary"/>
/// or the tool name) for the probe — typically <c>["--version"]</c>, or
/// <c>["version"]</c> for subcommand-style CLIs. Empty falls back to
/// <c>--version</c>.
/// </param>
/// <param name="VersionExtractor">
/// Optional extraction of the reported version from the probe's stdout;
/// return null when no version token is present so the pin fails closed.
/// Default null uses the shared first-<c>major.minor.patch</c>-token
/// extraction — supply one when the tool's banner prints another
/// component's version first (e.g. govulncheck prints the Go toolchain's
/// version before <c>govulncheck@v…</c>, so the first token is the wrong
/// component to pin).
/// </param>
/// <param name="ProbedBinary">
/// Binary the version probe execs; null (the default) probes the audited
/// tool itself. Set it when the version-pinned component is not the driver
/// named by <see cref="ExternalToolAuditorBase.ToolName"/> — e.g.
/// <c>iwyu_tool</c> is a launcher script whose analysis engine is the
/// separate <c>include-what-you-use</c> binary. When it differs from the
/// tool name, the pin first presence-checks this binary the same way the
/// tool itself is checked, then probes it with
/// <paramref name="VersionProbeArguments"/>; failure messages name the
/// probed binary.
/// </param>
public sealed record ToolVersionPin(
    string PluginId,
    Func<string?> ConfiguredExpectedVersion,
    string DefaultExpectedVersion,
    IReadOnlyList<string> VersionProbeArguments,
    Func<string, string?>? VersionExtractor = null,
    string? ProbedBinary = null)
{
    /// <summary>
    /// Scoped-config key every external-tool auditor honors for overriding
    /// the pinned tool version.
    /// </summary>
    public const string ExpectedVersionKey = "ExpectedVersion";
}
