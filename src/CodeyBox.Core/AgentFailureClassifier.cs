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
/// or replace them in tests without touching runner internals. Quota,
/// transient-network, and provider-auth-error patterns remain substring
/// matches; interactive login prompts match only as short, whole-line
/// CLI-shaped transcripts on either captured stream, because both streams can
/// carry model-relayed prose. The orchestrator-side <c>QuotaFailureDetector</c>
/// still owns the structured-stream parsing and reset-window extraction; this
/// classifier is responsible only for the in-iteration fallback decision
/// (which agent kind, if any, to retry on).
/// </para>
/// </summary>
public static class AgentFailureClassifier
{
    public const string HardQuotaReason = "hard quota pattern matched";
    public const string SoftRateLimitReason = "soft rate-limit pattern matched";

    private const int MaxStructuredOutputLineChars = 64 * 1024;

    /// <summary>
    /// Bounds for the shared CLI-login matcher below. Both captured streams
    /// are treated as potentially model-controlled: agent CLIs relay model
    /// text to stderr as well as stdout, and build/test/tool output is
    /// interleaved there. A genuine auth failure is the CLI refusing to run,
    /// which is a short transcript — not a substring buried in prose — so
    /// both streams require the same short, whole-line CLI shape.
    /// </summary>
    private const int MaxTrustedLoginChars = 4096;
    private const int MaxTrustedLoginLines = 8;
    private const int MaxTrustedLoginTranscriptChars = 8192;
    private const int MaxTrustedLoginTranscriptLines = 8;

    /// <summary>
    /// Longest single line still considered CLI-shaped. Genuine login-prompt
    /// lines are terse (the longest are OAuth URLs with query strings); a
    /// longer line embedding the same phrase is narration, not a refusal.
    /// </summary>
    private const int MaxCliLoginLineChars = 1024;

    /// <summary>
    /// Longest line that may match solely via an unanchored
    /// <c>run `... login`</c> directive fragment. Anchored shapes (prefix,
    /// exact, URL, not-logged-in) already constrain the whole line; a bare
    /// fragment inside a longer sentence is prose quoting the CLI, so
    /// fragment-only matches stay terse.
    /// </summary>
    private const int MaxCliLoginDirectiveLineChars = 200;

