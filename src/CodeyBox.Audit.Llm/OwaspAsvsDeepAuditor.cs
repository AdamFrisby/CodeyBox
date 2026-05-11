using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Audit.Llm;

/// <summary>
/// Deep auditor that reviews the full release branch against the OWASP
/// Application Security Verification Standard (ASVS) Levels 1 and 2.
/// Runs an LLM agent that reads the codebase and writes a structured JSON
/// verdict to <c>/audit/result.json</c>.
/// </summary>
public sealed class OwaspAsvsDeepAuditor : IDeepAuditor
{
    private const string ResultFile = "/audit/owasp-result.json";

    public string Name => "owasp-asvs";
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
                Title: "no agent runner available for OWASP ASVS deep audit",
                Description: "Configure an agent runner in the project's ReleaseConfig.DeepAuditors.")]);

        var prompt = BuildPrompt(workingDirectory);
        var agentResult = await agent.RunAsync(
            sandbox,
            workingDirectory,
            prompt,
            credential: null,
            modelId: context.ModelId,
            reasoningMode: context.ReasoningMode,
            ct: ct,
            stdoutChunkCallback: context.StdoutChunkCallback,
            captureStructuredStream: context.CaptureStructuredStream);

        if (!agentResult.Success)
            return new AuditResult(false, [new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Error,
                Title: "OWASP ASVS audit agent failed",
                Description: agentResult.Stderr ?? agentResult.Summary)],
                RawOutput: agentResult.Stdout);

        return await ReadVerdictAsync(sandbox, agentResult.Stdout, ct);
    }

    private string BuildPrompt(string workingDirectory) => $$"""
        You are a security auditor. Review the entire codebase at {{workingDirectory}} against
        OWASP Application Security Verification Standard (ASVS) Levels 1 and 2.

        Focus on the following ASVS chapters:
        - V2 Authentication: password storage, session management, MFA
        - V3 Session Management: secure tokens, expiry, fixation
        - V5 Validation/Sanitisation/Encoding: injection, XSS, input validation
        - V7 Error Handling: information leakage in errors and logs
        - V8 Data Protection: sensitive data exposure, encryption at rest/transit
        - V9 Communication: TLS configuration, certificate validation
        - V13 API and Web Service: IDOR, broken object-level auth, mass assignment

        Read the source files, configuration, and dependency declarations.
        Then write your verdict to /audit/owasp-result.json as a single JSON object:

        {
          "passed": true|false,
          "findings": [
            { "severity": "error|warning|info", "title": "short title (include ASVS control ID if applicable)",
              "description": "details and remediation guidance", "location": "path:line or 'global'" }
          ]
        }

        "passed" must be false when ANY finding has severity "error".
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
                Title: "OWASP ASVS audit produced invalid JSON",
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
