using System.Text.RegularExpressions;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Recognises quota / rate-limit / auth failures emitted by the devin CLI.
///
/// <para>Devin reports run failures on stderr as <c>Error: …</c> lines and
/// exits nonzero; its account-state strings are taken from the devin
/// 3000.11.1 binary (<c>Quota exhausted</c>, <c>Usage limit reached</c>,
/// <c>Usage paused</c>, <c>Authentication required</c>, <c>Devin needs
/// authentication</c>, <c>Not logged in</c>, <c>Purchase on-demand usage or
/// turn on auto-reload, or wait for your quota to reset.</c>). Because the
/// failure text lives on stderr — print-mode stdout is plain assistant text —
/// the detector scans BOTH streams.</para>
///
/// <para>The pattern list is config-driven: built-in defaults ship in
/// <see cref="DefaultPatterns"/>, and operators can append additional patterns
/// via <c>CodeyBox:QuotaFailurePatterns:devin</c> without recompilation
/// (mirroring the goose/vibe detectors). Patterns stay anchored to the CLI's
/// own status wording rather than bare numbers or single words: devin prompts
/// can contain repository content under review, and model output citing
/// "quota" or discussing rate limits must not gate dispatch.</para>
/// </summary>
public sealed class DevinQuotaFailureDetector : IAgentQuotaFailureDetector
{
    public AgentKind Kind => AgentKind.Devin;

    /// <summary>
    /// Built-in pattern allowlist. Order matters: rate-limit rows come first
    /// so a refusal carrying both shapes parks on the rate-limit backoff
    /// rather than terminating the item as a hard quota failure.
    /// </summary>
    public static readonly IReadOnlyList<QuotaFailurePattern> DefaultPatterns =
    [
        // Shared provider rate-limit rows (transient throughput refusals).
        .. SharedRateLimitPatterns.ProviderRateLimitPatterns,
        // Devin's own quota-exhaustion status strings (verified in the
        // 3000.11.1 binary).
        new("Quota exhausted", QuotaFailureKind.LimitReached),
        new("Usage limit reached", QuotaFailureKind.LimitReached),
        new("Usage paused", QuotaFailureKind.LimitReached),
        new("Purchase on-demand usage or turn on auto-reload", QuotaFailureKind.LimitReached),
        new("out of ACUs", QuotaFailureKind.LimitReached),
        new("quota exceeded", QuotaFailureKind.LimitReached),
        new("insufficient credits", QuotaFailureKind.LimitReached),
        new("402 Payment Required", QuotaFailureKind.LimitReached),
        // Auth failures (verified: `Error: Not logged in.` on stderr, exit 1).
        // The Devin-owned auth vocabulary lives in DevinAuthSignals so the
        // transient detectors' auth suppression consults the exact same set —
        // including operator-configured Unauthorized extras — as this detector.
        .. DevinAuthSignals.UnauthorizedPatterns,
    ];

    private readonly IReadOnlyList<QuotaFailurePattern> _patterns;
    private readonly IReadOnlyList<QuotaFailurePattern> _unauthorizedPatterns;

    /// <summary>
    /// Constructs a detector with the built-in <see cref="DefaultPatterns"/>.
    /// </summary>
    public DevinQuotaFailureDetector() : this(additionalPatterns: null) { }

    /// <summary>
    /// Constructs a detector whose pattern list is <see cref="DefaultPatterns"/>
    /// followed by <paramref name="additionalPatterns"/>. Operator-configured
    /// patterns are checked after defaults; null/empty input behaves
    /// identically to the parameterless constructor.
    /// </summary>
    public DevinQuotaFailureDetector(IEnumerable<QuotaFailurePattern>? additionalPatterns)
    {
        var extras = additionalPatterns?.Where(p => !string.IsNullOrEmpty(p.Pattern)).ToArray();
        if (extras is null || extras.Length == 0)
        {
            _patterns = DefaultPatterns;
        }
        else
        {
            var combined = new List<QuotaFailurePattern>(DefaultPatterns.Count + extras.Length);
            combined.AddRange(DefaultPatterns);
            combined.AddRange(extras);
            _patterns = combined;
        }

        _unauthorizedPatterns = _patterns
            .Where(p => p.Kind == QuotaFailureKind.Unauthorized)
            .ToArray();
    }

