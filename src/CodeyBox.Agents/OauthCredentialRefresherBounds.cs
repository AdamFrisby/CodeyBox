namespace CodeyBox.Agents;

/// <summary>
/// Hot-reloadable bounds for the OAuth credential refreshers
/// (<see cref="OauthCredentialFileRefresher"/> and its provider-specific
/// subclasses). Both caps guard network- or child-process-controlled bytes
/// from being buffered without a limit:
///
/// <list type="bullet">
///   <item>OAuth refresh response bodies, read through
///   <see cref="BoundedHttpResponseReader"/> with
///   <see cref="MaxRefreshBodyBytes"/> enforced <i>before</i> buffering
///   (streaming under <c>HttpCompletionOption.ResponseHeadersRead</c>).</item>
///   <item>Agent-CLI child-process stdout/stderr captured by
///   <c>OauthCredentialFileRefresher.ExecuteCliProcessAsync</c>, truncated at
///   <see cref="MaxCliOutputChars"/> per stream while still draining the
///   pipes so the child never blocks on a full buffer.</item>
/// </list>
///
/// <para>
/// Follows the <see cref="SessionResumeOptions"/> static-snapshot pattern:
/// the values are read on every use (so <c>CodeyBox:QuotaRouter</c> edits
/// applied through <c>AgentConfigHotReload</c> take effect on the next
/// refresh without a restart) and writes clamp into the documented range so
/// an operator typo fails closed to a bounded value rather than an unbounded
/// read. The default refresh-body cap (8 KiB) preserves the historical Gemini
/// limit; a legit refresh payload is a few hundred bytes of JSON.
/// </para>
/// </summary>
public static class OauthCredentialRefresherBounds
{
    /// <summary>Default cap for OAuth refresh response bodies, in bytes.</summary>
    public const int DefaultMaxRefreshBodyBytes = 8192;

    /// <summary>Smallest accepted refresh-body cap, in bytes. Below this a
    /// legitimate provider payload could not fit, so values clamp up.</summary>
    public const int MinMaxRefreshBodyBytes = 1024;

    /// <summary>Largest accepted refresh-body cap, in bytes. Matches
    /// <see cref="BoundedHttpResponseReader.MaximumBodyBytes"/> so the bound
    /// never exceeds what the underlying reader supports.</summary>
    public const int MaxMaxRefreshBodyBytes = BoundedHttpResponseReader.MaximumBodyBytes;

    /// <summary>Default per-stream cap for CLI child-process output, in chars.</summary>
    public const int DefaultMaxCliOutputChars = 1024 * 1024;

    /// <summary>Smallest accepted per-stream CLI output cap, in chars.</summary>
    public const int MinMaxCliOutputChars = 4096;

    /// <summary>Largest accepted per-stream CLI output cap, in chars.</summary>
    public const int MaxMaxCliOutputChars = 16 * 1024 * 1024;

    private static int _maxRefreshBodyBytes = DefaultMaxRefreshBodyBytes;
    private static int _maxCliOutputChars = DefaultMaxCliOutputChars;

    /// <summary>Current cap for OAuth refresh response bodies, in bytes.</summary>
    public static int MaxRefreshBodyBytes => Volatile.Read(ref _maxRefreshBodyBytes);

    /// <summary>Current per-stream cap for CLI child-process output, in chars.</summary>
    public static int MaxCliOutputChars => Volatile.Read(ref _maxCliOutputChars);

    /// <summary>
    /// Update the refresh-body cap. Out-of-range values clamp to
    /// [<see cref="MinMaxRefreshBodyBytes"/>, <see cref="MaxMaxRefreshBodyBytes"/>].
    /// </summary>
    public static void SetMaxRefreshBodyBytes(int value)
    {
        Volatile.Write(ref _maxRefreshBodyBytes, Math.Clamp(value, MinMaxRefreshBodyBytes, MaxMaxRefreshBodyBytes));
    }

    /// <summary>
    /// Update the per-stream CLI output cap. Out-of-range values clamp to
    /// [<see cref="MinMaxCliOutputChars"/>, <see cref="MaxMaxCliOutputChars"/>].
    /// </summary>
    public static void SetMaxCliOutputChars(int value)
    {
        Volatile.Write(ref _maxCliOutputChars, Math.Clamp(value, MinMaxCliOutputChars, MaxMaxCliOutputChars));
    }
}
