using System.Text.Json;

namespace CodeyBox.Core;

/// <summary>
/// Shared heuristics that map an agent's stderr / stdout into an
/// <see cref="AgentFailureKind"/>. Used as the default body of
/// <see cref="IAgentRunner.ClassifyFailure"/>; runner-specific overrides can
/// extend it with patterns the shared list doesn't yet cover.
///
/// <para>
/// Pattern dictionaries are exposed as static fields so the operator can tune
/// or replace them in tests without touching runner internals. The patterns
/// here are intentionally substring-only — the orchestrator-side
/// <c>QuotaFailureDetector</c> still owns the structured-stream parsing and
/// reset-window extraction; this classifier is responsible only for the
/// in-iteration fallback decision (which agent kind, if any, to retry on).
/// </para>
/// </summary>
public static class AgentFailureClassifier
{
    public const string HardQuotaReason = "hard quota pattern matched";
    public const string SoftRateLimitReason = "soft rate-limit pattern matched";

    private const int MaxStructuredOutputLineChars = 64 * 1024;

    private static readonly IReadOnlyList<string> StructuredTurnFailedTimeoutPatterns = new[]
    {
        "stream timeout",
        "provider timeout",
        "request timeout",
        "request timed out",
        "connection timeout",
        "connection timed out",
        "network timeout",
        "transport timeout",
        "i/o timeout",
    };

    /// <summary>
    /// Quota / capacity exhaustion shapes where an immediate same-agent resume
    /// would almost certainly re-fail.
    /// </summary>
    public static readonly IReadOnlyList<string> HardQuotaPatterns = new[]
    {
        // Anthropic / OpenAI account caps
        "usage_limit",
        "hit your usage limit",
        "hit your limit",
        // Google / Gemini capacity caps
        "RESOURCE_EXHAUSTED",
        "quota exceeded",
        "exhausted your capacity",
    };

    /// <summary>
    /// Short-window rate / overload shapes. Classify as quota for the
    /// orchestrator fallback chain; the in-VM CLI session resume gate may still
    /// resume provider-confirmed rate-limit blips that have no parsed reset
    /// window, while reset-window-bearing failures follow the normal
    /// quota defer/fallback path.
    /// </summary>
    public static readonly IReadOnlyList<string> SoftRateLimitPatterns = new[]
    {
        // Anthropic / OpenAI
        "rate_limit_exceeded",
        "rate limit exceeded",
        // Google / Gemini
        "exceeded the rate limit",
        // Anthropic overloaded shape
        "overloaded_error",
        // Bare HTTP shapes the CLIs surface verbatim
        "HTTP 429",
        "status 429",
        "API Error: 429",
        "HTTP 529",
        "status 529",
    };

    /// <summary>
    /// All quota/rate substrings recognised by the shared classifier.
    /// </summary>
    public static readonly IReadOnlyList<string> QuotaPatterns =
        HardQuotaPatterns.Concat(SoftRateLimitPatterns).ToArray();

    /// <summary>
    /// Substrings that signal an authentication / authorisation failure —
    /// typically a revoked OAuth token, expired API key, or HTTP 401/403.
    /// Quota responses surface as 429 / RESOURCE_EXHAUSTED and are matched by
    /// <see cref="QuotaPatterns"/> instead.
    /// </summary>
    public static readonly IReadOnlyList<string> AuthPatterns = new[]
    {
        "401 Unauthorized",
        "API Error: 401",
        "403 Forbidden",
        "invalid_api_key",
        "authentication failed",
        "credentials are invalid",
        "token has expired",
        "OAuth token expired",
    };

    /// <summary>
    /// Substrings that signal a CLI is prompting for interactive login rather
    /// than running the task. These are separated from <see cref="AuthPatterns"/>
    /// because they can occur on exit-0 runs that otherwise look like a benign
    /// no-diff outcome.
    ///
    /// <para>Patterns are deliberately CLI-specific phrasings (full-sentence
    /// prompts, OAuth-callback URLs, <c>`agy login`</c>-style suggestions) so a
    /// matching string in a model's task response — e.g. explaining a 401
    /// response shape, or coding an auth flow — does NOT trip the breaker on
    /// an otherwise healthy agent. Generic substrings like "not logged in"
    /// were intentionally rejected after the auditor flagged them as too
    /// broad. Operator-supplied <c>CodeyBox:AuthFailurePatterns</c> entries are
    /// matched against stderr, not untrusted model stdout.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> AuthRequiredPatterns = new[]
    {
        "Authentication required. Please visit",
        "Please visit the URL to log in",
        "Waiting for authentication (timeout",
        "authentication timed out",
        "accounts.google.com/o/oauth2",
        "run `agy login`",
        "run `gemini auth login`",
        "run `agent login`",
        "run `opencode auth login`",
        "run `codex login`",
        "run `claude login`",
    };

