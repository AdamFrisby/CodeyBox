using CodeyBox.PluginSdk.Tools;

namespace CodeyBox.GlslangAuditorPlugin;

/// <summary>
/// glslang backend for the shared shader-validation family: validates
/// configured standalone GLSL/ESSL targets with <c>glslangValidator</c> and
/// parses its text diagnostics via <see cref="GlslangDiagnosticParser"/>.
/// Owns only what differs per tool — the binary, the pinned release, the
/// flag construction, and the parser. Target/stage/environment resolution
/// stays in <see cref="ShaderValidationSupport"/> so a later SPIR-V backend
/// reuses the same family seam.
/// </summary>
internal sealed class GlslangValidationBackend : IShaderValidationBackend
{
    /// <inheritdoc />
    public string BackendId => "glslang";

    /// <inheritdoc />
    public string ToolName => "glslangValidator";

    /// <summary>
    /// glslang release the invocation and report shape were checked against
    /// (upstream <c>glslang</c> tag <c>14.3.0</c>; <c>glslangValidator
    /// --version</c> prints <c>Glslang Version: 14.3.0</c> as its first
    /// version token, which the shared first-token extraction pins).
    /// </summary>
    public string DefaultExpectedVersion => GlslangAuditor.DefaultExpectedVersion;

    /// <inheritdoc />
    public IReadOnlyList<string> VersionProbeArguments => ["--version"];

    /// <inheritdoc />
    public IExternalToolOutputParser OutputParser { get; } = new GlslangDiagnosticParser();

    /// <inheritdoc />
    public IReadOnlyList<string> BuildValidationArguments(ShaderTargetSet targets, string targetEnvironment)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(targetEnvironment);

        // Validation only: no `-V` codegen and no `-o` output — the auditor
        // checks the sources and emits no SPIR-V (a SPIR-V backend is a
        // separate dependent task). The target environment is always passed
        // explicitly rather than inherited from the tool default, and the
        // stage flag only when every target resolved to one stage (the only
        // shape a single `-S` can express; mixed-stage sets rely on
        // per-file canonical extensions, enforced at resolve time).
        var args = new List<string> { "--target-env", targetEnvironment };
        if (targets.SingleStage is { Length: > 0 } stage)
            args.AddRange(["-S", stage]);
        return args;
    }
}
