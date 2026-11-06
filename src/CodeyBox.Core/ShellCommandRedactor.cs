using System.Text;

namespace CodeyBox.Core;

/// <summary>
/// Redacts credentials in one shell command line before that line is copied
/// into an operator-visible message. The result is display text, not a script
/// to execute. Static and stateless.
/// </summary>
/// <remarks>
/// <see cref="SensitiveDataRedactionEnricher.RedactText"/> runs first for
/// known token shapes. It stops at the first <c>key:value</c> on a line, and
/// <c>https:</c> is not a sensitive key, so a later assignment on the same
/// line is redacted again per shell word. URL userinfo is removed per word
/// with <see cref="EmbeddedUrlUserInfo.StripToken"/> after quotes and
/// backslashes are decoded, so a sub-delimiter inside a password stays inside
/// the userinfo. Quoted text is the same command language and is scrubbed
/// with the same rules, bounded by <see cref="MaxQuotedCommandDepth"/>.
/// </remarks>
public static class ShellCommandRedactor
{
    /// <summary>
    /// Quote nesting this redactor will open. Deeper quotes still have URL
    /// userinfo stripped, and the unparsed remainder is kept.
    /// </summary>
    public const int MaxQuotedCommandDepth = 8;

    // Longer flags first so `--password` is not confused with a shorter prefix.
    private static readonly string[] CredentialFlags =
    [
        "--proxy-user",
        "--password",
        "--user",
        "-U",
        "-u",
        "-p",
    ];

    /// <summary>
    /// Redacts URL userinfo, basic-auth and password flags, npm <c>_auth</c>
    /// values, and sensitive <c>key=value</c> assignments in <paramref name="command"/>.
    /// </summary>
    public static string Redact(string? command)
    {
        if (string.IsNullOrEmpty(command))
            return command ?? string.Empty;
        return RedactWords(SensitiveDataRedactionEnricher.RedactText(command), depth: 0);
    }

