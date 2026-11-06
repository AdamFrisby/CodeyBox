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
/// line is redacted again per shell word. Adjacent quoted and unquoted
/// segments with no whitespace between them are one shell word, except a
/// bare credential flag, which keeps glued text as the next word
/// (<c>"--user"builder:pass</c>). URL userinfo
/// is removed from that decoded word with
/// <see cref="EmbeddedUrlUserInfo.StripToken"/> before the word is scanned
/// again, so a space or a sub-delimiter inside a password stays inside the
/// userinfo. A sensitive key with no in-word value redacts the following
/// word, and one more when that word is an HTTP auth scheme. Quoted text is
/// the same command language and is scrubbed with the same rules, bounded by
/// <see cref="MaxQuotedCommandDepth"/>.
/// </remarks>
public static class ShellCommandRedactor
{
    /// <summary>
    /// Quote nesting this redactor will open. Deeper quotes still have URL
    /// userinfo stripped with <see cref="EmbeddedUrlUserInfo.StripToken"/>,
    /// and the unparsed remainder is kept.
    /// </summary>
    public const int MaxQuotedCommandDepth = 8;

    // Exact-word or flag=value matches. The '=' boundary keeps --user-agent
    // and --password-stdin from matching --user or --password.
    private static readonly string[] CredentialFlags =
    [
        "--oauth2-bearer",
        "--proxy-user",
        "--password",
        "--user",
        "-U",
        "-u",
        "-p",
    ];

    /// <summary>
    /// Redacts URL userinfo, credential flags (including an attached <c>-p</c>
    /// and <c>--oauth2-bearer</c>), npm <c>_auth</c> values, and sensitive
    /// assignments in <paramref name="command"/>. A sensitive key with no
    /// in-word value redacts the next word, plus one more when that word is
    /// an HTTP auth scheme.
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
            return AppendQuotedWord(text, word, builder, depth);

        if (TryRedactInlineCredential(word.Decoded, out var inline))
        {
            builder.Append(inline);
            return word.End;
        }

        if (TryAppendPendingHeaderSecret(text, word, builder, out var headerEnd))
            return headerEnd;

        var stripped = EmbeddedUrlUserInfo.StripToken(word.Decoded);
        if (TryRedactSensitiveAssignment(stripped, out var assigned))
        {
            builder.Append(assigned);
            return word.End;
        }

        if (TakesFollowingWord(word.Decoded))
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

    private static int AppendQuotedWord(string text, ShellWord word, StringBuilder builder, int depth)
    {
        // Strip userinfo on the decoded word before the inner scan. A quoted
        // password may contain spaces; splitting first would separate that
        // password from the '@' that closes userinfo. At the depth cap the
        // inside of the quote is not parsed, but userinfo is still removed.
        var stripped = EmbeddedUrlUserInfo.StripToken(word.Decoded);
        var inner = depth >= MaxQuotedCommandDepth
            ? stripped
            : RedactWords(stripped, depth + 1);
        if (string.Equals(inner, word.Decoded, StringComparison.Ordinal))
            builder.Append(text, word.Start, word.End - word.Start);
        else if (word.Quote != '\0')
            builder.Append(Quote(inner, word.Quote));
        else
            builder.Append(inner);

        // Following-word checks use the raw decoded word, not the scrubbed
        // body. A spaceless word containing a sensitive fragment (NPM_TOKEN)
        // still consumes the next word outside this quote. Whitespace means
        // the quote is a script; those words are handled by the inner scan
        // below the depth cap.
        var outside = OutsideHeaderSecretNeed(word.Decoded);
        if (outside == OutsideHeaderNeed.SchemeAndSecret)
            return AppendFollowingHeaderValue(text, word.End, builder, schemeAlreadyConsumed: false);
        if (outside == OutsideHeaderNeed.SecretOnly)
            return AppendFollowingHeaderValue(text, word.End, builder, schemeAlreadyConsumed: true);
        if (TakesFollowingWord(word.Decoded))
            return AppendFollowingValue(text, word.End, builder);
        return word.End;
    }

    private static int AppendFollowingValue(string text, int index, StringBuilder builder) =>
        AppendRedactedFollowingWord(text, index, builder, out _);

    private static int AppendFollowingHeaderValue(
        string text,
        int index,
        StringBuilder builder,
        bool schemeAlreadyConsumed)
    {
        if (!schemeAlreadyConsumed)
        {
            index = AppendRedactedFollowingWord(text, index, builder, out var scheme);
            if (scheme is null || !IsHttpAuthScheme(scheme))
                return index;
        }

        return AppendRedactedFollowingWord(text, index, builder, out _);
    }

