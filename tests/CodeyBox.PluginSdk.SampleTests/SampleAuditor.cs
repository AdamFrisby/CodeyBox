// SAMPLE PLUGIN — canonical reference for CodeyBox plugin authors.
//
// How to write a plugin:
// 1. Create a class library targeting net10.0.
// 2. Add a PackageReference to CodeyBox.PluginSdk (which pulls CodeyBox.Core).
// 3. Implement the Core interface(s) you want to contribute.
// 4. Decorate your class with [CodeyBoxPlugin(id, displayName)].
// 5. Optionally implement IPluginInitializer for async startup logic.
// 6. Optionally implement IAsyncDisposable for clean shutdown.
// 7. Register your plugin ID in the host's CodeyBox:Plugins:Allowlist config.
//
// The host discovers your assembly, validates it, and registers your type
// under its Core interface(s) as a DI singleton. Existing orchestrator code
// that resolves IEnumerable<IAuditor> will pick it up automatically.
//
// See docs/plugins.md for full authoring guidance.

using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Logging;

namespace CodeyBox.PluginSdk.SampleTests;

/// <summary>
/// Minimal auditor that always passes. Demonstrates the full plugin author
/// workflow without introducing any external dependencies.
/// </summary>
[CodeyBoxPlugin(
    id: "sample.auditor",
    displayName: "Sample Auditor",
    minHostApiVersion: "1.0")]
public sealed class SampleAuditor : IAuditor, IPluginInitializer, IAsyncDisposable
{
    // ── IAuditor ──────────────────────────────────────────────────────────────

    /// <summary>Stable name used in logs and audit findings.</summary>
    public string Name => "sample-auditor";

    /// <summary>
    /// Implementation kind for observability. Use "tool" for external-tool
    /// auditors, "shell" for shell-script auditors, "llm" for LLM-backed ones.
    /// </summary>
    public string Kind => "tool";

    /// <summary>
    /// This sample needs no agent credentials or network — runs in the most
    /// restrictive sandbox group.
    /// </summary>
    public AuditCapabilities Required => AuditCapabilities.None;

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        // A real auditor would run a tool, check its exit code, and parse output.
        return Task.FromResult(new AuditResult(Passed: true, Findings: []));
    }

    // ── IPluginInitializer ───────────────────────────────────────────────────

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        // Read plugin-scoped config: CodeyBox:Plugins:sample.auditor:<key>
        // Use the string indexer (available from IConfigurationSection directly;
        // no extra package needed beyond CodeyBox.PluginSdk).
        var dryRun = context.ScopedConfig["DryRun"] == "true";

        context.Logger.LogInformation(
            "SampleAuditor initialized: pluginId={PluginId} hostApi={HostApi} dryRun={DryRun}",
            context.PluginId, context.HostApiVersion, dryRun);

        return Task.CompletedTask;
    }

    // ── IAsyncDisposable ─────────────────────────────────────────────────────

    public ValueTask DisposeAsync()
    {
        // Release resources here. The DI container calls this at host shutdown.
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A second plugin type in the same assembly to demonstrate that one assembly
/// can contribute multiple independent plugins, each with its own ID and
/// allowlist entry. Used by the allowlist-enforcement tests.
/// </summary>
[CodeyBoxPlugin(
    id: "sample.blocked-auditor",
    displayName: "Sample Blocked Auditor",
    minHostApiVersion: "1.0")]
public sealed class SampleBlockedAuditor : IAuditor
{
    public string Name => "sample-blocked-auditor";
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
        => Task.FromResult(new AuditResult(Passed: true, Findings: []));
}