    private static string RedactWords(string text, int depth)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                builder.Append(text[index]);
                index++;
                continue;
            }

            var word = ReadShellWord(text, index);
            if (word.End <= index)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            index = AppendWord(text, word, builder, depth);
        }

        return builder.ToString();
    }

    private static int AppendWord(string text, ShellWord word, StringBuilder builder, int depth)
    {
        if (word.Quoted)
        {
            // A quoted region is another shell command (sh -c "...") or a
            // single token. Scrub the decoded body before deciding whether
            // the token itself is a flag, so a quote that merely contains
            // the letters "Token" is not treated as a key.
            var inner = depth >= MaxQuotedCommandDepth
                ? EmbeddedUrlUserInfo.Strip(word.Decoded)
                : RedactWords(word.Decoded, depth + 1);
            if (string.Equals(inner, word.Decoded, StringComparison.Ordinal))
                builder.Append(text, word.Start, word.End - word.Start);
            else
                builder.Append(Quote(inner, word.Quote));
            if (TakesFollowingCredential(word.Decoded))
                return AppendFollowingValue(text, word.End, builder);
            return word.End;
        }

        if (TryRedactInlineCredential(word.Decoded, out var inline))
        {
            builder.Append(inline);
            return word.End;
        }

        var stripped = EmbeddedUrlUserInfo.StripToken(word.Decoded);
        if (TryRedactSensitiveAssignment(stripped, out var assigned))
        {
            builder.Append(assigned);
            return word.End;
        }

        if (TakesFollowingCredential(word.Decoded))
        {
            builder.Append(text, word.Start, word.End - word.Start);
            return AppendFollowingValue(text, word.End, builder);
        }

        if (!string.Equals(stripped, word.Decoded, StringComparison.Ordinal))
            builder.Append(stripped);
        else
            builder.Append(text, word.Start, word.End - word.Start);
        return word.End;
    }

    private static int AppendFollowingValue(string text, int index, StringBuilder builder)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            builder.Append(text[index]);
            index++;
        }

        if (index >= text.Length)
            return index;

        var value = ReadShellWord(text, index);
        if (value.End <= index)
        {
            builder.Append(text, index, text.Length - index);
            return text.Length;
        }

        // The value may be glued to the closing quote (`"--user"builder:pass`).
        // It is still the next shell word and is a credential.
        builder.Append("***");
        return value.End;
    }

    private static bool TakesFollowingCredential(string word)
    {
        if (word.Length == 0 || word.IndexOfAny([' ', '\t', '\r', '\n']) >= 0)
            return false;
        if (IsExactCredentialFlag(word) || IsUserShortOptionCluster(word))
            return true;
        if (SensitiveDataRedactionEnricher.IsSensitiveAssignmentKey(word))
            return true;
        return IsNpmBareAuthKey(word);
    }

    private static bool TryRedactInlineCredential(string word, out string redacted)
    {
        if (TryRedactFlagAssignment(word, out redacted))
            return true;
        if (TryRedactAttachedUserOption(word, out redacted))
            return true;
        return TryRedactNpmAuthAssignment(word, out redacted);
    }

    private static bool IsExactCredentialFlag(string word)
    {
        foreach (var flag in CredentialFlags)
        {
            if (string.Equals(word, flag, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool TryRedactFlagAssignment(string word, out string redacted)
    {
        foreach (var flag in CredentialFlags)
        {
            if (word.Length > flag.Length
                && word[flag.Length] == '='
                && word.StartsWith(flag, StringComparison.Ordinal))
            {
                redacted = flag + "=***";
                return true;
            }
        }

        redacted = "";
        return false;
    }

    /// <summary>
    /// Curl treats a short-option cluster as flags until the first option that
    /// takes an argument. <c>-fsSLu</c> and <c>-fsSLubuilder:pass</c> are basic
    /// auth; the attached value is redacted here and a cluster that ends on
    /// <c>u</c>/<c>U</c> redacts the next word. A lone <c>-u</c> followed by
    /// letters and no colon (<c>-update</c>) is left unchanged.
    /// </summary>
    private static bool TryRedactAttachedUserOption(string word, out string redacted)
    {
        redacted = "";
        if (word.Length < 3 || word[0] != '-' || word[1] == '-')
            return false;

        for (var i = 1; i < word.Length; i++)
        {
            var c = word[i];
            if (c is 'u' or 'U')
            {
                var attached = word[(i + 1)..];
                if (attached.Length == 0)
                    return false;
                if (i == 1 && attached.IndexOf(':') < 0)
                    return false;
                redacted = string.Concat(word.AsSpan(0, i + 1), " ***");
                return true;
            }

            if (!char.IsAsciiLetter(c))
                return false;
        }

        return false;
    }

    private static bool IsUserShortOptionCluster(string word)
    {
        if (word.Length < 2 || word[0] != '-' || word[1] == '-')
            return false;
        if (word[^1] is not ('u' or 'U'))
            return false;
        for (var i = 1; i < word.Length; i++)
        {
            if (!char.IsAsciiLetter(word[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// npm <c>config set //registry/:_auth VALUE</c>. <c>_authToken</c> is a
    /// sensitive assignment key and is not matched here.
    /// </summary>
    private static bool IsNpmBareAuthKey(string word) =>
        string.Equals(word, "_auth", StringComparison.Ordinal)
        || word.EndsWith(":_auth", StringComparison.Ordinal);

    private static bool TryRedactNpmAuthAssignment(string word, out string redacted)
    {
        var equals = word.IndexOf('=');
        if (equals <= 0)
        {
            redacted = "";
            return false;
        }

        var key = word[..equals];
        if (!IsNpmBareAuthKey(key))
        {
            redacted = "";
            return false;
        }

        redacted = key + "=***";
        return true;
    }

    private static bool TryRedactSensitiveAssignment(string word, out string redacted)
    {
        var schemeUrl = word.Contains("://", StringComparison.Ordinal);
        if (word.IndexOf('=') < 0 && (schemeUrl || word.IndexOf(':') < 0))
        {
            redacted = "";
            return false;
        }

        var builder = new StringBuilder(word.Length);
        var index = 0;
        var changed = false;
        while (index < word.Length)
        {
            var equals = word.IndexOf('=', index);
            var colon = schemeUrl ? -1 : word.IndexOf(':', index);
            int separator;
            if (equals < 0)
                separator = colon;
            else if (colon < 0)
                separator = equals;
            else
                separator = Math.Min(equals, colon);
            if (separator < 0)
            {
                builder.Append(word, index, word.Length - index);
                break;
            }

            var keyStart = separator;
            while (keyStart > index && IsAssignmentKeyChar(word[keyStart - 1]))
                keyStart--;
            var key = word[keyStart..separator];
            if (key.Length > 0 && SensitiveDataRedactionEnricher.IsSensitiveAssignmentKey(key))
            {
                builder.Append(word, index, separator + 1 - index);
                builder.Append("***");
                changed = true;
                index = separator + 1;
                // Query strings separate pairs with '&'. Any other assignment
                // value runs to the end of the word, so a value that itself
                // contains '&' is not left behind the marker.
                if (word[separator] == '=' && (schemeUrl || word.Contains('?')))
                {
                    while (index < word.Length && word[index] != '&')
                        index++;
                }
                else
                {
                    index = word.Length;
                }

                continue;
            }

            builder.Append(word, index, separator + 1 - index);
            index = separator + 1;
        }

        redacted = changed ? builder.ToString() : "";
        return changed;
    }

    private static bool IsAssignmentKeyChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-';

    private static string Quote(string text, char quote)
    {
        var builder = new StringBuilder(text.Length + 2);
        builder.Append(quote);
        foreach (var c in text)
        {
            if (c == quote || c == '\\')
                builder.Append('\\');
            builder.Append(c);
        }

        builder.Append(quote);
        return builder.ToString();
    }

    private readonly struct ShellWord(int start, int end, string decoded, bool quoted, char quote)
    {
        public int Start { get; } = start;
        public int End { get; } = end;
        public string Decoded { get; } = decoded;
        public bool Quoted { get; } = quoted;
        public char Quote { get; } = quote;
    }

    private static ShellWord ReadShellWord(string text, int index)
    {
        var start = index;
        if (text[index] is '"' or '\'')
        {
            var quote = text[index];
            index++;
            var decoded = new StringBuilder();
            while (index < text.Length && text[index] != quote)
            {
                if (quote == '"' && text[index] == '\\' && index + 1 < text.Length)
                {
                    var next = text[index + 1];
                    // POSIX double quotes: backslash is special only before these.
                    if (next is '$' or '`' or '"' or '\\' or '\n')
                    {
                        decoded.Append(next);
                        index += 2;
                        continue;
                    }
                }

                decoded.Append(text[index]);
                index++;
            }

            if (index < text.Length && text[index] == quote)
                index++;
            return new ShellWord(start, index, decoded.ToString(), quoted: true, quote);
        }

        var unquoted = new StringBuilder();
        while (index < text.Length)
        {
            if (text[index] == '\\' && index + 1 < text.Length)
            {
                unquoted.Append(text[index + 1]);
                index += 2;
                continue;
            }

            if (char.IsWhiteSpace(text[index]))
                break;
            unquoted.Append(text[index]);
            index++;
        }

        return new ShellWord(start, index, unquoted.ToString(), quoted: false, quote: '\0');
    }
}
