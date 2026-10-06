using CodeyBox.Core;

namespace CodeyBox.PluginSdk.Tools;

/// <summary>
/// Small backend seam for the shared shader-validation family: one backend
/// per validation tool (glslang today, SPIR-V tooling as a later dependent).
/// The backend owns only what differs between tools — the binary, the pinned
/// release, the flag construction, and the diagnostic parser. Everything
/// else (target/stage/environment resolution in
/// <see cref="ShaderValidationSupport"/>, sandboxed invocation, timeout and
/// output bounds, severity mapping, finding identity, scoped-config binding)
/// stays on the shared auditor path so backends cannot fork the policy.
/// </summary>
public interface IShaderValidationBackend
{
    /// <summary>Stable backend id for logs and finding provenance (e.g. <c>"glslang"</c>).</summary>
    string BackendId { get; }

    /// <summary>Bare binary name the auditor invokes (e.g. <c>"glslangValidator"</c>). Validated fail-closed.</summary>
    string ToolName { get; }

    /// <summary>Tool release the invocation and report shape were verified against.</summary>
    string DefaultExpectedVersion { get; }

    /// <summary>Arguments appended after the tool name for the version probe (typically <c>["--version"]</c>).</summary>
    IReadOnlyList<string> VersionProbeArguments { get; }

    /// <summary>Parses a finished validation invocation into findings. Throws <see cref="ExternalToolParseException"/> when the output is not a recognizable validation report.</summary>
    IExternalToolOutputParser OutputParser { get; }

    /// <summary>
    /// Builds the backend flag arguments for one validation invocation over
    /// already-resolved <paramref name="targets"/> (flags only — the auditor
    /// appends the target files as context arguments after these). The target
    /// environment is always passed explicitly; the stage flag is passed only
    /// when <see cref="ShaderTargetSet.SingleStage"/> is non-null.
    /// </summary>
    IReadOnlyList<string> BuildValidationArguments(ShaderTargetSet targets, string targetEnvironment);
}
