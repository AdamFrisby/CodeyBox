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
    private const string ResultFile = "audit/result.json";
    private readonly LlmReviewAuditorOptions _opts;

    public LlmReviewAuditor(LlmReviewAuditorOptions opts)
    {
        _opts = opts;
    }

    public string Name => _opts.Name;
    public string Kind => "llm";
    public AuditCapabilities Required => AuditCapabilities.AgentCredentials | AuditCapabilities.Network;

    public string? SelfReviewGuidance
    {
        get
        {
            if (Name.Contains("cheating", StringComparison.OrdinalIgnoreCase))
                return null;
            if (Name.Contains("architecture", StringComparison.OrdinalIgnoreCase))
                return ArchitectureGuidance;
            if (Name.Contains("completeness", StringComparison.OrdinalIgnoreCase))
                return CompletenessGuidance;
            if (Name.Contains("quality", StringComparison.OrdinalIgnoreCase))
                return QualityGuidance;
            if (Name.Contains("security", StringComparison.OrdinalIgnoreCase))
                return SecurityGuidance;
            if (Name.Contains("tests:meaningfulness", StringComparison.OrdinalIgnoreCase) ||
                Name.Contains("tests", StringComparison.OrdinalIgnoreCase))
            {
                if (Name.Contains("mutation", StringComparison.OrdinalIgnoreCase))
                    return null;
                return TestsGuidance;
            }
            return null;
        }
    }

    private const string ArchitectureGuidance = """
- **Loose-coupling violations**: concrete types appearing in cross-module method signatures where an interface already exists.
- **New direct dependencies** that should have gone through an existing abstraction.
- **God objects / classes** accumulating unrelated responsibilities.
- **Layering violations** (e.g. domain code referencing infrastructure).
- **Public APIs that leak internal types.**
""";

    private const string CompletenessGuidance = """
- **TODO / FIXME / XXX markers** added in this change.
- **New functionality without corresponding tests.**
- **Half-finished implementations** (functions that return early, swallowed branches).
- **Public functions whose docstrings/comments describe behaviour the code doesn't implement.**
- **Test files that were renamed or deleted instead of fixed.**
""";

    private const string QualityGuidance = """
- **Dead code** (unreachable branches, unused functions/imports).
- **Magic numbers** and unexplained literal constants.
- **Unclear or misleading names**; abbreviations a new reader couldn't expand.
- **Error handling at boundaries** that swallows or rethrows incorrectly.
- **Duplicated logic** that should be a single helper.
- **Comments that describe WHAT instead of WHY.**
""";

    private const string SecurityGuidance = """
- **Injection (SQL, Command, LDAP, NoSQL, XPath, Template, Header)**: No user input concatenated into queries, process commands, or template rendering.
- **Output Encoding / XSS**: Proper encoding of user inputs in dynamic HTML/DOM sinks.
- **Validation & Business Logic**: Guard against negative values, integer overflows, sign-flips, and TOCTOU.
- **API / Web Service**: State-changing handlers must not respond to GET; avoid mass assignment.
- **File Handling**: Path traversal validation (canonicalisation & containment check) and unrestricted upload prevention.
- **Authentication & Sessions**: Endpoints must require authentication; secure passwords/session tokens; check JWT signature.
- **Authorization / IDOR**: Verify caller ownership/roles for all state-changing or data access routes.
- **Cryptography**: No hardcoded secrets/keys/salts; do not use weak algorithms (MD5/SHA1/DES/RC4); use AEAD where needed.
- **SSRF**: Block cloud metadata/localhost/internal IP ranges for user-supplied URLs.
- **Resource Exhaustion**: Limit unbounded loops/recursion; set request size limits.
- **Data Protection**: Zero hardcoded secrets; do not leak PII/secrets in logs/telemetry.
""";

    private const string TestsGuidance = """
- **Adequacy**: Ensure each new public class, function, endpoint, and error path has at least one test.
- **Meaningfulness**: Avoid implementation-mirroring, pure-mock tests, no-assertion tests, and trivially-true assertions.
- **Edge cases & failures**: Cover boundaries, empty, null, unicode, timeouts, network errors, and resource exhaustion.
- **Heuristic**: Ask yourself: "if I introduced a plausible bug (off-by-one, inverted condition, forgotten null check), would this test catch it?"
""";


    public async Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
    {
        // Make audit/ directory available for the agent's structured output.
        await sandbox.ExecAsync(new SandboxExec { Argv = ["mkdir", "-p", "audit"], WorkingDirectory = workingDirectory }, ct);

        var prompt = BuildPrompt(context);
        // Use the per-invocation override supplied by the pipeline for cross-review,
        // falling back to the baked-in runner from options (backwards compat).
        var agent = context.AuditRunner ?? _opts.Agent;
        var agentResult = await agent.RunAsync(sandbox, workingDirectory, prompt, context.AuditCredential,
            modelId: context.ModelId,
            reasoningMode: context.ReasoningMode,
            ct,
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
            WorkingDirectory = workingDirectory
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
                Title: "review agent produced invalid JSON",
                Description: $"{ex.Message}\n---\n{Truncate(read.Stdout, 1024)}")],
                RawOutput: rawOutput);
        }
    }

    private string BuildPrompt(AuditContext context)
    {
        // Escape closing tag sequences and common delimiters in user content to prevent breakout.
        var safePrompt = context.OriginalPrompt
            .Replace("</", "< /", StringComparison.Ordinal)
            .Replace("]]>", "]] >", StringComparison.Ordinal);

        var safeFocus = _opts.ReviewFocus
            .Replace("</", "< /", StringComparison.Ordinal)
            .Replace("]]>", "]] >", StringComparison.Ordinal);

        return LlmPromptFrameTemplate.Render(_opts.FrameTemplate, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workingDirectory"] = SandboxConventions.WorkDir,
            ["reviewFocus"] = safeFocus,
            ["baseBranch"] = context.BaseBranch,
            ["workBranch"] = context.WorkBranch,
            ["originalPrompt"] = safePrompt,
            ["resultFile"] = ResultFile,
        });
    }

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
