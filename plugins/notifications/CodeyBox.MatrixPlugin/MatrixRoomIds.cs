namespace CodeyBox.MatrixPlugin;

/// <summary>
/// Exact-match validation for Matrix room IDs. The sink carries its own guard:
/// only syntactically valid room IDs (<c>!localpart:server</c>) are ever
/// interpolated into request paths — a recipient string that is not a room ID
/// (a user ID, alias, URL, or path traversal) is refused before it can select
/// a destination.
/// </summary>
internal static class MatrixRoomIds
{
    /// <summary>Upper bound on a room ID's length. The spec caps the server
    /// name at 255 bytes; a whole-ID cap keeps an unbounded recipient from
    /// becoming an unbounded path segment.</summary>
    public const int MaxRoomIdChars = 255;

    /// <summary>Whether <paramref name="roomId"/> is a syntactically valid
    /// Matrix room ID: starts with <c>!</c>, contains exactly one
    /// <c>:</c> separating non-empty localpart and server parts, carries no
    /// whitespace, slash, query, or fragment characters, and fits the length
    /// bound. The server part itself is opaque here — the homeserver owns
    /// its namespace; this check only stops non-room strings reaching the
    /// path sink.</summary>
    public static bool IsValid(string? roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
            return false;
        if (roomId.Length > MaxRoomIdChars)
            return false;
        if (!roomId.StartsWith('!'))
            return false;
        var colon = roomId.IndexOf(':');
        if (colon <= 1 || colon == roomId.Length - 1)
            return false;
        if (roomId.IndexOf(':', colon + 1) >= 0)
            return false;
        foreach (var c in roomId)
        {
            if (char.IsWhiteSpace(c) || c is '/' or '?' or '#' or '@')
                return false;
        }
        return true;
    }
}
