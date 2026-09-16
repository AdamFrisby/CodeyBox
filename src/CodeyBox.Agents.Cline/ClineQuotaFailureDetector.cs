using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Cline;

/// <summary>
/// Recognises quota / rate-limit / auth failures emitted by the cline CLI
/// (verified against cline 3.0.62 live frames with a <c>$0</c>-spend-limit
/// OpenRouter key).
///
/// <para>Cline reports terminal failures three ways: an <c>agent_event</c>
/// error frame (<c>event.error.message</c> plus <c>errorClass</c>), an error
/// <c>run_result</c> (<c>finishReason: error</c>, cause repeated in
/// <c>text</c>), and a <c>{"type":"error","message":"…"}</c> line on stderr.
/// Verified shapes:</para>
/// <list type="bullet">
/// <item><description>Bad OpenRouter key (exit 1):
/// <c>User not found.</c> (also with <c>errorClass: auth</c>).</description></item>
/// <item><description>$0-spend-limit key against a paid model (exit 1):
/// <c>Key limit exceeded (total limit). Manage it using
/// https://openrouter.ai/…</c> (also with <c>errorClass: auth</c> — the
/// class alone cannot distinguish spend-limit from bad-key, so the message
/// text decides).</description></item>
/// <item><description>Missing <c>-P</c> (default <c>cline</c> vendor
/// provider, exit 1): <c>Unauthorized: Please make sure you're using the
/// latest version of Cline and re-authenticate your Cline account.</c> —
/// the confusing first failure when the runner forgets the provider
/// flag.</description></item>
/// </list>
///
/// <para><b>Scoping.</b> Cline-specific auth/spend phrases are matched only
/// against failure signal — error-frame messages and raw stderr (which in
/// <c>--json</c> mode carries only the terminal error line) — never against
/// a completed run's assistant prose (<c>done.text</c> /
/// <c>run_result.text</c>): a work item about "user not found" handling
/// must not park the agent on a false auth signal. Shared provider
/// rate-limit rows stay global across both streams per established
/// convention. The pattern list is config-driven: built-in defaults ship in
/// <see cref="DefaultPatterns"/>, and operators can append additional
/// patterns via <c>CodeyBox:QuotaFailurePatterns:cline</c> without
/// recompilation (mirroring the goose detector).</para>
/// </summary>
public sealed class ClineQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Cline;

    /// <summary>
    /// Built-in pattern allowlist. Order matters: the spend-limit rows come
    /// before the access-denied row so a paid-model refusal parks on the
    /// hard-cap backoff (<see cref="QuotaFailureKind.LimitReached"/>) rather
    /// than the auth recovery path. These patterns match against failure
    /// signal only (see class docs), not completed-run prose.
    /// </summary>
    public static readonly IReadOnlyList<QuotaFailurePattern> DefaultPatterns =
    [
        // Shared provider rate-limit rows (transient throughput refusals).
        .. SharedRateLimitPatterns.ProviderRateLimitPatterns,
        // Spend-limit / hard-cap exhaustion (verified: $0 key, paid model).
        new("Key limit exceeded", QuotaFailureKind.LimitReached),
        new("permission for this model", QuotaFailureKind.LimitReached),
        new("insufficient_quota", QuotaFailureKind.LimitReached),
        new("insufficient credits", QuotaFailureKind.LimitReached),
        new("billing_hard_limit_reached", QuotaFailureKind.LimitReached),
        new("HTTP 402", QuotaFailureKind.LimitReached),
        new("402 Payment Required", QuotaFailureKind.LimitReached),
        // "quota" alone matches reviewing-quota-code text; require a verb
        // that conveys exhaustion.
        new("quota exceeded", QuotaFailureKind.LimitReached),
        new("quota exhausted", QuotaFailureKind.LimitReached),
        new("usage limit reached", QuotaFailureKind.LimitReached),
        // Provider-shaped auth failures (verified: bad OpenRouter key, and
        // the default-provider vendor miss). "User not found" is matched
        // only in failure signal (error frames/stderr), never in
        // completed-run prose — it matches reviewed text (user-management
        // code, docs) far more often than provider auth state.
        new("User not found", QuotaFailureKind.Unauthorized),
        new("re-authenticate your Cline account", QuotaFailureKind.Unauthorized),
        new("Missing Authentication header", QuotaFailureKind.Unauthorized),
        new("authentication_error", QuotaFailureKind.Unauthorized),
        new("API key is invalid", QuotaFailureKind.Unauthorized),
        new("invalid_api_key", QuotaFailureKind.Unauthorized),
        new("incorrect api key", QuotaFailureKind.Unauthorized),
        new("Access denied", QuotaFailureKind.Unauthorized),
        new("401 Unauthorized", QuotaFailureKind.Unauthorized),
        new("API Error: 401", QuotaFailureKind.Unauthorized),
    ];

    private readonly IReadOnlyList<QuotaFailurePattern> _patterns;

    /// <summary>
    /// Constructs a detector with the built-in <see cref="DefaultPatterns"/>.
    /// </summary>
    public ClineQuotaFailureDetector() : this(additionalPatterns: null) { }

    /// <summary>
    /// Constructs a detector whose pattern list is <see cref="DefaultPatterns"/>
    /// followed by <paramref name="additionalPatterns"/>. Operator-configured
    /// patterns are checked after defaults; null/empty input behaves
    /// identically to the parameterless constructor.
    /// </summary>
    public ClineQuotaFailureDetector(IEnumerable<QuotaFailurePattern>? additionalPatterns)
    {
        if (additionalPatterns is null)
        {
            _patterns = DefaultPatterns;
            return;
        }

        var extras = additionalPatterns.Where(p => !string.IsNullOrEmpty(p.Pattern)).ToArray();
        if (extras.Length == 0)
        {
            _patterns = DefaultPatterns;
            return;
        }

        var combined = new List<QuotaFailurePattern>(DefaultPatterns.Count + extras.Length);
        combined.AddRange(DefaultPatterns);
        combined.AddRange(extras);
        _patterns = combined;
    }

    public QuotaDetection? Detect(string? stderr, string? stdout)
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return null;

        // Failure signal: error-frame messages parsed from both streams plus
        // raw stderr (in --json mode stderr carries only the terminal error
        // line). A completed run's assistant prose is deliberately excluded
        // so model output discussing quota/auth text cannot false-park.
        var failureSignal = ExtractFailureSignal(stderr, stdout);

        foreach (var entry in _patterns)
        {
            var inSignal = failureSignal.Any(s =>
                s.Contains(entry.Pattern, StringComparison.OrdinalIgnoreCase));
            if (inSignal)
            {
                var resetSources = new List<string?>(2);
                if (!string.IsNullOrEmpty(stderr)) resetSources.Add(stderr);
                if (!string.IsNullOrEmpty(stdout)) resetSources.Add(stdout);
                return new QuotaDetection(
                    entry.Kind,
                    QuotaResetParser.TryParseResetAt(resetSources)
                        ?? QuotaResetParser.TryParseRetryAfterHeader(resetSources));
            }

            // Shared rate-limit shapes stay global per established
            // convention: a mid-stream 429 mention parks even outside an
            // error frame.
            if (SharedRateLimitPatterns.ProviderRateLimitPatterns.Any(p =>
                    string.Equals(p.Pattern, entry.Pattern, StringComparison.Ordinal)))
            {
                var inStdout = !string.IsNullOrEmpty(stdout)
                    && stdout.Contains(entry.Pattern, StringComparison.OrdinalIgnoreCase);
                if (inStdout)
                {
                    var resetSources = new List<string?>(2);
                    if (!string.IsNullOrEmpty(stderr)) resetSources.Add(stderr);
                    if (!string.IsNullOrEmpty(stdout)) resetSources.Add(stdout);
                    return new QuotaDetection(
                        entry.Kind,
                        QuotaResetParser.TryParseResetAt(resetSources)
                            ?? QuotaResetParser.TryParseRetryAfterHeader(resetSources));
                }
            }
        }

        return null;
    }

    private static List<string> ExtractFailureSignal(string? stderr, string? stdout)
    {
        var signal = new List<string>();
        if (!string.IsNullOrEmpty(stderr))
            signal.Add(stderr);

        foreach (var text in new[] { stdout, stderr })
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || !line.StartsWith('{'))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (ExtractErrorMessage(doc.RootElement) is { } message)
                        signal.Add(message);
                }
                catch (JsonException)
                {
                    // Half-written or interleaved chatter — keep scanning.
                }
            }
        }

        return signal;
    }

    private static string? ExtractErrorMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (!root.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String)
            return null;

        var typeName = type.GetString();
        if (string.Equals(typeName, "agent_event", StringComparison.OrdinalIgnoreCase)
            && root.TryGetProperty("event", out var inner)
            && inner.ValueKind == JsonValueKind.Object
            && inner.TryGetProperty("type", out var innerType)
            && innerType.ValueKind == JsonValueKind.String
            && string.Equals(innerType.GetString(), "error", StringComparison.OrdinalIgnoreCase)
            && inner.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("message", out var agentMessage)
            && agentMessage.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(agentMessage.GetString()))
        {
            return agentMessage.GetString();
        }

        if (string.Equals(typeName, "run_result", StringComparison.OrdinalIgnoreCase)
            && root.TryGetProperty("finishReason", out var finish)
            && finish.ValueKind == JsonValueKind.String
            && string.Equals(finish.GetString(), "error", StringComparison.OrdinalIgnoreCase)
            && root.TryGetProperty("text", out var resultText)
            && resultText.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(resultText.GetString()))
        {
            return resultText.GetString();
        }

        if (string.Equals(typeName, "error", StringComparison.OrdinalIgnoreCase)
            && root.TryGetProperty("message", out var stderrMessage)
            && stderrMessage.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(stderrMessage.GetString()))
        {
            return stderrMessage.GetString();
        }

        return null;
    }
}
