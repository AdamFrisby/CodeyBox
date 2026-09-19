// FIXTURE ASSEMBLY for plugin enablement / external-tool tests. See the
// .csproj: this is a class library loaded at runtime by the orchestrator
// test suite, not a test project itself.

using CodeyBox.Core;
using CodeyBox.PluginSdk;

namespace CodeyBox.PluginSdk.ToolSampleTests;

/// <summary>
/// Plugin declaring a valid external tool. The binary name is chosen to not
/// exist on any realistic host PATH, so startup-report tests can observe an
/// unmet requirement without depending on host state.
/// </summary>
[CodeyBoxPlugin(
    id: "sample.tool-auditor",
    displayName: "Sample Tool Auditor",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool(
    "codeybox-sample-scan-tool",
    AptPackage = "codeybox-sample-scan",
    InstallHint = "install codeybox-sample-scan from your toolchain feed")]
public sealed class SampleToolAuditor : IAuditor
{
    public string Name => "sample-tool-auditor";
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
        => Task.FromResult(new AuditResult(true, []));
}

/// <summary>
/// Plugin with no tool declarations. Used to prove enablement alone (without
/// any tooling) loads the plugin and contributes nothing to the baseline.
/// </summary>
[CodeyBoxPlugin(
    id: "sample.plain-auditor",
    displayName: "Sample Plain Auditor",
    minHostApiVersion: "1.0")]
public sealed class SamplePlainAuditor : IAuditor
{
    public string Name => "sample-plain-auditor";
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
        => Task.FromResult(new AuditResult(true, []));
}

/// <summary>
/// Plugin declaring a tool whose binary name contains shell metacharacters.
/// The loader must fail closed and skip this plugin; the declaration text
/// must never reach a baseline command, a log-as-command, or DI.
/// </summary>
[CodeyBoxPlugin(
    id: "sample.evil-tool",
    displayName: "Sample Evil Tool",
    minHostApiVersion: "1.0")]
[CodeyBoxPluginRequiresTool("x; touch /tmp/codeybox-pwned")]
public sealed class SampleEvilToolAuditor : IAuditor
{
    public string Name => "sample-evil-tool-auditor";
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
        => Task.FromResult(new AuditResult(true, []));
}
