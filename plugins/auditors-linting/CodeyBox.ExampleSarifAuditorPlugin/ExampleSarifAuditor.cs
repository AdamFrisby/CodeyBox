using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.ExampleSarifAuditorPlugin;

/// <summary>
/// Worked example of an external-tool auditor built on
/// <see cref="ExternalToolAuditorBase"/>. It wraps a SARIF-emitting scanner
/// (<c>example-scanner</c>): the author supplies the tool name, its arguments,
/// a SARIF parser, and a severity map, and the base supplies invocation with a
/// bounded timeout, exit-code classification, finding identity, and per-auditor
/// configuration. Copy this file as the starting point for a real scanner.
/// </summary>
[CodeyBoxPlugin(
    id: "codeybox.example-sarif",
    displayName: "CodeyBox: Example SARIF Scanner",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "example-scanner",
    InstallHint = "install example-scanner from your toolchain feed")]
public sealed class ExampleSarifAuditor : ExternalToolAuditorBase, IPluginInitializer
{
    public const string PluginId = "codeybox.example-sarif";

    // This scanner exits 1 when it reports findings, so both 0 and 1 are
    // findings-producing verdicts. Any other exit means the tool could not run.
    private static readonly ExternalToolAuditorOptions AuditorDefaults = new()
    {
        FindingsExitCodes = new HashSet<int> { 0, 1 },
    };

    private Func<ExternalToolAuditorOptions> _optionsAccessor = () => AuditorDefaults;

    public override string Name => "codeybox:example-sarif";

    protected override string ToolName => "example-scanner";

    protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();

    // This scanner reports "high"/"medium"/"low": high blocks the merge,
    // medium is advisory, low is informational.
    protected override ExternalToolSeverityMapping SeverityMapping { get; } =
        new(new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["high"] = AuditSeverity.Error,
            ["medium"] = AuditSeverity.Warning,
            ["low"] = AuditSeverity.Info,
        }, AuditSeverity.Warning);

    protected override Func<ExternalToolAuditorOptions> OptionsAccessor => _optionsAccessor;

    protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions options)
        => ["scan", "--format", "sarif", "."];

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = context.ScopedConfig;
        _optionsAccessor = () => ExternalToolAuditorOptions.Bind(scoped, AuditorDefaults);
        return Task.CompletedTask;
    }
}
