using System.Text;

namespace CodeyBox.Core;

/// <summary>
/// Replaces embedded URL userinfo (<c>scheme://user:password@host</c>) with
/// <c>scheme://***@host</c>. One rule for free text (git stderr, log lines)
/// and for a single shell word.
/// </summary>
/// <remarks>
/// The last <c>@</c> in the authority is the separator, so a password that
/// itself contains <c>@</c> is removed with the userinfo. The authority ends
/// at <c>/</c>, <c>?</c>, <c>#</c>, or the end of the token.
/// <see cref="Strip"/> also ends the authority at whitespace so a later
/// <c>git@host</c> is not swallowed. <see cref="StripToken"/> does not: a
/// decoded shell word can contain spaces that were quoted, and those spaces
/// are still inside the password. RFC 3986 sub-delimiters
/// (<c>! $ &amp; ' ( ) * + , ; =</c>) stay inside userinfo; ending the
/// authority on them leaves the rest of the password in front of the host.
/// Text without <c>scheme://</c>, including scp-style <c>git@host:path</c>,
/// is unchanged.
/// </remarks>
public static class EmbeddedUrlUserInfo
{
    /// <summary>Marker written in place of userinfo, including the separator.</summary>
    public const string RedactedUserInfo = "***@";

    /// <summary>
    /// Strips userinfo from every absolute URL in free text. Whitespace ends
    /// the authority.
    /// </summary>
    public static string Strip(string? text) => StripCore(text, whitespaceEndsAuthority: true);

    /// <summary>
    /// Strips userinfo from one already-decoded shell word. Whitespace does
    /// not end the authority, because a quoted password may contain spaces
    /// and the caller has already bounded the word.
    /// </summary>
    public static string StripToken(string? text) => StripCore(text, whitespaceEndsAuthority: false);

    private static string StripCore(string? text, bool whitespaceEndsAuthority)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var scheme = FindUrlSchemeStart(text, index);
            if (scheme < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            builder.Append(text, index, scheme - index);
            var separator = text.IndexOf("://", scheme, StringComparison.Ordinal);
            builder.Append(text, scheme, separator + 3 - scheme);
            var authorityStart = separator + 3;
            var authorityEnd = authorityStart;
            while (authorityEnd < text.Length && !EndsAuthority(text[authorityEnd], whitespaceEndsAuthority))
                authorityEnd++;

            var at = -1;
            for (var i = authorityEnd - 1; i >= authorityStart; i--)
            {
                if (text[i] == '@')
                {
                    at = i;
                    break;
                }
            }

            if (at >= authorityStart)
            {
                builder.Append(RedactedUserInfo);
                builder.Append(text, at + 1, authorityEnd - (at + 1));
            }
            else
            {
                builder.Append(text, authorityStart, authorityEnd - authorityStart);
            }

            index = authorityEnd;
        }

        return builder.ToString();
    }

    private static int FindUrlSchemeStart(string text, int start)
    {
        var index = start;
        while (index < text.Length)
        {
            var separator = text.IndexOf("://", index, StringComparison.Ordinal);
            if (separator < 0)
                return -1;
            var scheme = separator;
            while (scheme > start && IsUrlSchemeChar(text[scheme - 1]))
                scheme--;
            // The walk stops at `start`, so a scheme glued to a scheme
            // character before this window is not rejected by the loop.
            // Check that character too, including when scheme == start.
            if (scheme < separator
                && char.IsAsciiLetter(text[scheme])
                && (scheme == 0 || !IsUrlSchemeChar(text[scheme - 1])))
                return scheme;
            index = separator + 3;
        }

        return -1;
    }

    private static bool IsUrlSchemeChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.';

    private static bool EndsAuthority(char c, bool whitespaceEndsAuthority) =>
        c is '/' or '?' or '#' || (whitespaceEndsAuthority && char.IsWhiteSpace(c));
}