    /// <summary>Longest matched line retained as auth evidence on a detection.</summary>
    private const int MaxAuthEvidenceLineChars = 200;

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
        // agy per-model rolling limit: "Individual quota reached. ... Resets in 1h8m20s."
        // Says "quota reached", not "quota exceeded", so without this row a real 429
        // falls through to the generic auth patterns ("authentication failed or timed out")
        // and misclassifies as auth_required instead of quota-exhausted.
        "individual quota reached",
    };

    /// <summary>
    /// Short-window rate / overload shapes. Classify as quota for the
    /// orchestrator fallback chain; the in-VM CLI session resume gate may still
    /// resume provider-confirmed rate-limit blips that have no parsed reset
    /// window, while reset-window-bearing failures follow the normal
    /// quota defer/fallback path.
    ///
    /// <para>Trust split: the bare numeric shapes below ("code 429" et al) are
    /// matched as raw substrings on stderr only — stderr is the CLI's own
    /// surface. Stdout can carry model-relayed prose where an 8-char substring
    /// like "code 429" appears innocently (e.g. discussing HTTP 429 handling),
    /// so stdout quota evidence must arrive as a provider JSON error envelope
    /// and bare numerics there additionally require an exhaustion anchor in the
    /// same envelope line (see the structured-stdout matcher).</para>
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
        "code 429",
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
        "API Error: 403",
        "invalid_api_key",
        "authentication failed",
        "credentials are invalid",
        "token has expired",
        "OAuth token expired",
    };

    private static readonly IReadOnlyList<string> AuthRequiredLoginPromptPrefixes = new[]
    {
        "Authentication required. Please visit",
        "Please visit the URL to log in",
    };

    private static readonly IReadOnlyList<string> AuthRequiredWaitOrTimeoutLinePrefixes = new[]
    {
        "Waiting for authentication (timeout",
        "Error: authentication timed out",
    };

    private static readonly IReadOnlyList<string> AuthRequiredLinePrefixes =
        AuthRequiredLoginPromptPrefixes
            .Concat(AuthRequiredWaitOrTimeoutLinePrefixes)
            .ToArray();

    private static readonly IReadOnlyList<string> AuthRequiredExactLines = new[]
    {
        "authentication timed out",
    };

    private static readonly IReadOnlyList<string> OAuthLoginUrlFragments = new[]
    {
        "accounts.google.com/o/oauth2",
    };

    private static readonly IReadOnlyList<string> CliLoginCommandFragments = new[]
    {
        "run `agy login`",
        "run `gemini auth login`",
        "run `agent login`",
        "run `opencode auth login`",
        "run `codex login`",
        "run `claude login`",
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
    /// broad. The orchestrator-side <c>AgentAuthFailureClassifier</c> applies
    /// operator-supplied <c>CodeyBox:AuthFailurePatterns</c> to stderr/stdout
    /// with stream scoping; stdout additions should remain tightly formed CLI
    /// transcript signatures.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> AuthRequiredPatterns =
        AuthRequiredLinePrefixes
            .Concat(AuthRequiredExactLines)
            .Concat(OAuthLoginUrlFragments)
            .Concat(CliLoginCommandFragments)
            .ToArray();

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
        "agent output HTTP ingest unavailable before launch",
        "agent output transport produced nothing",
        "detached run reported no exit",
        "without authenticated exit completion",
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
        => Classify(default, stderr, stdout, summary);

    public static AgentFailureClassification Classify(
        AgentKind kind,
        string? stderr,
        string? stdout,
        string? summary,
        IReadOnlyDictionary<string, IReadOnlyList<AuthFailurePattern>>? additionalAuthPatternsByAgent = null)
    {
        if (IsMaterialisationFailure(summary))
            return new AgentFailureClassification(AgentFailureKind.Infrastructure, Reason: "agent prerequisite materialisation failed");

        if (IsBinaryNotFoundFailure(summary, stderr, stdout))
        {
            return new AgentFailureClassification(AgentFailureKind.Infrastructure, Reason: "agent binary was not found in the sandbox");
        }

        if (FindUnknownModelEvidence(stderr, stdout, summary) is { } unknownModel)
        {
            return new AgentFailureClassification(
                AgentFailureKind.Infrastructure,
                Reason: $"agent CLI does not know the requested model (pattern '{unknownModel.Pattern}' on {unknownModel.Stream}: '{unknownModel.Line}')");
        }

        var noCapturedOutput = string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout);

        if (ContainsHardQuotaInStderr(stderr) || ContainsHardQuotaInStructuredStdout(stdout))
            return new AgentFailureClassification(
                AgentFailureKind.QuotaExhausted,
                Reason: HardQuotaReason,
                QuotaFailure: AgentQuotaFailureKind.HardQuota);

        if (ContainsAny(stderr, SoftRateLimitPatterns) || ContainsSoftQuotaInStructuredStdout(stdout))
            return new AgentFailureClassification(
                AgentFailureKind.QuotaExhausted,
                Reason: SoftRateLimitReason,
                QuotaFailure: AgentQuotaFailureKind.SoftRateLimit);

        var authRequired = DetectAuthRequired(
            kind,
            stderr,
            stdout,
            additionalAuthPatternsByAgent);
        if (authRequired is not null)
        {
            return authRequired.Classification;
        }

        var stderrAuthError = ContainsAuthErrorPattern(stderr);
        if (stderrAuthError)
        {
            // Record what matched, as for login-prompt detections: the
            // incident reviews had to read source to establish that the
            // trigger was the agent's own text, so the reason carries the
            // pattern, the stream, and the surrounding line.
            var authErrorEvidence = FindAuthErrorEvidence(stderr);
            var authErrorReason = authErrorEvidence is { } hit
                ? $"auth pattern matched in stderr (pattern '{hit.Pattern}' on stderr: '{hit.Line}')"
                : "auth pattern matched";
            return new AgentFailureClassification(AgentFailureKind.AuthError, Reason: authErrorReason);
        }

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

    public static AgentAuthFailureDetection? DetectAuthRequired(
        AgentKind kind,
        string? stderr,
        string? stdout,
        IReadOnlyDictionary<string, IReadOnlyList<AuthFailurePattern>>? additionalPatternsByAgent = null)
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return null;

        var matchedStderr = ContainsAuthRequiredPatternInStderr(stderr);
        var matchedStderrAuthError = ContainsAuthErrorPattern(stderr);
        // Compute the trusted-transcript hit once and reuse it: the public
        // ContainsAuthRequiredPatternInStdout helper would otherwise re-split
        // the buffer line-by-line a second time on the hot path.
        var matchedTrustedStdoutTranscript = ContainsTrustedLoginTranscript(stdout);
        var matchedDefaultStdout = matchedTrustedStdoutTranscript
            || ContainsShortCliLoginOutput(stdout);
        var matchedStdoutFragment = ContainsAuthRequiredFragmentInStdout(stdout);
        var matchedConfiguredStderr = false;
        var matchedConfiguredStdout = false;

        foreach (var pattern in AdditionalAuthPatternsFor(kind, additionalPatternsByAgent))
        {
            if (string.IsNullOrWhiteSpace(pattern.Pattern))
                continue;

            if (pattern.MatchesStderr
                && !string.IsNullOrEmpty(stderr)
                && stderr.Contains(pattern.Pattern, StringComparison.OrdinalIgnoreCase))
            {
                matchedConfiguredStderr = true;
                matchedStderr = true;
            }

            if (pattern.MatchesStdout
                && !string.IsNullOrEmpty(stdout)
                && stdout.Contains(pattern.Pattern, StringComparison.OrdinalIgnoreCase))
            {
                matchedConfiguredStdout = true;
            }
        }

        var matchedStdout = matchedDefaultStdout || matchedConfiguredStdout
            || matchedStderrAuthError && matchedStdoutFragment;
        if (matchedStderrAuthError && (matchedStdout || matchedStdoutFragment))
            matchedStderr = true;

        if (!matchedStderr && !matchedStdout)
            return null;

        var source = matchedStderr && matchedStdout
            ? "stderr/stdout"
            : matchedStderr ? "stderr" : "stdout";
        var evidence = FindCliLoginEvidence(
            matchedStderr ? stderr : null,
            matchedStdout ? stdout : null)
            ?? FindConfiguredAuthEvidence(
                kind,
                matchedConfiguredStderr ? stderr : null,
                matchedConfiguredStdout ? stdout : null,
                additionalPatternsByAgent);
        var reason = evidence is { } hit
            ? $"auth/login prompt pattern matched in {source} (pattern '{hit.Pattern}' on {hit.Stream}: '{hit.Line}')"
            : $"auth/login prompt pattern matched in {source}";
        return new AgentAuthFailureDetection(
            new AgentFailureClassification(
                AgentFailureKind.AuthRequired,
                Reason: reason),
            matchedStderr,
            matchedStdout,
            matchedTrustedStdoutTranscript,
            matchedConfiguredStdout,
            matchedDefaultStdout)
        {
            MatchedConfiguredStderrPattern = matchedConfiguredStderr,
            MatchedPattern = evidence?.Pattern,
            MatchedStream = evidence?.Stream,
            MatchedLine = evidence?.Line,
        };
    }

    /// <summary>
    /// Evidence for an operator-configured pattern hit: the configured pattern
    /// text, the stream it matched, and the surrounding line that carried it.
    /// </summary>
    private static (string Pattern, string Stream, string Line)? FindConfiguredAuthEvidence(
        AgentKind kind,
        string? stderr,
        string? stdout,
        IReadOnlyDictionary<string, IReadOnlyList<AuthFailurePattern>>? additionalPatternsByAgent)
    {
        foreach (var pattern in AdditionalAuthPatternsFor(kind, additionalPatternsByAgent))
        {
            if (string.IsNullOrWhiteSpace(pattern.Pattern))
                continue;

            if (stderr is not null
                && stderr.Contains(pattern.Pattern, StringComparison.OrdinalIgnoreCase)
                && FindPatternLine(stderr, pattern.Pattern) is { } stderrLine)
            {
                return (pattern.Pattern, "stderr", stderrLine);
            }

            if (stdout is not null
                && stdout.Contains(pattern.Pattern, StringComparison.OrdinalIgnoreCase)
                && FindPatternLine(stdout, pattern.Pattern) is { } stdoutLine)
            {
                return (pattern.Pattern, "stdout", stdoutLine);
            }
        }

        return null;
    }

    private static string? FindPatternLine(string text, string pattern)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return TruncateEvidenceLine(line);
        }

        return null;
    }

    private static IEnumerable<AuthFailurePattern> AdditionalAuthPatternsFor(
        AgentKind kind,
        IReadOnlyDictionary<string, IReadOnlyList<AuthFailurePattern>>? additionalPatternsByAgent)
    {
        if (additionalPatternsByAgent is null
            || string.IsNullOrWhiteSpace(kind.Value)
            || !additionalPatternsByAgent.TryGetValue(kind.Value, out var exact))
        {
            yield break;
        }

        foreach (var pattern in exact)
            yield return pattern;
    }

    private static bool IsBinaryNotFoundFailure(string? summary, string? stderr, string? stdout)
    {
        if (IsExit127(summary)
            && (ContainsAny(stderr, BinaryNotFoundPatterns)
                || string.IsNullOrWhiteSpace(stderr) && string.IsNullOrWhiteSpace(stdout)))
        {
            return true;
        }

        return IsSandboxWrapperBinaryLaunchFailure(stderr);
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

    /// <summary>
    /// Strong provider-rejection anchors for the forgeable "individual quota
    /// reached" per-model phrase: the real refusal always carries one on the
    /// same stderr line (usually "Resets in &lt;duration&gt;" plus
    /// RESOURCE_EXHAUSTED / code 429). The word "quota" itself is not an
    /// anchor — the phrase already contains it. A bare echo without an anchor
    /// is model-relayed prose, not a provider rejection.
    /// </summary>
    private static readonly IReadOnlyList<string> IndividualQuotaStderrAnchors = new[]
    {
        "resource_exhausted",
        "resourceexhausted",
        "has been exhausted",
        "429",
        "529",
        "resets in",
        "reset at",
        "locked",
        "lockout",
    };

    private static bool ContainsHardQuotaInStderr(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return false;
        foreach (var rawLine in stderr.Split('\n'))
        {
            var line = rawLine;
            if (line.Length == 0) continue;
            for (var i = 0; i < HardQuotaPatterns.Count; i++)
            {
                var pattern = HardQuotaPatterns[i];
                if (!line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(pattern, "individual quota reached", StringComparison.OrdinalIgnoreCase)
                    && !ContainsAny(line, IndividualQuotaStderrAnchors))
                    continue;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Bare numeric rate shapes: short substrings that occur in ordinary prose
    /// (e.g. a model response discussing HTTP 429 handling mentions "code 429"),
    /// so they are only ever matched on stderr or inside a provider error
    /// envelope that also carries an exhaustion anchor — never in raw stdout.
    /// </summary>
    private static readonly IReadOnlyList<string> BareNumericRateLimitPatterns = new[]
    {
        "HTTP 429",
        "status 429",
        "code 429",
        "API Error: 429",
        "HTTP 529",
        "status 529",
    };

    /// <summary>
    /// Exhaustion anchors that must accompany a bare numeric shape inside a
    /// stdout error envelope before it counts as quota evidence. The host-side
    /// agy shape always carries one ("RESOURCE_EXHAUSTED (code 429): Individual
    /// quota reached"), while prose mentioning a bare code does not.
    /// </summary>
    private static readonly IReadOnlyList<string> StdoutQuotaExhaustionAnchors = new[]
    {
        "quota",
        "exhausted",
        "rate limit",
        "rate_limit",
        "too many requests",
    };

    private static bool ContainsHardQuotaInStructuredStdout(string? stdout)
        => ContainsQuotaInStructuredStdout(stdout, HardQuotaPatterns, anchorBareNumerics: false);

    private static bool ContainsSoftQuotaInStructuredStdout(string? stdout)
        => ContainsQuotaInStructuredStdout(stdout, SoftRateLimitPatterns, anchorBareNumerics: true);

    /// <summary>
    /// Scoped stdout quota matcher. Both captured streams can carry model-relayed
    /// prose, so a raw stdout substring must not force a quota verdict ahead of
    /// auth: stdout only counts inside provider JSON error envelopes
    /// (<c>{"type":"error",...}</c> or <c>{"type":"result","status":"error",...}</c>),
    /// mirroring the per-detector <c>ScopeStdoutForQuotaDetection</c> seam. Bare
    /// numeric shapes additionally require an exhaustion anchor in the same
    /// envelope line. Stderr stays a raw substring match: it is the CLI's own
    /// surface. Never throws.
    /// </summary>
    private static bool ContainsQuotaInStructuredStdout(
        string? stdout,
        IReadOnlyList<string> patterns,
        bool anchorBareNumerics)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return false;
        using var reader = new StringReader(stdout);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.Length > MaxStructuredOutputLineChars || line[0] != '{')
                continue;
            if (!IsProviderErrorEnvelope(line))
                continue;
            for (var i = 0; i < patterns.Count; i++)
            {
                var pattern = patterns[i];
                if (!line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (anchorBareNumerics
                    && IsBareNumericRateLimitPattern(pattern)
                    && !ContainsAny(line, StdoutQuotaExhaustionAnchors))
                    continue;
                return true;
            }
        }
        return false;
    }

    private static bool IsBareNumericRateLimitPattern(string pattern)
    {
        for (var i = 0; i < BareNumericRateLimitPatterns.Count; i++)
        {
            if (string.Equals(pattern, BareNumericRateLimitPatterns[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsProviderErrorEnvelope(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                return false;
            if (string.Equals(type.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.Equals(type.GetString(), "result", StringComparison.OrdinalIgnoreCase))
                return false;
            if (root.TryGetProperty("status", out var status)
                && string.Equals(status.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                return true;
            if (root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
                return true;
            if (root.TryGetProperty("error", out _))
                return true;
            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
            {
                if (result.TryGetProperty("status", out var resultStatus)
                    && string.Equals(resultStatus.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (result.TryGetProperty("is_error", out var resultIsError) && resultIsError.ValueKind == JsonValueKind.True)
                    return true;
                if (result.TryGetProperty("error", out _))
                    return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
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
        ContainsTrustedLoginTranscript(stderr) || ContainsShortCliLoginOutput(stderr);

    public static bool ContainsAuthErrorPattern(string? text) =>
        ContainsAny(text, AuthPatterns);

    /// <summary>
    /// Finds the first stderr line carrying an <see cref="AuthPatterns"/> hit
    /// so an <see cref="AgentFailureKind.AuthError"/> classification can
    /// record the pattern and the surrounding line. Auth-error evidence is
    /// always stderr: unlike the login-prompt matcher, stdout is never
    /// consulted for these shapes, so there is no stream to disambiguate.
    /// Returns null only when no line carries a hit (possible solely for a
    /// pattern spanning a line break); callers keep the bare reason then.
    /// </summary>
    private static (string Pattern, string Line)? FindAuthErrorEvidence(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr))
            return null;
        foreach (var rawLine in stderr.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                continue;
            foreach (var pattern in AuthPatterns)
            {
                if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return (pattern, TruncateEvidenceLine(line));
            }
        }

        return null;
    }

    /// <summary>
    /// CLI-reported "the requested model is not known to this CLI" shapes.
    /// Deliberately CLI-voiced ("unknown model", "model not found") rather
    /// than bare "model": model prose ("the model failed to compile") must
    /// never classify as a configuration fault. Matched case-insensitively
    /// across stderr, stdout, and summary so an agy-style glog line folded
    /// into any of them still classifies. Real CLI errors name the rejected
    /// id inline (<c>model 'x' not found</c>), so a second anchored check
    /// below catches those with the id quoted between the words.
    /// </summary>
    public static readonly IReadOnlyList<string> UnknownModelPatterns = new[]
    {
        "unknown model",
        "unsupported model",
        "unrecognized model",
        "unrecognised model",
        "invalid model",
        "model not found",
        "model was not found",
        "no such model",
        "model does not exist",
        "unknown --model",
        "invalid --model",
        "not a valid model",
    };

    /// <summary>
    /// Error words that, on the same line as an anchored model reference
    /// (<c>model 'id'</c>, <c>model "id"</c>, <c>model: id</c>,
    /// <c>model=id</c>, <c>--model</c>), mark a CLI model refusal even when
    /// the quoted id sits between the words (<c>model 'x' not found</c>).
    /// </summary>
    private static readonly IReadOnlyList<string> UnknownModelErrorWords = new[]
    {
        "unknown",
        "invalid",
        "unsupported",
        "unrecognized",
        "unrecognised",
        "not found",
        "no such",
        "does not exist",
        "doesn't exist",
    };

    private static readonly IReadOnlyList<string> UnknownModelAnchors = new[]
    {
        "model '",
        "model \"",
        "model:",
        "model=",
        "--model",
    };

    /// <summary>
    /// True when any captured stream carries a CLI-reported unknown-model
    /// shape. Used by the audit path to fail fast with a deterministic
    /// configuration error instead of burning the fresh-sandbox retry on a
    /// command that will fail identically. Never throws.
    /// </summary>
    public static bool IsUnknownModelFailure(string? stderr, string? stdout, string? summary = null) =>
        FindUnknownModelEvidence(stderr, stdout, summary) is not null;

    /// <summary>
    /// Finds the first unknown-model line across stderr, stdout, then summary
    /// (that priority order), returning the matched pattern, the stream, and
    /// the truncated line for reasons. Returns null when nothing matches.
    /// </summary>
    public static (string Pattern, string Stream, string Line)? FindUnknownModelEvidence(
        string? stderr, string? stdout, string? summary = null) =>
        FindInStream(stderr, "stderr")
        ?? FindInStream(stdout, "stdout")
        ?? FindInStream(summary, "summary");

    private static (string Pattern, string Stream, string Line)? FindInStream(string? text, string stream)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                continue;
            foreach (var pattern in UnknownModelPatterns)
            {
                if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return (pattern, stream, TruncateEvidenceLine(line));
            }
            if (FindAnchoredModelRefusal(line) is { } word)
                return (word, stream, TruncateEvidenceLine(line));
        }

        return null;
    }

    /// <summary>
    /// Catches <c>model 'id' not found</c>-shaped refusals the fixed patterns
    /// miss because the quoted id breaks the word adjacency. Requires BOTH an
    /// anchored model reference and an error word on the same line, so bare
    /// prose ("the model failed", "model output was empty") never matches.
    /// The error word must sit OUTSIDE the quoted model id as a standalone
    /// token: in <c>model 'unknown-xyz' is not served by the provider
    /// endpoint</c> the word "unknown" is part of the id, not a CLI refusal,
    /// so that provider-side failure must not classify as unknown-model.
    /// Returns the matched error word for reasons, else null.
    /// </summary>
    private static string? FindAnchoredModelRefusal(string line)
    {
        if (!ContainsAny(line, UnknownModelAnchors))
            return null;
        var unquoted = StripQuotedSegments(line);
        for (var i = 0; i < UnknownModelErrorWords.Count; i++)
        {
            if (ContainsTokenSequence(unquoted, UnknownModelErrorWords[i]))
                return UnknownModelErrorWords[i];
        }
        return null;
    }

    /// <summary>
    /// Blanks out paired single/double-quoted segments (typically the rejected
    /// model id) so <see cref="FindAnchoredModelRefusal"/> cannot match an
    /// error word that only occurs inside the id. An unmatched quote (for
    /// example the apostrophe in "doesn't") leaves the rest of the line
    /// untouched.
    /// </summary>
    private static string StripQuotedSegments(string line)
    {
        var chars = line.ToCharArray();
        var i = 0;
        while (i < chars.Length)
        {
            if (!IsQuoteChar(chars[i]))
            {
                i++;
                continue;
            }
            var closer = IndexOfQuoteChar(chars, i + 1);
            if (closer < 0)
                break;
            for (var j = i; j <= closer; j++)
                chars[j] = ' ';
            i = closer + 1;
        }
        return new string(chars);
    }

    private static bool IsQuoteChar(char ch) =>
        ch == '\'' || ch == '"' || ch == '\u2018' || ch == '\u2019' || ch == '\u201C' || ch == '\u201D';

    private static int IndexOfQuoteChar(char[] chars, int start)
    {
        for (var i = start; i < chars.Length; i++)
        {
            if (IsQuoteChar(chars[i]))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Word-boundary punctuation stripped from token edges before matching.
    /// Dashes, underscores, slashes, and dots are deliberately NOT stripped:
    /// they join model-id parts (<c>unknown-xyz</c>), so trimming them would
    /// reintroduce the false positive this guard exists to prevent.
    /// </summary>
    private static readonly char[] TokenEdgePunctuation =
        ['.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '\'', '"', '\u2018', '\u2019', '\u201C', '\u201D', '`', '*'];

    /// <summary>
    /// Case-insensitive whole-token (or adjacent-token for multi-word phrases)
    /// match, so <c>unknown-xyz</c> never matches the error word
    /// <c>unknown</c> while <c>not found.</c> still matches <c>not found</c>.
    /// </summary>
    private static bool ContainsTokenSequence(string text, string word)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var parts = word.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + parts.Length <= tokens.Length; i++)
        {
            var match = true;
            for (var j = 0; j < parts.Length; j++)
            {
                if (!tokens[i + j].Trim(TokenEdgePunctuation).Equals(parts[j], StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Guarded stdout matcher. Delegates to the same shared predicate as
    /// <see cref="ContainsAuthRequiredPatternInStderr"/> so the two streams
    /// cannot drift apart: an input accepted on one is accepted on the other.
    /// </summary>
    public static bool ContainsAuthRequiredPatternInStdout(string? stdout) =>
        ContainsTrustedLoginTranscript(stdout) || ContainsShortCliLoginOutput(stdout);

    public static bool ContainsAuthRequiredFragmentInStdout(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return false;

        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(IsCliLoginLine);
    }

    /// <summary>
    /// Backwards-compatible name for <see cref="ContainsTrustedLoginTranscript"/>.
    /// Kept because operator tooling references the stdout-named entry point.
    /// </summary>
    public static bool ContainsTrustedStdoutLoginTranscript(string? stdout) =>
        ContainsTrustedLoginTranscript(stdout);

    /// <summary>
    /// Shared whole-transcript guard used for both captured streams: trust only
    /// a short output that is entirely the CLI login transcript (login prompt
    /// plus wait/timeout lines), not task prose embedding one.
    /// </summary>
    private static bool ContainsTrustedLoginTranscript(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (text.Length > MaxTrustedLoginTranscriptChars)
            return false;

        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (lines.Length == 0 || lines.Length > MaxTrustedLoginTranscriptLines)
            return false;
        if (lines.Any(static line => !IsTrustedLoginTranscriptLine(line)))
            return false;

        var hasLoginPrompt = AuthRequiredLoginPromptPrefixes
            .Any(pattern => text.Contains(pattern, StringComparison.OrdinalIgnoreCase));
        var hasWaitOrTimeout =
            AuthRequiredWaitOrTimeoutLinePrefixes
                .Any(pattern => text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            || AuthRequiredExactLines
                .Any(pattern => text.Contains(pattern, StringComparison.OrdinalIgnoreCase));
        return hasLoginPrompt && hasWaitOrTimeout;
    }

    /// <summary>
    /// Shared short-output guard used for both captured streams: accept only
    /// short outputs whose non-empty lines are themselves CLI-shaped login
    /// prompts. This catches one-line auth refusals without accepting prose
    /// that embeds the same strings as examples.
    /// </summary>
    private static bool ContainsShortCliLoginOutput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (text.Length > MaxTrustedLoginChars)
            return false;

        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        return lines.Length is > 0 and <= MaxTrustedLoginLines
            && lines.All(IsCliLoginLine);
    }

    /// <summary>
    /// Finds the first CLI-shaped login line in either stream for evidence
    /// recording. Checks stderr before stdout: harness diagnostics outrank
    /// model-relayed text when both carry a match.
    /// </summary>
    private static (string Pattern, string Stream, string Line)? FindCliLoginEvidence(string? stderr, string? stdout)
    {
        foreach (var (text, stream) in new[] { (stderr, "stderr"), (stdout, "stdout") })
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line))
                    continue;
                var pattern = DescribeCliLoginLine(line);
                if (pattern is not null)
                    return (pattern, stream, TruncateEvidenceLine(line));
            }
        }

        return null;
    }

    /// <summary>
    /// Names the catalogued shape a CLI login line matched, for evidence
    /// recording. Returns null when the line is not CLI-shaped. The match
    /// order mirrors <see cref="IsCliLoginLine"/> so the recorded pattern is
    /// the shape the classifier actually accepted.
    /// </summary>
    private static string? DescribeCliLoginLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaxCliLoginLineChars)
            return null;

        foreach (var pattern in AuthRequiredLinePrefixes)
        {
            if (line.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                return pattern;
        }

        foreach (var pattern in AuthRequiredExactLines)
        {
            if (line.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                return pattern;
        }

        if (IsStandaloneOAuthLoginUrl(line))
            return "standalone OAuth login URL";

        var lower = line.ToLowerInvariant();
        if (lower.StartsWith("not logged in", StringComparison.Ordinal)
            && lower.Contains("run ", StringComparison.Ordinal)
            && lower.Contains(" login", StringComparison.Ordinal))
        {
            return "not-logged-in login directive";
        }

        if (line.Length <= MaxCliLoginDirectiveLineChars)
        {
            foreach (var pattern in CliLoginCommandFragments)
            {
                if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return pattern;
            }
        }

        return null;
    }

    private static string TruncateEvidenceLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= MaxAuthEvidenceLineChars
            ? trimmed
            : trimmed[..MaxAuthEvidenceLineChars] + "…";
    }

    private static bool IsTrustedLoginTranscriptLine(string line) =>
        AuthRequiredLinePrefixes.Any(pattern => line.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
        || AuthRequiredExactLines.Any(pattern => line.Equals(pattern, StringComparison.OrdinalIgnoreCase))
        || IsStandaloneOAuthLoginUrl(line);

    private static bool IsCliLoginLine(string line) =>
        DescribeCliLoginLine(line) is not null;

    private static bool IsStandaloneOAuthLoginUrl(string line) =>
        line.StartsWith("https://accounts.google.com/o/oauth2", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("http://accounts.google.com/o/oauth2", StringComparison.OrdinalIgnoreCase)
        || ((line.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            && line.Contains("/oauth-callback", StringComparison.OrdinalIgnoreCase));
}