    private static int AppendRedactedFollowingWord(
        string text,
        int index,
        StringBuilder builder,
        out string? decoded)
    {
        decoded = null;
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
        decoded = value.Decoded;
        builder.Append("***");
        return value.End;
    }

    /// <summary>
    /// True when the next shell word is a credential. The check uses the raw
    /// decoded word: a spaceless word containing a sensitive fragment still
    /// consumes the next word. A URL does not, even when the password happens
    /// to contain that fragment.
    /// </summary>
    private static bool TakesFollowingWord(string word) =>
        word.IndexOf("://", StringComparison.Ordinal) < 0 && TakesFollowingCredential(word);

    private static bool TakesFollowingCredential(string word)
    {
        if (word.Length == 0 || ContainsWhitespace(word))
            return false;
        if (IsExactCredentialFlag(word) || IsShortOptionNextWord(word))
            return true;
        if (SensitiveDataRedactionEnricher.IsSensitiveAssignmentKey(word))
            return true;
        return IsNpmBareAuthKey(word);
    }

    private static bool ContainsWhitespace(string word) =>
        word.IndexOfAny([' ', '\t', '\r', '\n']) >= 0;

    private static bool TryRedactInlineCredential(string word, out string redacted)
    {
        if (TryRedactFlagAssignment(word, out redacted))
            return true;
        if (TryRedactAttachedShortOption(word, out redacted))
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
    /// A single-dash cluster is flags until the first letter that takes an
    /// argument. <c>u</c>/<c>U</c> is basic auth and <c>p</c> is a password.
    /// The attached span is the value when present; a cluster that ends on
    /// that letter takes the next word. A lone <c>-u</c>/<c>-U</c> whose
    /// remainder has no colon (<c>-update</c>) is not a credential: the scan
    /// stops there, so the <c>p</c> inside <c>-update</c> is not a password.
    /// </summary>
    private static ShortOptionValue ClassifyShortCredentialOption(string word, out string redacted)
    {
        redacted = "";
        if (word.Length < 2 || word[0] != '-' || word[1] == '-')
            return ShortOptionValue.None;

        for (var i = 1; i < word.Length; i++)
        {
            var c = word[i];
            if (c is 'u' or 'U')
            {
                var attached = word[(i + 1)..];
                if (attached.Length == 0)
                    return ShortOptionValue.NextWord;
                if (i == 1 && attached.IndexOf(':') < 0)
                    return ShortOptionValue.None;
                redacted = string.Concat(word.AsSpan(0, i + 1), " ***");
                return ShortOptionValue.Attached;
            }

            if (c == 'p')
            {
                if (i + 1 >= word.Length)
                    return ShortOptionValue.NextWord;
                redacted = string.Concat(word.AsSpan(0, i + 1), " ***");
                return ShortOptionValue.Attached;
            }

            if (!char.IsAsciiLetter(c))
                return ShortOptionValue.None;
        }

        return ShortOptionValue.None;
    }

    private static bool TryRedactAttachedShortOption(string word, out string redacted) =>
        ClassifyShortCredentialOption(word, out redacted) == ShortOptionValue.Attached;

    private static bool IsShortOptionNextWord(string word) =>
        ClassifyShortCredentialOption(word, out _) == ShortOptionValue.NextWord;

    private enum ShortOptionValue
    {
        None = 0,
        NextWord,
        Attached,
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

    private enum OutsideHeaderNeed
    {
        None = 0,
        SchemeAndSecret,
        SecretOnly,
    }

    /// <summary>
    /// A sensitive key whose secret is a following shell word. The key may
    /// stand alone (<c>Authorization</c>, <c>Password:</c>) or already contain
    /// only an auth scheme (<c>Authorization:Bearer</c>). An in-word secret
    /// that is not a scheme is left for <see cref="TryRedactSensitiveAssignment"/>.
    /// </summary>
    private static bool TryGetPendingHeaderSecret(string word, out int separatorEnd, out bool schemeEmbedded)
    {
        separatorEnd = 0;
        schemeEmbedded = false;
        if (word.Length == 0 || ContainsWhitespace(word) || word.Contains("://", StringComparison.Ordinal))
            return false;
        if (IsExactCredentialFlag(word) || IsNpmBareAuthKey(word))
            return false;

        var colon = word.IndexOf(':');
        var equals = word.IndexOf('=');
        int separator;
        if (colon < 0)
            separator = equals;
        else if (equals < 0)
            separator = colon;
        else
            separator = Math.Min(colon, equals);

        if (separator < 0)
        {
            if (!SensitiveDataRedactionEnricher.IsSensitiveAssignmentKey(word))
                return false;
            return true;
        }

        if (separator == 0)
            return false;

        var key = word[..separator];
        if (!SensitiveDataRedactionEnricher.IsSensitiveAssignmentKey(key))
            return false;

        var value = word[(separator + 1)..];
        separatorEnd = separator + 1;
        if (value.Length == 0)
            return true;
        if (!IsHttpAuthScheme(value))
            return false;

        schemeEmbedded = true;
        return true;
    }

    private static bool TryAppendPendingHeaderSecret(string text, ShellWord word, StringBuilder builder, out int end)
    {
        end = word.End;
        if (!TryGetPendingHeaderSecret(word.Decoded, out var separatorEnd, out var schemeEmbedded))
            return false;

        if (schemeEmbedded)
        {
            builder.Append(word.Decoded.AsSpan(0, separatorEnd));
            builder.Append("***");
        }
        else
        {
            builder.Append(text, word.Start, word.End - word.Start);
        }

        end = AppendFollowingHeaderValue(text, word.End, builder, schemeEmbedded);
        return true;
    }

    /// <summary>
    /// The quoted word itself is only a sensitive key, or a key plus an auth
    /// scheme, so the secret is the next word outside the quotes. A longer
    /// quote already holds that secret and the inner scan redacts it.
    /// </summary>
    private static OutsideHeaderNeed OutsideHeaderSecretNeed(string decoded)
    {
        if (decoded.Length == 0 || decoded.Contains("://", StringComparison.Ordinal))
            return OutsideHeaderNeed.None;

        var parts = decoded.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > 2)
            return OutsideHeaderNeed.None;
        if (!TryGetPendingHeaderSecret(parts[0], out _, out var schemeEmbedded))
            return OutsideHeaderNeed.None;
        if (schemeEmbedded)
            return parts.Length == 1 ? OutsideHeaderNeed.SecretOnly : OutsideHeaderNeed.None;
        if (parts.Length == 1)
            return OutsideHeaderNeed.SchemeAndSecret;
        if (IsHttpAuthScheme(parts[1]))
            return OutsideHeaderNeed.SecretOnly;
        return OutsideHeaderNeed.None;
    }

    private static bool IsHttpAuthScheme(string word) =>
        word.Equals("Basic", StringComparison.OrdinalIgnoreCase)
        || word.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
        || word.Equals("Digest", StringComparison.OrdinalIgnoreCase)
        || word.Equals("Negotiate", StringComparison.OrdinalIgnoreCase)
        || word.Equals("NTLM", StringComparison.OrdinalIgnoreCase);

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
        var decoded = new StringBuilder();
        var anyQuoted = false;
        var segments = 0;
        while (index < text.Length && !char.IsWhiteSpace(text[index]))
        {
            var mark = index;
            if (text[index] is '"' or '\'')
            {
                anyQuoted = true;
                ReadQuotedSegment(text, ref index, text[index], decoded);
            }
            else
            {
                ReadUnquotedSegment(text, ref index, decoded);
            }

            if (index == mark)
                break;

            segments++;
            // "--user"builder:pass is a flag plus a glued value, not one token.
            // A fragment that contains :// keeps joining so a password split
            // across quotes still shares a word with the closing '@'.
            if (segments == 1
                && index < text.Length
                && !char.IsWhiteSpace(text[index])
                && TakesFollowingWord(decoded.ToString()))
            {
                break;
            }
        }

        var quote = text[start] is '"' or '\'' ? text[start] : '\0';
        return new ShellWord(start, index, decoded.ToString(), anyQuoted, quote);
    }

    private static void ReadQuotedSegment(string text, ref int index, char quote, StringBuilder decoded)
    {
        index++;
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
    }

    private static void ReadUnquotedSegment(string text, ref int index, StringBuilder decoded)
    {
        while (index < text.Length)
        {
            if (text[index] == '\\' && index + 1 < text.Length)
            {
                decoded.Append(text[index + 1]);
                index += 2;
                continue;
            }

            if (char.IsWhiteSpace(text[index]) || text[index] is '"' or '\'')
                break;
            decoded.Append(text[index]);
            index++;
        }
    }
}
