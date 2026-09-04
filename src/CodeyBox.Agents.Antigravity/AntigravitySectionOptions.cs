namespace CodeyBox.Agents.Antigravity;

/// <summary>
/// Operator settings for the Google Antigravity (<c>agy</c>) agent. Bind from
/// <c>CodeyBox:Antigravity</c>.
/// </summary>
public sealed class AntigravitySectionOptions
{
    /// <summary>
    /// Per-model-response wait passed to agy as <c>--print-timeout</c>, in minutes. agy's own default
    /// is 5 minutes and it aborts the whole session when a single turn exceeds it, which a large work
    /// item does. Zero leaves agy's default in place.
    /// </summary>
    public int PrintTimeoutMinutes { get; set; } = 20;

    /// <summary>
    /// <c>User-Agent</c> presented to the gateway's quota meter. Null uses
    /// <see cref="AntigravityQuotaProbe.DefaultQuotaUserAgent"/>.
    ///
    /// <para>The gateway gates its quota RPC on client identity — the same credential answers
    /// <c>403 SUBSCRIPTION_REQUIRED</c> under any other agent string — so this must match the
    /// <c>agy</c> build actually installed on the host. <b>Update it when you upgrade the CLI:</b>
    /// claiming a version that does not exist is worse than claiming none, and a stale value simply
    /// loses the numeric reading (the probe degrades to the liveness read rather than failing).
    /// The current value is visible in <c>agy --version</c> plus the CLI's own request line.</para>
    /// </summary>
    public string? QuotaUserAgent { get; set; }

    /// <summary>
    /// Path to the agy OAuth bundle the quota probe reads, re-read on every probe. Null falls back to
    /// the <c>CODEYBOX_ANTIGRAVITY_OAUTH_CREDS_JSON</c> environment variable.
    ///
    /// <para><b>Why a file rather than the env var.</b> agy's access token lives about an hour and is
    /// refreshed into the system keyring by the CLI itself. An environment variable is captured once at
    /// process start, so the probe's copy expires mid-run and every subsequent read returns
    /// <c>401</c> — which the router treats as UNKNOWN and fails open, dispatching with no quota gate.
    /// Reading a file per probe lets an external refresher (see <c>codey-dump-antigravity-token.sh</c>,
    /// scheduled by the <c>codeybox-agy-token-refresh</c> timer) keep the credential live without
    /// restarting the orchestrator.</para>
    /// </summary>
    public string? OAuthTokenFile { get; set; }
}