    public QuotaDetection? Detect(string? stderr, string? stdout)
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return null;

        foreach (var entry in _patterns)
        {
            var inStderr = !string.IsNullOrEmpty(stderr) && stderr.Contains(entry.Pattern, StringComparison.OrdinalIgnoreCase);
            var inStdout = !string.IsNullOrEmpty(stdout) && stdout.Contains(entry.Pattern, StringComparison.OrdinalIgnoreCase);
            if (!inStderr && !inStdout) continue;

            var resetSources = new List<string?>(2);
            if (!string.IsNullOrEmpty(stderr)) resetSources.Add(stderr);
            if (!string.IsNullOrEmpty(stdout)) resetSources.Add(stdout);
            return new QuotaDetection(
                entry.Kind,
                QuotaResetParser.TryParseResetAt(resetSources)
                    ?? QuotaResetParser.TryParseRetryAfterHeader(resetSources));
        }

        return null;
    }

    /// <summary>
    /// Devin-specific exact transient signatures, checked before the shared
    /// agent-neutral sets. The devin CLI wraps provider refusals in its own
    /// <c>Agent error: Client error: Protocol error (unimplemented)</c>
    /// envelope (verified), which is devin-shaped rather than
    /// provider-relayed. The structured ACP signals are checked first:
    /// the startup transport failure
    /// (<see cref="DevinTeamSettingsTimeoutDetector"/>: <c>fatal</c>
    /// <c>session/new</c> team-settings fetch timeout, code -32603), the
    /// model-substitution abort (<see cref="DevinModelMismatchDetector"/>:
    /// <c>model_mismatch</c> — the CLI's <c>--model</c> fallback must retry,
    /// never bill a substituted model), and a provider-declared retryable
    /// turn error (<see cref="DevinRetryableTurnErrorDetector"/>:
    /// <c>turn_error</c> with <c>cognition.ai/retryable</c>) — never a bare
    /// timeout, code, or prose shape. Operator extras from
    /// <c>CodeyBox:ProviderTransientSignatures:devin</c> are appended at
    /// detect time via <see cref="ProviderTransientSignatureStore"/>.
    /// </summary>
    private static readonly AgentTransientSignature[] DevinTransientSignatures =
    [
        new(
            new Regex(
                @"protocol error\s*\(unimplemented\)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
                ProviderTransientMatcher.MatchTimeout),
            ProviderTransientKind.ModelCapacity,
            "protocol-unimplemented"),
    ];

    public ProviderTransientDetection? DetectProviderTransient(string? stderr, string? stdout, string? summary)
        // The stream-shaped entry point carries no envelope provenance, so it
        // is treated as an unframed capture: structured envelope signatures
        // only match on results the runner stamped StdoutIsEnvelopeFramed
        // (see the AgentResult overload below).
        => DetectProviderTransient(new AgentResult(
            Success: false,
            Summary: summary ?? string.Empty,
            Stdout: stdout,
            Stderr: stderr));

    /// <summary>
    /// Result-shaped detection: the structured ACP detectors only trust
    /// envelope structure when <see cref="AgentResult.StdoutIsEnvelopeFramed"/>
    /// proves the shim owns the stream; raw stderr still feeds the WARN-text
    /// fallback and the shared signatures scan every stream as before.
    /// </summary>
    public ProviderTransientDetection? DetectProviderTransient(AgentResult result)
    {
        try
        {
            if (DevinTeamSettingsTimeoutDetector.TryDetect(result, _unauthorizedPatterns) is { } startupTransient)
                return startupTransient;

            if (DevinModelMismatchDetector.TryDetect(result, _unauthorizedPatterns) is { } modelMismatch)
                return modelMismatch;

            if (DevinRetryableTurnErrorDetector.TryDetect(result, _unauthorizedPatterns) is { } retryableTurnError)
                return retryableTurnError;
        }
        catch (Exception)
        {
        }

        return ProviderTransientDetectorCore.Detect(
            Kind.Value, result.Stderr, result.Stdout, result.Summary, DevinTransientSignatures);
    }
}
