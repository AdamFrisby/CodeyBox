namespace CodeyBox.Core;

/// <summary>
/// Compact binding stamped on Discord message-component buttons
/// (<c>custom_id</c>) and parsed back by the host's inbound endpoint.
/// Discord caps <c>custom_id</c> at 100 characters, so the binding is
/// <c>cb:{workItemId}:{questionId}:{answer}</c> with the answer in
/// base64url — far tighter than the JSON value Slack buttons carry.
/// Buttons whose binding would exceed the budget must not be posted;
/// the caller keeps the question answerable another way (answer/Agnes
/// links) instead of an unresolvable button.
///
/// <para>Lives in Core (like <see cref="NotificationCorrelation"/>) so the
/// provider plugin and the host endpoint bind and parse the same value.
/// One source of truth: do not re-implement this format elsewhere.</para>
/// </summary>
public static class DiscordButtonCodec
{
    /// <summary>Prefix marking a <c>custom_id</c> as a CodeyBox answer button.
    /// The inbound parser honours only this prefix; anything else is not ours.</summary>
    public const string Prefix = "cb:";

    /// <summary>Discord button <c>custom_id</c> limit in characters.</summary>
    public const int MaxCustomIdChars = 100;

    /// <summary>Encode one offered action as a button <c>custom_id</c>.
    /// Returns null when the binding does not fit the budget.</summary>
    public static string? Encode(string workItemId, string questionId, string answer)
    {
        if (string.IsNullOrEmpty(workItemId)
            || string.IsNullOrEmpty(questionId)
            || string.IsNullOrEmpty(answer))
            return null;
        if (workItemId.Contains(':') || questionId.Contains(':'))
            return null;
        var encodedAnswer = ToBase64Url(answer);
        var customId = $"{Prefix}{workItemId}:{questionId}:{encodedAnswer}";
        return customId.Length > MaxCustomIdChars ? null : customId;
    }

    /// <summary>Decode a button <c>custom_id</c> back to its binding.</summary>
    public static bool TryDecode(string? customId, out string workItemId, out string questionId, out string answer)
    {
        workItemId = questionId = answer = string.Empty;
        if (string.IsNullOrEmpty(customId)
            || customId.Length > MaxCustomIdChars
            || !customId.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        var rest = customId[Prefix.Length..];
        var first = rest.IndexOf(':');
        if (first <= 0)
            return false;
        var second = rest.IndexOf(':', first + 1);
        if (second <= first + 1 || second == rest.Length - 1)
            return false;
        workItemId = rest[..first];
        questionId = rest[(first + 1)..second];
        var decoded = FromBase64Url(rest[(second + 1)..]);
        if (decoded is null)
            return false;
        answer = decoded;
        return !string.IsNullOrEmpty(workItemId)
            && !string.IsNullOrEmpty(questionId)
            && !string.IsNullOrEmpty(answer);
    }

    private static string ToBase64Url(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string? FromBase64Url(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var padded = text.Replace('-', '+').Replace('_', '/');
        var remainder = padded.Length % 4;
        if (remainder == 1)
            return null;
        if (remainder != 0)
            padded += new string('=', 4 - remainder);
        try
        {
            var bytes = Convert.FromBase64String(padded);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
