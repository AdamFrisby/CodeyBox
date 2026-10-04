using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Narrowly scoped structured match for the Devin ACP startup transport
/// failure observed as <c>{"type":"devin.acp","event":"fatal",
/// "stage":"session/new","code":-32603,"message":"Failed to load team
/// settings: Failed to fetch team settings: fetch timed out after
/// 10000ms"}</c> (sig-devin-team-settings-timeout).
///
/// <para>Matching requires the agent-owned structured envelope: a
/// <c>devin.acp</c> <c>fatal</c> envelope whose <c>stage</c> is exactly
/// <c>session/new</c>, whose <c>code</c> is <c>-32603</c> (number or string),
/// and whose <c>message</c> carries the team-settings fetch-timeout identity
/// (team settings + fetch + timed-out/timeout). A bare <c>-32603</c>, a bare
/// <c>timeout</c>, repository/user prose containing the same words, an echoed
/// prompt, or the same code at any other stage (<c>prompt</c>,
/// <c>initialize</c>, <c>session/prompt</c>) never matches. The shim-owned
/// lifted diagnostic (<c>devin acp fatal during session/new: … (code
/// -32603)</c>) is accepted as a secondary shape only when it carries the
/// same stage, code, and message identity.</para>
///
/// <para>Bounded: each input is truncated to the last
/// <see cref="ProviderTransientMatcher.MaxScannedChars"/> chars (the fatal is
/// the terminal line), at most <see cref="MaxLinesScanned"/> lines and
/// <see cref="MaxEnvelopesExamined"/> envelopes are examined, and every
/// evaluation is total (never throws). The returned detection carries only
/// the fixed signature label — never raw agent output, which may contain
/// secrets. An auth signal in the same capture suppresses the match so a
/// genuine credential failure mixed with timeout chatter keeps its existing
/// auth classification instead of being laundered into transient retries.</para>
/// </summary>
internal static class DevinTeamSettingsTimeoutDetector
{
    /// <summary>Detector-owned signature label; never raw agent output.</summary>
    internal const string Signature = "devin-team-settings-timeout";

    private const int MaxLinesScanned = 2000;
    private const int MaxEnvelopesExamined = 128;

    internal static ProviderTransientDetection? TryDetect(string? stderr, string? stdout, string? summary)
    {
        try
        {
            if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout) && string.IsNullOrEmpty(summary))
                return null;

            if (HasAuthSignal(stderr, stdout, summary))
                return null;

            if (HasStructuredTimeout(stderr) || HasStructuredTimeout(stdout) || HasStructuredTimeout(summary))
                return new ProviderTransientDetection(
                    ProviderTransientKind.InfraTransport,
                    Signature,
                    "devin ACP startup transport failure fetching team settings during session/new; retry the turn with backoff");

            if (HasLiftedFatalDiagnostic(stderr) || HasLiftedFatalDiagnostic(stdout) || HasLiftedFatalDiagnostic(summary))
                return new ProviderTransientDetection(
                    ProviderTransientKind.InfraTransport,
                    Signature,
                    "devin ACP startup transport failure fetching team settings during session/new; retry the turn with backoff");

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool HasAuthSignal(string? stderr, string? stdout, string? summary)
    {
        try
        {
            if (!string.IsNullOrEmpty(stderr))
            {
                if (AgentFailureClassifier.ContainsAuthErrorPattern(stderr))
                    return true;
                if (AgentFailureClassifier.ContainsAuthRequiredPatternInStderr(stderr))
                    return true;
                if (IsDevinUnauthorized(stderr, null))
                    return true;
            }

            if (!string.IsNullOrEmpty(stdout))
            {
                if (AgentFailureClassifier.ContainsAuthErrorPattern(stdout))
                    return true;
                if (AgentFailureClassifier.ContainsAuthRequiredPatternInStdout(stdout))
                    return true;
            }

            if (!string.IsNullOrEmpty(summary) && AgentFailureClassifier.ContainsAuthErrorPattern(summary))
                return true;
        }
        catch (Exception)
        {
        }

        return false;
    }

