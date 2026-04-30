using System.Text.RegularExpressions;
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
        "Token", "Secret", "Password", "Authorization", "ApiKey",
    };

    private static readonly Regex SecretValuePattern = new(
        @"(?:gho_[A-Za-z0-9]+|ghp_[A-Za-z0-9]+|github_pat_[A-Za-z0-9_]+|sk-ant-[A-Za-z0-9_-]+|AIza[A-Za-z0-9_-]{35,})",
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
                prop is ScalarValue { Value: string strVal } &&
                SecretValuePattern.IsMatch(strVal))
            {
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(key, "***"));
            }
        }
    }

    private static bool IsSensitiveKey(string key) =>
        SensitiveKeyFragments.Any(f => key.Contains(f, StringComparison.OrdinalIgnoreCase));
}