    /// <summary>
    /// Substrings that signal a transient connectivity failure where a retry
    /// (against the same agent or another) may succeed without operator action.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultTransientNetworkPatterns = new[]
    {
        "ECONNRESET",
        "ETIMEDOUT",
        "ECONNREFUSED",
        "EAI_AGAIN",
        "Connection reset by peer",
        "Temporary failure in name resolution",
        "Name or service not known",
        "TLS handshake timeout",
        "503 Service Unavailable",
        "504 Gateway Timeout",
        "upstream connect error",
        // Common post-suspend shapes when the peer closed during the freeze window
        "socket hang up",
        "Socket hang up",
        "EPIPE",
        "Broken pipe",
        "fetch failed",
        "Network request failed",
        "Client network socket disconnected",
        "read ECONNRESET",
        // Conservative transport-timeout shapes. Do not add bare "timeout";
        // build/test/phase timeouts are real failures, not retryable transport blips.
        "request timed out",
        "request_timeout",
        "Reconnecting...",
        "Transport channel closed",
        "timeout waiting for child process to exit",
        "Connection timed out",
        "i/o timeout",
    };

    private static string[] _transientNetworkPatterns = DefaultTransientNetworkPatterns.ToArray();

    /// <summary>
    /// Active transient-network substring list. Defaults plus any operator
    /// additions supplied via <see cref="SetAdditionalTransientNetworkPatterns"/>.
    /// </summary>
    public static IReadOnlyList<string> TransientNetworkPatterns =>
        System.Threading.Volatile.Read(ref _transientNetworkPatterns);

    /// <summary>
    /// Appends operator-configured transient network patterns to the built-in
    /// defaults. Empty entries are ignored; matching stays case-insensitive.
    /// </summary>
    public static void SetAdditionalTransientNetworkPatterns(IEnumerable<string>? additionalPatterns)
    {
        var merged = new List<string>(DefaultTransientNetworkPatterns);
        if (additionalPatterns is not null)
        {
            foreach (var pattern in additionalPatterns)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                    continue;
                if (merged.Any(existing => string.Equals(existing, pattern.Trim(), StringComparison.OrdinalIgnoreCase)))
                    continue;
                merged.Add(pattern.Trim());
            }
        }