    /// <summary>
    /// Devin-owned auth shapes (<c>Not logged in</c>, <c>Authentication
    /// required</c>, <c>401 Unauthorized</c> — the CLI's own status wording
    /// from <see cref="DevinQuotaFailureDetector"/>) take precedence over the
    /// startup-transport signature. Reuses the detector's quota/auth
    /// vocabulary as the single source of truth instead of re-listing the
    /// strings. Never throws.
    /// </summary>
    private static bool IsDevinUnauthorized(string? stderr, string? stdout)
    {
        try
        {
            return new DevinQuotaFailureDetector().Detect(stderr, stdout)?.Kind == QuotaFailureKind.Unauthorized;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool HasStructuredTimeout(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        try
        {
            var bounded = BoundInput(text);
            if (HasStructuredTimeoutInText(bounded))
                return true;

            if (bounded.Contains("devin", StringComparison.OrdinalIgnoreCase)
                && bounded.Contains("\\\"", StringComparison.Ordinal)
                && TryUnescape(bounded) is { } unescaped
                && !ReferenceEquals(unescaped, bounded)
                && HasStructuredTimeoutInText(unescaped))
            {
                return true;
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    private static bool HasStructuredTimeoutInText(string bounded)
    {
        var examined = 0;
        foreach (var envelope in DevinAcpEnvelope.Enumerate(bounded))
        {
            if (++examined > MaxEnvelopesExamined)
                break;
            if (!string.Equals(envelope.Event, DevinAcpEnvelope.EventFatal, StringComparison.Ordinal))
                continue;
            if (IsSessionNewTeamSettingsTimeout(envelope.Root))
                return true;
        }

        return HasNestedEnvelopeTimeout(bounded);
    }

    /// <summary>
    /// Fallback for the nested/escaped representation: the fatal envelope
    /// embedded mid-line inside a larger log string (a prefix/suffix around
    /// the object, or a JSON-string-escaped copy). Line-based
    /// <see cref="DevinAcpEnvelope.Enumerate"/> only yields whole-line
    /// envelopes, so locate each <c>devin.acp</c> tag occurrence, expand to
    /// the enclosing brace-balanced object (bounded), and validate the same
    /// stage/code/message identity. Bounded to a small candidate count and
    /// object size so a hostile buffer degrades to "no match".
    /// </summary>
    private static bool HasNestedEnvelopeTimeout(string bounded)
    {
        const int maxCandidates = 20;
        const int maxObjectChars = 8 * 1024;

        try
        {
            var candidates = 0;
            var searchFrom = 0;
            while (candidates < maxCandidates)
            {
                var tag = bounded.IndexOf("devin.acp", searchFrom, StringComparison.Ordinal);
                if (tag < 0)
                    return false;
                searchFrom = tag + 1;
                candidates++;

                if (TryParseEnclosingObject(bounded, tag, maxObjectChars) is { } root)
                {
                    if (IsSessionNewTeamSettingsTimeout(root))
                        return true;
                }
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    private static JsonElement? TryParseEnclosingObject(string text, int tagIndex, int maxObjectChars)
    {
        try
        {
            var open = tagIndex <= 0 ? -1 : text.LastIndexOf('{', tagIndex);
            if (open < 0)
                return null;
            if (tagIndex - open > maxObjectChars)
                return null;

            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var i = open; i < text.Length && i - open < maxObjectChars; i++)
            {
                var c = text[i];
                if (inString)
                {
                    if (escaped)
                        escaped = false;
                    else if (c == '\\')
                        escaped = true;
                    else if (c == '"')
                        inString = false;
                    continue;
                }

                if (c == '"')
                    inString = true;
                else if (c == '{')
                    depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        var candidate = text.Substring(open, i - open + 1);
                        using var doc = JsonDocument.Parse(candidate);
                        return doc.RootElement.Clone();
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static string BoundInput(string text)
    {
        const int max = ProviderTransientMatcher.MaxScannedChars;
        var bounded = text.Length > max ? text[^max..] : text;
        if (CountLines(bounded) > MaxLinesScanned)
            bounded = TakeLastLines(bounded, MaxLinesScanned);
        return bounded;
    }

    private static int CountLines(string text)
    {
        var count = 1;
        foreach (var c in text)
        {
            if (c == '\n' && ++count > MaxLinesScanned)
                return count;
        }

        return count;
    }

    private static string TakeLastLines(string text, int maxLines)
    {
        var start = text.Length;
        var newlines = 0;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] == '\n' && ++newlines >= maxLines)
            {
                start = i + 1;
                break;
            }

            if (i == 0)
                start = 0;
        }

        return text[start..];
    }

    private static string? TryUnescape(string bounded)
    {
        try
        {
            if (bounded.Length > ProviderTransientMatcher.MaxScannedChars)
                return null;
            return bounded.Replace("\\\"", "\"", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsSessionNewTeamSettingsTimeout(JsonElement root)
    {
        try
        {
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (!root.TryGetProperty(DevinAcpEnvelope.EventPropertyName, out var eventEl)
                || eventEl.ValueKind != JsonValueKind.String
                || !string.Equals(eventEl.GetString(), DevinAcpEnvelope.EventFatal, StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("stage", out var stageEl)
                || stageEl.ValueKind != JsonValueKind.String
                || !string.Equals(stageEl.GetString(), "session/new", StringComparison.Ordinal))
            {
                return false;
            }

            if (!HasCodeMinus32603(root))
                return false;

            if (!root.TryGetProperty("message", out var messageEl)
                || messageEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return IsTeamSettingsFetchTimeout(messageEl.GetString());
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool HasCodeMinus32603(JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("code", out var codeEl))
                return false;
            if (codeEl.ValueKind == JsonValueKind.Number && codeEl.TryGetInt32(out var code))
                return code == -32603;
            if (codeEl.ValueKind == JsonValueKind.String)
                return string.Equals(codeEl.GetString()?.Trim(), "-32603", StringComparison.Ordinal);
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool IsTeamSettingsFetchTimeout(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return false;
        return message.Contains("team settings", StringComparison.OrdinalIgnoreCase)
            && message.Contains("fetch", StringComparison.OrdinalIgnoreCase)
            && (message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                || message.Contains("timed-out", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasLiftedFatalDiagnostic(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        try
        {
            var bounded = text.Length > ProviderTransientMatcher.MaxScannedChars
                ? text[^ProviderTransientMatcher.MaxScannedChars..]
                : text;
            return bounded.Contains("devin acp fatal", StringComparison.OrdinalIgnoreCase)
                && bounded.Contains("session/new", StringComparison.Ordinal)
                && bounded.Contains("-32603", StringComparison.Ordinal)
                && IsTeamSettingsFetchTimeout(bounded);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
