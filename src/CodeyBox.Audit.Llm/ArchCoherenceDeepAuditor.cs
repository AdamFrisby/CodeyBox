using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Audit.Llm;

/// <summary>
/// Deep auditor that checks architectural coherence across the full release
/// branch: layering violations, circular dependencies, separation-of-concerns
/// gaps, and drift from established project conventions.
/// Runs an LLM agent that reads the codebase and writes a structured JSON
/// verdict to <c>/audit/result.json</c>.
/// </summary>
public sealed class ArchCoherenceDeepAuditor : IDeepAuditor
{
    private const string ResultFile = "/audit/arch-result.json";

    public string Name => "arch-coherence";
    public string Kind => "llm";
    public AuditCapabilities Required => AuditCapabilities.AgentCredentials | AuditCapabilities.Network;

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        DeepAuditContext context,
        CancellationToken ct = default)
    {
        await sandbox.ExecAsync(new SandboxExec { Argv = ["mkdir", "-p", "/audit"] }, ct);

        var agent = context.AuditRunner;
        if (agent is null)
            return new AuditResult(false, [new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Error,
                Title: "no agent runner available for architecture coherence deep audit",
                Description: "Configure an agent runner in the project's ReleaseConfig.DeepAuditors.")]);

        var prompt = BuildPrompt(workingDirectory);
        var agentResult = await agent.RunAsync(
            sandbox,
            workingDirectory,
            prompt,
            credential: null,
            modelId: null,
            reasoningMode: null,
            ct: ct,
            stdoutChunkCallback: context.StdoutChunkCallback,
            captureStructuredStream: context.CaptureStructuredStream);

        if (!agentResult.Success)
            return new AuditResult(false, [new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Error,
                Title: "architecture coherence audit agent failed",
                Description: agentResult.Stderr ?? agentResult.Summary)],
                RawOutput: agentResult.Stdout);

        return await ReadVerdictAsync(sandbox, agentResult.Stdout, ct);
    }

    private string BuildPrompt(string workingDirectory) => $$"""
        You are an architecture reviewer. Analyse the codebase at {{workingDirectory}} for
        architectural coherence. This review covers the full release branch, not just recent
        changes, so focus on cross-cutting structural issues that accumulate over time.

        Check for:
        - Layer violations: UI/API code calling data-layer internals directly, or domain
          logic leaking into infrastructure
        - Circular or inappropriate dependencies between assemblies / packages
        - Inconsistent patterns: mixing different approaches to the same concern (e.g.
          two logging frameworks, two ORMs, inconsistent error handling strategies)
        - God objects or service classes with too many responsibilities
        - Hardcoded configuration values that should live in config files
        - Missing abstractions: concrete infrastructure types used where an interface
          would improve testability
        - Naming and structural conventions that contradict the rest of the codebase

        Read the source files and project structure, then write your verdict to /audit/arch-result.json:

        {
          "passed": true|false,
          "findings": [
            { "severity": "error|warning|info", "title": "short title",
              "description": "details including which files are affected and how to fix",
              "location": "path:line or 'global'" }
          ]
        }

        "passed" must be false when ANY finding has severity "error".
        Reserve "error" for clear violations. Use "warning" for smells and "info" for notes.
        Do not include text outside the JSON object. After writing the file, exit.
        """;

    private async Task<AuditResult> ReadVerdictAsync(ISandbox sandbox, string? rawOutput, CancellationToken ct)
    {
        var read = await sandbox.ExecAsync(new SandboxExec { Argv = ["cat", ResultFile] }, ct);
        if (!read.Success || string.IsNullOrWhiteSpace(read.Stdout))
            return new AuditResult(false, [new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Error,
                Title: $"agent did not write {ResultFile}",
                Description: rawOutput ?? "")],
                RawOutput: rawOutput);

        try
        {
            var parsed = JsonSerializer.Deserialize<ReviewVerdict>(read.Stdout, JsonOpts);
            if (parsed is null) throw new JsonException("null verdict");
            var findings = (parsed.Findings ?? []).Select(f => new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverityParser.Parse(f.Severity),
                Title: f.Title ?? "(no title)",
                Description: f.Description ?? "",
                Location: f.Location)).ToList();
            return new AuditResult(parsed.Passed, findings, RawOutput: rawOutput);
        }
        catch (JsonException ex)
        {
            return new AuditResult(false, [new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Error,
                Title: "arch coherence audit produced invalid JSON",
                Description: $"{ex.Message}\n---\n{Truncate(read.Stdout, 1024)}")],
                RawOutput: rawOutput);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record ReviewVerdict(bool Passed, List<ReviewFinding>? Findings);
    private sealed record ReviewFinding(string? Severity, string? Title, string? Description, string? Location);
}
