using System.Text.RegularExpressions;

namespace CodeyBox.Core;

/// <summary>
/// Redacts known secret patterns from arbitrary text (e.g. diff content).
/// Uses the same token patterns as <see cref="SensitiveDataRedactionEnricher"/>
/// so agents that accidentally commit tokens get them scrubbed before operators
/// see the raw diff.
/// </summary>
public static class SecretRedactor
{
    private static readonly Regex SecretPattern = new(
        @"(?:gho_[A-Za-z0-9]+|ghp_[A-Za-z0-9]+|github_pat_[A-Za-z0-9_]+|sk-ant-[A-Za-z0-9_-]+|AIza[A-Za-z0-9_-]{35,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Redact(string text) => SecretPattern.Replace(text, "***");
}