        System.Threading.Volatile.Write(ref _transientNetworkPatterns, merged.ToArray());
    }

    /// <summary>
    /// Substrings that only become an infrastructure failure when paired with
    /// an exit-127 summary. Each pattern carries enough syntactic context to
    /// distinguish a shell binary-launch failure from a repository-level
    /// filesystem error such as Node.js's
    /// <c>ENOENT: no such file or directory, open 'foo.txt'</c> — a bare
    /// "No such file or directory" match would conflate the two and silently
    /// turn the latter into an infrastructure signal.
    /// </summary>
    public static readonly IReadOnlyList<string> BinaryNotFoundPatterns = new[]
    {
        // bash / zsh: "bash: codex: command not found"
        "command not found",
        // GNU coreutils env: "env: 'codex': No such file or directory" —
        // the close-quote + ": No such file" sequence is the discriminator;
        // Node's fs ENOENT shape is "ENOENT: no such file or directory, open '..."
        // which never contains the close-quote-before-colon prefix.
        "': No such file or directory",
        // POSIX /bin/sh: "/bin/sh: 1: codex: not found"
        ": not found",
        // CodeyBox-side explicit signal raised by the in-VM smoke prober.
        "not found in sandbox",
    };

    /// <summary>
    /// Provider-level launch failures where the sandbox wrapper, not the
    /// invoked shell, reports that the agent executable could not be exec'd.
    /// These can surface as exit 1 (for example bubblewrap's own exit code)
    /// rather than a shell-level 127, so they are intentionally checked
    /// outside <see cref="IsExit127"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> BinaryLaunchFailurePatterns = new[]
    {
        "bwrap: execvp ",
        "bwrap: execv ",
    };

    /// <summary>
    /// Classifies an agent failure. Returns <see cref="AgentFailureKind.Normal"/>
    /// when no exceptional shape was detected — i.e. the agent ran and reported
    /// a work-related failure (failed tests, refused task, malformed output).
    ///
    /// <para>
    /// Order of checks is fixed: prerequisite materialisation and exit-127
    /// sandbox provisioning failures first, then quota (so a 429 in stderr is
    /// never stolen by a generic "connection reset" hint somewhere else in the
    /// payload), then interactive-login auth, then auth, then network. Never
    /// throws.
    /// </para>
    /// </summary>
    public static AgentFailureClassification Classify(string? stderr, string? stdout = null) =>
        Classify(stderr, stdout, summary: null);

    public static AgentFailureClassification Classify(string? stderr, string? stdout, string? summary)
    {
        if (IsMaterialisationFailure(summary))
            return new AgentFailureClassification(AgentFailureKind.Infrastructure, Reason: "agent prerequisite materialisation failed");

        if (IsBinaryNotFoundFailure(summary, stderr, stdout))
        {
            return new AgentFailureClassification(AgentFailureKind.Infrastructure, Reason: "agent binary was not found in the sandbox");
        }

        var noCapturedOutput = string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout);

        if (ContainsAny(stderr, HardQuotaPatterns) || ContainsAny(stdout, HardQuotaPatterns))
            return new AgentFailureClassification(
                AgentFailureKind.QuotaExhausted,
                Reason: HardQuotaReason,
                QuotaFailure: AgentQuotaFailureKind.HardQuota);

        if (ContainsAny(stderr, SoftRateLimitPatterns) || ContainsAny(stdout, SoftRateLimitPatterns))
            return new AgentFailureClassification(
                AgentFailureKind.QuotaExhausted,
                Reason: SoftRateLimitReason,
                QuotaFailure: AgentQuotaFailureKind.SoftRateLimit);

        if (ContainsAuthRequiredPatternInStderr(stderr) || ContainsAuthRequiredPatternInStdout(stdout))
            return new AgentFailureClassification(AgentFailureKind.AuthRequired, Reason: "auth-required pattern matched");

        if (ContainsAny(stderr, AuthPatterns) || ContainsAny(stdout, AuthPatterns))
            return new AgentFailureClassification(AgentFailureKind.AuthError, Reason: "auth pattern matched");

        // The transient list is intentionally conservative; apply it to the
        // captured CLI streams so stdout-only transport diagnostics still park
        // for durable retry. Summary text remains limited to structured
        // turn.failed metadata to avoid classifying synthesized explanations.
        if (ContainsAny(stderr, TransientNetworkPatterns)
            || ContainsAny(stdout, TransientNetworkPatterns)
            || ContainsTurnFailedTransientNetwork(stderr)
            || ContainsTurnFailedTransientNetwork(stdout)
            || ContainsTurnFailedTransientNetwork(summary))
            return new AgentFailureClassification(AgentFailureKind.TransientNetwork, Reason: "network pattern matched");

        if (noCapturedOutput)
            return new AgentFailureClassification(AgentFailureKind.Unknown, Reason: "no output captured");

        return new AgentFailureClassification(AgentFailureKind.Normal);
    }

    private static bool IsBinaryNotFoundFailure(string? summary, string? stderr, string? stdout)
    {
        if (IsExit127(summary)
            && (ContainsAny(stderr, BinaryNotFoundPatterns)
                || ContainsAny(stdout, BinaryNotFoundPatterns)
                || string.IsNullOrWhiteSpace(stderr) && string.IsNullOrWhiteSpace(stdout)))
        {
            return true;
        }

        return IsSandboxWrapperBinaryLaunchFailure(stderr)
            || IsSandboxWrapperBinaryLaunchFailure(stdout);
    }

    private static bool IsMaterialisationFailure(string? summary) =>
        !string.IsNullOrWhiteSpace(summary)
        && (summary.TrimStart().StartsWith("failed to materialise ", StringComparison.OrdinalIgnoreCase)
            || summary.TrimStart().StartsWith("failed to materialize ", StringComparison.OrdinalIgnoreCase));

    private static bool IsExit127(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return false;

        var trimmed = summary.Trim();
        return StartsWithExit127Shape(trimmed, "agent exited 127")
            || StartsWithExit127Shape(trimmed, "exit 127");
    }

    private static bool StartsWithExit127Shape(string haystack, string needle)
    {
        if (!haystack.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            return false;

        var after = needle.Length;
        return after >= haystack.Length || !char.IsLetterOrDigit(haystack[after]);
    }

    private static bool IsSandboxWrapperBinaryLaunchFailure(string? text) =>
        ContainsAny(text, BinaryLaunchFailurePatterns)
        && ContainsAny(text, ["No such file or directory"]);

    private static bool ContainsAny(string? haystack, IReadOnlyList<string> needles)
    {
        if (string.IsNullOrEmpty(haystack)) return false;
        for (var i = 0; i < needles.Count; i++)
        {
            if (haystack.Contains(needles[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool ContainsTurnFailedTransientNetwork(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;

        using var reader = new StringReader(output);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            var line = rawLine.Trim();
            if (line.Length == 0
                || line.Length > MaxStructuredOutputLineChars
                || line[0] != '{'
                || !line.Contains("turn.failed", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String
                    || !string.Equals(type.GetString(), "turn.failed", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var message = ExtractTurnFailedMessage(root);
                if (IsTurnFailedTransientNetworkMessage(message))
                    return true;
            }
            catch (JsonException)
            {
                // Non-JSON lines are covered by the substring matcher above.
            }
        }

        return false;
    }

    private static string? ExtractTurnFailedMessage(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String)
                return error.GetString();
            if (error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }
        }

        if (root.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("error", out var resultError)
            && resultError.ValueKind == JsonValueKind.Object
            && resultError.TryGetProperty("message", out var resultMessage)
            && resultMessage.ValueKind == JsonValueKind.String)
        {
            return resultMessage.GetString();
        }

        return null;
    }

    private static bool IsTurnFailedTransientNetworkMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        // A structured turn.failed "timeout" is provider transport metadata.
        // Keep bare timeout out of the general substring list so build/test
        // timeouts in ordinary logs remain non-retryable.
        if (string.Equals(message.Trim(), "timeout", StringComparison.OrdinalIgnoreCase))
            return true;

        return ContainsAny(message, TransientNetworkPatterns)
            || ContainsAny(message, StructuredTurnFailedTimeoutPatterns);
    }

    public static bool ContainsAuthRequiredPatternInStderr(string? stderr) =>
        ContainsAny(stderr, AuthRequiredPatterns) || ContainsStandaloneOAuthLoginUrlLine(stderr);

    public static bool ContainsAuthRequiredPatternInStdout(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return false;

        return ContainsTrustedStdoutLoginTranscript(stdout);
    }

    public static bool ContainsTrustedStdoutLoginTranscript(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return false;
        if (stdout.Length > 8192)
            return false;

        var lines = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (lines.Length == 0 || lines.Length > 8)
            return false;
        if (lines.Any(static line => !IsTrustedStdoutLoginTranscriptLine(line)))
            return false;

        var hasLoginPrompt =
            stdout.Contains("Authentication required. Please visit", StringComparison.OrdinalIgnoreCase)
            || stdout.Contains("Please visit the URL to log in", StringComparison.OrdinalIgnoreCase);
        var hasWaitOrTimeout =
            stdout.Contains("Waiting for authentication (timeout", StringComparison.OrdinalIgnoreCase)
            || stdout.Contains("authentication timed out", StringComparison.OrdinalIgnoreCase);
        return hasLoginPrompt && hasWaitOrTimeout;
    }

    private static bool IsTrustedStdoutLoginTranscriptLine(string line) =>
        line.StartsWith("Authentication required. Please visit", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Please visit the URL to log in", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Waiting for authentication (timeout", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Error: authentication timed out", StringComparison.OrdinalIgnoreCase)
        || line.Equals("authentication timed out", StringComparison.OrdinalIgnoreCase)
        || IsStandaloneOAuthLoginUrl(line);

    private static bool ContainsStandaloneOAuthLoginUrlLine(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Split('\n').Any(static line => IsStandaloneOAuthLoginUrl(line.Trim()));

    private static bool IsStandaloneOAuthLoginUrl(string line) =>
        line.StartsWith("https://accounts.google.com/o/oauth2", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("http://accounts.google.com/o/oauth2", StringComparison.OrdinalIgnoreCase)
        || ((line.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            && line.Contains("/oauth-callback", StringComparison.OrdinalIgnoreCase));
}
