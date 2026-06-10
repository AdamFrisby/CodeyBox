using CodeyBox.Core;

namespace CodeyBox.Agents.Antigravity;

/// <summary>
/// Supplies the gateway model ids available to the Antigravity (<c>agy</c>) CLI.
///
/// <para><b>Why this is a static list, not a live read (verified 2026-06-10,
/// agy 1.0.7).</b> There is no reachable endpoint that enumerates the agy
/// gateway models for our credential:
/// <list type="bullet">
///   <item><description>The Code Assist host
///   (<c>cloudcode-pa.googleapis.com:retrieveUserQuota*</c>) answers 200 but with
///   the <em>Gemini Code Assist</em> catalog (gemini-2.5-flash, …) — the wrong
///   surface; the gateway model ids (gemini-3.5-flash-*, claude-*-thinking,
///   gpt-oss-120b-*) are absent, which is what produced the spurious "NOT in
///   provider model list" startup warnings.</description></item>
///   <item><description>The agy gateway host
///   (<c>daily-cloudcode-pa.googleapis.com</c>) returns 403 PERMISSION_DENIED on
///   <c>:retrieveUserQuota*</c> and <c>:fetchAvailableModels</c> for our
///   token.</description></item>
///   <item><description>The CLI's own <c>agy models</c> prints human display
///   names ("Gemini 3.5 Flash (High)"), not the canonical <c>--model</c> ids.</description></item>
/// </list>
/// So <see cref="AntigravityKnownModels.All"/> is authoritative — the same
/// curated list the config validator checks ids against. The probe exists to
/// satisfy the <see cref="IAgentModelListProbe"/> seam the startup validator
/// drives; returning the curated list makes valid configured ids validate
/// cleanly instead of perpetually "skipping validation".</para>
/// </summary>
public sealed class AntigravityModelListProbe : IAgentModelListProbe
{
    public AgentKind Kind => AgentKind.Antigravity;

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct) =>
        Task.FromResult(AgentModelListResult.Success(AntigravityKnownModels.All));
}
