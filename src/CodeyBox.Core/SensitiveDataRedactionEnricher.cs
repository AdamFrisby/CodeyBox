using System.Text.RegularExpressions;
using System.Text.Json;
using Serilog.Core;
using Serilog.Events;

namespace CodeyBox.Core;

/// <summary>
/// Serilog enricher that redacts secret values from structured log events.
/// Applies two independent checks:
/// <list type="number">
///   <item>Property <em>name</em> contains a known-sensitive fragment
///   (Token, Secret, Password, Authorization, ApiKey — case-insensitive).
///   The entire value is replaced with <c>***</c>.</item>
///   <item>Property <em>value</em> is a string matching a known secret
///   pattern (GitHub PAT, Anthropic key). The entire value is replaced with
///   <c>***</c>.</item>
/// </list>
/// This is defence-in-depth: call sites must never log raw secrets in the
/// first place. The enricher catches accidental leakage, not intentional
/// logging of secrets.
/// </summary>
public sealed class SensitiveDataRedactionEnricher : ILogEventEnricher
{
    private static readonly HashSet<string> SensitiveKeyFragments = new(StringComparer.OrdinalIgnoreCase)
    {
        "Token", "Secret", "Password", "Authorization", "ApiKey", "AuthJson", "Credential",
        "SessionId",
    };

    internal const string SecretValuePatternSource =
        @"(?:"
        + @"gh[opsur]_[A-Za-z0-9_]+"
        + @"|github_pat_[A-Za-z0-9_]+"
        + @"|sk-ant-[A-Za-z0-9_-]+"
        + @"|sk-proj-[A-Za-z0-9_-]+"
        + @"|sk-[A-Za-z0-9_-]{20,}"
        + @"|sk_live_[A-Za-z0-9]{16,}"
        + @"|rk_live_[A-Za-z0-9]{16,}"
        + @"|whsec_[A-Za-z0-9]{16,}"
        + @"|AIza[A-Za-z0-9_-]{35,}"
        + @"|(?:A3T[A-Z0-9]|AKIA|ASIA|AGPA|AIDA|AIPA|ANPA|ANVA|AROA)[A-Z0-9]{16}"
        + @"|xox[baprs]-[A-Za-z0-9-]{10,}"
        + @"|xapp-[A-Za-z0-9-]{10,}"
        + @"|-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----"
        + @")";

    private static readonly Regex SecretValuePattern = new(
        SecretValuePatternSource,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex JsonStringPropertyPattern = new(
        "(?<prefix>\"(?<key>(?:\\\\.|[^\"\\\\])*)\"\\s*:\\s*)\"(?:\\\\.|[^\"\\\\])*\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TextKeyValuePattern = new(
        @"(?<prefix>\b(?<key>[A-Za-z_][A-Za-z0-9_.-]*)\s*[:=]\s*)(?<value>[^\r\n]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var key in logEvent.Properties.Keys.ToList())
        {
            if (IsSensitiveKey(key))
            {
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(key, "***"));
                continue;
            }

            if (logEvent.Properties.TryGetValue(key, out var prop) &&
                prop is ScalarValue { Value: string strVal })
            {
                var redacted = RedactText(strVal);
                if (!string.Equals(redacted, strVal, StringComparison.Ordinal))
                    logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(key, redacted));
            }
        }
    }

    public static string RedactText(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var redacted = RedactJsonSensitiveProperties(value);

        if (!LooksLikeJson(redacted))
        {
            redacted = TextKeyValuePattern.Replace(redacted, match =>
                IsSensitiveKey(match.Groups["key"].Value)
                    ? match.Groups["prefix"].Value + "***"
                    : match.Value);
        }

        return SecretValuePattern.Replace(redacted, "***");
    }

    public static string RedactJsonSensitiveProperties(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return JsonStringPropertyPattern.Replace(value, match =>
        {
            var key = UnescapeJsonString(match.Groups["key"].Value);
            return IsSensitiveKey(key) ? match.Groups["prefix"].Value + "\"***\"" : match.Value;
        });
    }

    private static bool IsSensitiveKey(string key) =>
        SensitiveKeyFragments.Any(f => key.Contains(f, StringComparison.OrdinalIgnoreCase))
        || SensitiveKeyFragments.Any(f => NormalizeKey(key).Contains(NormalizeKey(f), StringComparison.OrdinalIgnoreCase));

    private static string NormalizeKey(string key)
    {
        Span<char> buffer = key.Length <= 256 ? stackalloc char[key.Length] : new char[key.Length];
        var written = 0;
        foreach (var ch in key)
        {
            if (char.IsLetterOrDigit(ch))
                buffer[written++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..written]);
    }

    private static string UnescapeJsonString(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<string>($"\"{value}\"") ?? value;
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static bool LooksLikeJson(string value)
    {
        var trimmed = value.AsSpan().TrimStart();
        return trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[');
    }
}
