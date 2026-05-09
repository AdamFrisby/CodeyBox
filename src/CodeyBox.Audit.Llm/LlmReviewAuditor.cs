using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Audit.Llm;

/// <summary>
/// Drives an <see cref="IAgentRunner"/> with a review-style prompt and
/// parses the agent's structured verdict. Requires the agent's credentials
/// in the sandbox, so the pipeline runs LLM auditors in a separate sandbox
/// from tool-only ones.
///
/// Contract with the agent:
///   - Prompt instructs the agent to write a JSON file at
///     <c>/audit/result.json</c> with shape:
///     <code>
///     { "passed": true|false, "findings": [
///         { "severity": "error|warning|info", "title": "...",
///           "description": "...", "location": "path:line" }
///     ] }
///     </code>
///   - If the file is missing or unparsable, the auditor reports a single
///     Error finding describing the failure. The pipeline treats this as
///     a normal audit failure and re-runs the agent on the next iteration.
/// </summary>
public sealed class LlmReviewAuditor : IAuditor
{
    private const string ResultFile = "/audit/result.json";
    private readonly LlmReviewAuditorOptions _opts;

    public LlmReviewAuditor(LlmReviewAuditorOptions opts)
    {
        _opts = opts;
    }

    public string Name => _opts.Name;
    public string Kind => "llm";
    public AuditCapabilities Required => AuditCapabilities.AgentCredentials | AuditCapabilities.Network;

    public async Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
    {
        // Make /audit available for the agent's structured output.
        await sandbox.ExecAsync(new SandboxExec { Argv = ["mkdir", "-p", "/audit"] }, ct);

        var prompt = BuildPrompt(context);
        // Use the per-invocation override supplied by the pipeline for cross-review,
        // falling back to the baked-in runner from options (backwards compat).
        var agent = context.AuditRunner ?? _opts.Agent;
        var agentResult = await agent.RunAsync(sandbox, workingDirectory, prompt, context.AuditCredential, modelId: null, reasoningMode: null, ct,
            stdoutChunkCallback: context.StdoutChunkCallback,
            captureStructuredStream: context.CaptureStructuredStream);

        // The pipeline already populates SandboxSpec.Environment with the
        // agent credential (set on the container at boot). Passing the same
        // bundle here lets runners that need file-based setup materialise it.

        var rawOutput = agentResult.Stdout;

        if (!agentResult.Success)
        {
            return new AuditResult(false, [new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Error,
                Title: "review agent failed to run",
                Description: agentResult.Stderr ?? agentResult.Summary)],
                RawOutput: rawOutput,
                AgentStderr: agentResult.Stderr,
                AgentSummary: agentResult.Summary,
                AgentStdout: agentResult.Stdout);
        }

        // Read the JSON result file from the sandbox.
        var read = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["cat", ResultFile],
        }, ct);
        if (!read.Success || string.IsNullOrWhiteSpace(read.Stdout))
        {
            return new AuditResult(false, [new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Error,
                Title: $"agent did not write {ResultFile}",
                Description: agentResult.Stdout ?? "")],
                RawOutput: rawOutput);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ReviewVerdict>(read.Stdout, JsonOpts);
            if (parsed is null)
                throw new JsonException("null verdict");
            var findings = (parsed.Findings ?? []).Select(f => new AuditFinding(
                AuditorName: Name,
                Severity: ParseSeverity(f.Severity),
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
                Title: "review agent produced invalid JSON",
                Description: $"{ex.Message}\n---\n{Truncate(read.Stdout, 1024)}")],
                RawOutput: rawOutput);
        }
    }

    private string BuildPrompt(AuditContext context)
    {
        // Escape any closing tag sequence in user content to prevent delimiter breakout.
        var safePrompt = context.OriginalPrompt
            .Replace("</task_description>", "< /task_description>", StringComparison.OrdinalIgnoreCase);

        return LlmPromptFrameTemplate.Render(_opts.FrameTemplate, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workingDirectory"] = SandboxConventions.WorkDir,
            ["reviewFocus"] = _opts.ReviewFocus,
            ["baseBranch"] = context.BaseBranch,
            ["workBranch"] = context.WorkBranch,
            ["originalPrompt"] = safePrompt,
            ["resultFile"] = ResultFile,
        });
    }

    private static AuditSeverity ParseSeverity(string? s) => s?.ToLowerInvariant() switch
    {
        "error" => AuditSeverity.Error,
        "warning" or "warn" => AuditSeverity.Warning,
        _ => AuditSeverity.Info,
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record ReviewVerdict(bool Passed, List<ReviewFinding>? Findings);
    private sealed record ReviewFinding(string? Severity, string? Title, string? Description, string? Location);
}

public sealed record LlmReviewAuditorOptions
{
    public required string Name { get; init; }
    public required IAgentRunner Agent { get; init; }

    /// <summary>
    /// Bullet-list of focus areas appended to the review prompt. E.g.:
    /// "- Architectural boundaries / loose coupling violations\n- Hardcoded secrets".
    /// </summary>
    public required string ReviewFocus { get; init; }

    public required string FrameTemplate { get; init; }
}

public static class LlmPromptFrameTemplate
{
    public static readonly IReadOnlySet<string> AllowedPlaceholders =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "workingDirectory",
            "reviewFocus",
            "baseBranch",
            "workBranch",
            "originalPrompt",
            "resultFile",
        };

    public static IReadOnlyList<string> FindPlaceholders(string template)
    {
        var placeholders = new List<string>();
        for (var i = 0; i < template.Length;)
        {
            var start = template.IndexOf("{{", i, StringComparison.Ordinal);
            if (start < 0)
                break;
            var end = template.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (end < 0)
                break;
            placeholders.Add(template[(start + 2)..end].Trim());
            i = end + 2;
        }
        return placeholders;
    }

    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        var rendered = new StringBuilder(template.Length);
        for (var i = 0; i < template.Length;)
        {
            var start = template.IndexOf("{{", i, StringComparison.Ordinal);
            if (start < 0)
            {
                rendered.Append(template, i, template.Length - i);
                break;
            }

            var end = template.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                rendered.Append(template, i, template.Length - i);
                break;
            }

            rendered.Append(template, i, start - i);
            var placeholder = template[(start + 2)..end].Trim();
            if (!AllowedPlaceholders.Contains(placeholder))
                throw new InvalidOperationException($"Unknown LLM prompt frame placeholder '{{{{{placeholder}}}}}'");
            if (!values.TryGetValue(placeholder, out var value))
                throw new InvalidOperationException($"No value supplied for LLM prompt frame placeholder '{{{{{placeholder}}}}}'");
            rendered.Append(value);
            i = end + 2;
        }
        return rendered.ToString();
    }
}
