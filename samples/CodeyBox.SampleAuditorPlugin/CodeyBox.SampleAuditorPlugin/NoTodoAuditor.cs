// Sample auditor plugin: "No TODO comments in committed code"
//
// HOW TO USE THIS SAMPLE
// ─────────────────────
// 1. Copy this directory (samples/CodeyBox.SampleAuditorPlugin/) somewhere outside
//    the CodeyBox tree (or fork/clone the repo and modify in place).
// 2. Rename the project, namespace, class, and plugin ID to match your organisation.
// 3. Implement your audit logic in RunAsync (grep, static analysis, external tool, etc.).
// 4. Build: dotnet build
// 5. Copy the output DLL to your CodeyBox plugins directory (e.g. /etc/codeybox/plugins/).
// 6. Add the plugin ID to CodeyBox:Plugins:Allowlist in appsettings.json.
// 7. Enable the plugin for a project via Audit.Custom:
//      "Custom": [ { "Kind": "plugin", "PluginId": "sample.no-todo" } ]
//
// For NuGet distribution replace the ProjectReferences in the .csproj with:
//   <PackageReference Include="CodeyBox.Core"      Version="..." />
//   <PackageReference Include="CodeyBox.PluginSdk" Version="..." />
//
// See docs/auditor-plugins.md for full author guidance.

using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Logging;

namespace CodeyBox.SampleAuditorPlugin;

/// <summary>
/// Rejects committed code that introduces new TODO/FIXME/HACK/XXX comments.
/// Searches the working tree for TODO-style markers in all text files and
/// reports them as audit findings.
///
/// This is an intentionally simple example. Real-world auditors might run
/// external tools (linters, SAST scanners, compliance checkers) and parse
/// their output into <see cref="AuditFinding"/> records.
/// </summary>
[CodeyBoxPlugin(
    id: "sample.no-todo",
    displayName: "Sample: No TODO comments",
    minHostApiVersion: "1.0")]
public sealed class NoTodoAuditor : IAuditor, IPluginInitializer
{
    // ── IAuditor ──────────────────────────────────────────────────────────────

    public string Name => "sample:no-todo";

    /// <summary>
    /// "tool" is the right kind for deterministic, non-LLM auditors.
    /// Other valid kinds: "shell", "diff-pattern", "llm".
    /// </summary>
    public string Kind => "tool";

    /// <summary>
    /// This auditor needs no agent credentials or network access — it only
    /// reads the working tree. Always declare the minimum required capabilities
    /// so the orchestrator can run this auditor in the most restrictive sandbox.
    /// </summary>
    public AuditCapabilities Required => AuditCapabilities.None;

    private static readonly Regex TodoPattern = new(
        @"(?://|#|--|/\*)\s*(TODO|FIXME|HACK|XXX)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private bool _failOnTodo = true;

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        var findings = new List<AuditFinding>();

        // Walk the working tree; skip binary files and .git/.
        var files = Directory.EnumerateFiles(workingDirectory, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file, ct);
            }
            catch (IOException)
            {
                continue;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                if (!TodoPattern.IsMatch(lines[i])) continue;

                var relativePath = Path.GetRelativePath(workingDirectory, file);
                findings.Add(new AuditFinding(
                    AuditorName: Name,
                    Severity: _failOnTodo ? AuditSeverity.Error : AuditSeverity.Warning,
                    Title: "TODO comment in committed code",
                    Description: $"Line contains a TODO/FIXME/HACK/XXX marker: {lines[i].Trim()}",
                    Location: $"{relativePath}:{i + 1}"));
            }
        }

        return new AuditResult(
            Passed: findings.Count == 0,
            Findings: findings);
    }

    // ── IPluginInitializer ───────────────────────────────────────────────────

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        // Read plugin-scoped config from appsettings.json:
        //   "CodeyBox": { "Plugins": { "sample.no-todo": { "FailOnTodo": "false" } } }
        var failOnTodo = context.ScopedConfig["FailOnTodo"];
        if (failOnTodo is not null)
            _failOnTodo = !failOnTodo.Equals("false", StringComparison.OrdinalIgnoreCase);

        context.Logger.LogInformation(
            "NoTodoAuditor initialized: pluginId={PluginId} failOnTodo={FailOnTodo}",
            context.PluginId, _failOnTodo);

        return Task.CompletedTask;
    }
}
