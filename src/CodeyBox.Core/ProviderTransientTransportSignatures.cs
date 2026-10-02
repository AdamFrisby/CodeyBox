using System.Text.RegularExpressions;

namespace CodeyBox.Core;

/// <summary>
/// Agent-neutral transport/upstream transient signatures shared by the
/// per-agent transient detectors. Each entry pairs a compiled exact
/// expression with its stable signature label (used as
/// <see cref="ProviderTransientDetection.MatchedSignature"/> and as the
/// host-level correlation key).
///
/// <para>Every pattern names a multi-token provider/transport diagnostic —
/// never a bare word or status code — so model prose that merely mentions a
/// network concept cannot match. Verified against the September 2026 host
/// blip that hit copilot and devin at the same instant
/// (<c>Error: websocket: close 1006 (abnormal closure): unexpected EOF</c>)
/// and the Copilot CLI's upstream-5xx shape
/// (<c>… Last error: 504 Upstream response was not valid JSON</c>).
/// Deliberately NOT matched: the CLI's outcome-neutral
/// <c>Failed to get response … retried N times</c> wrapper, which also
/// prefixes genuine quota (429) refusals that must keep reaching the quota
/// path — only the underlying transport condition classifies.</para>
/// </summary>
public static class ProviderTransientTransportSignatures
{
    public static readonly (Regex Pattern, string Signature)[] Defaults =
    [
        new(
            new Regex(
                @"websocket:\s*close\s*1006",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
                ProviderTransientMatcher.MatchTimeout),
            "websocket-close-1006"),
        new(
            new Regex(
                @"abnormal closure",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
                ProviderTransientMatcher.MatchTimeout),
            "abnormal-closure"),
        new(
            new Regex(
                @"unexpected EOF",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
                ProviderTransientMatcher.MatchTimeout),
            "unexpected-eof"),
        new(
            new Regex(
                @"connection reset",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
                ProviderTransientMatcher.MatchTimeout),
            "connection-reset"),
        new(
            new Regex(
                @"upstream response was not valid JSON",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
                ProviderTransientMatcher.MatchTimeout),
            "upstream-invalid-json"),
        new(
            new Regex(
                @"\b504\b.{0,80}\bupstream\b|\bupstream\b.{0,80}\b504\b",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
                ProviderTransientMatcher.MatchTimeout),
            "upstream-504"),
        new(
            new Regex(
                @"\b50[34]\b.{0,80}not valid JSON",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
                ProviderTransientMatcher.MatchTimeout),
            "upstream-5xx-invalid"),
    ];

    /// <summary>
    /// Matches <paramref name="text"/> against <see cref="Defaults"/> and
    /// returns the first matching signature label, or null. Never throws.
    /// </summary>
    public static string? MatchFirst(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        foreach (var (pattern, signature) in Defaults)
        {
            if (ProviderTransientMatcher.IsMatch(pattern, text))
                return signature;
        }

        return null;
    }
}

/// <summary>
/// Agent-neutral model-capacity transient signatures shared by the per-agent
/// transient detectors. Verified against the devin capacity refusal
/// (<c>We are currently experiencing capacity issues with this serving
/// model. Please switch to a different model</c> — the provider's
/// model-switch suggestion must never be followed: only the configured model
/// may be used). Same exact-match discipline as
/// <see cref="ProviderTransientTransportSignatures"/>.
/// </summary>
public static class ProviderTransientModelSignatures
{
    public static readonly (Regex Pattern, string Signature)[] Defaults =
    [
        new(
            new Regex(
                @"capacity issues with this serving model",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
                ProviderTransientMatcher.MatchTimeout),
            "serving-model-capacity"),
        new(
            new Regex(
                @"\bmodel\b.{0,40}\boverloaded\b|\boverloaded\b.{0,40}\bmodel\b",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
                ProviderTransientMatcher.MatchTimeout),
            "model-overloaded"),
        new(
            new Regex(
                @"server.{0,20}overloaded|overloaded.{0,20}server",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
                ProviderTransientMatcher.MatchTimeout),
            "server-overloaded"),
    ];

    /// <summary>
    /// Matches <paramref name="text"/> against <see cref="Defaults"/> and
    /// returns the first matching signature label, or null. Never throws.
    /// </summary>
    public static string? MatchFirst(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        foreach (var (pattern, signature) in Defaults)
        {
            if (ProviderTransientMatcher.IsMatch(pattern, text))
                return signature;
        }

        return null;
    }
}

/// <summary>
/// Agent-neutral output-truncation transient signatures shared by the
/// per-agent transient detectors. Verified against the devin truncation
/// refusal (<c>response truncated (model hit max output token limit)</c>).
/// Same exact-match discipline as
/// <see cref="ProviderTransientTransportSignatures"/>.
/// </summary>
public static class ProviderTransientTruncationSignatures
{
    public static readonly (Regex Pattern, string Signature)[] Defaults =
    [
        new(
            new Regex(
                @"response truncated",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
                ProviderTransientMatcher.MatchTimeout),
            "response-truncated"),
        new(
            new Regex(
                @"model hit max output token limit",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
                ProviderTransientMatcher.MatchTimeout),
            "max-output-tokens"),
        new(
            new Regex(
                @"max output token limit",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
                ProviderTransientMatcher.MatchTimeout),
            "output-token-limit"),
    ];

    /// <summary>
    /// Matches <paramref name="text"/> against <see cref="Defaults"/> and
    /// returns the first matching signature label, or null. Never throws.
    /// </summary>
    public static string? MatchFirst(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        foreach (var (pattern, signature) in Defaults)
        {
            if (ProviderTransientMatcher.IsMatch(pattern, text))
                return signature;
        }

        return null;
    }
}
