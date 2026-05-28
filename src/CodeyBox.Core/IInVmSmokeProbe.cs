namespace CodeyBox.Core;

/// <summary>
/// Declares the in-sandbox commands that prove an agent's CLI is actually
/// runnable inside a freshly-cloned baseline image. Unlike
/// <see cref="IAgentSmokeProbe"/> — which only checks that the orchestrator
/// host holds the right credential env-vars — an in-VM probe is exec'd
/// <em>inside</em> the sandbox so it catches failures the host can't see:
/// the binary missing from PATH (exit 127), an auth file materialised to the
/// wrong path, or a CLI that refuses to run non-interactively.
///
/// <para>The implementation lives next to the agent's
/// <see cref="IAgentRunner"/> so the binary name and credential-materialisation
/// path stay in lock-step with the real runner — drift between the two is the
/// exact failure mode this probe exists to catch (see PR #138).</para>
///
/// <para>Agents without an in-VM CLI simply have no registered implementation,
/// and <c>InVmSmokeProber</c> skips them — same convention as
/// <see cref="IAgentSmokeProbe"/>.</para>
/// </summary>
public interface IInVmSmokeProbe
{
    AgentKind Kind { get; }

    /// <summary>
    /// The ordered command sequence to exec inside the sandbox. Steps run in
    /// order and short-circuit on the first failure. When
    /// <paramref name="credential"/> is null the probe should return only the
    /// credential-independent steps (e.g. <c>--version</c>) so a missing
    /// credential never produces a false binary-failure exclusion.
    /// </summary>
    IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential);
}

/// <summary>
/// One command in an in-VM smoke sequence. The step passes when the exec
/// returns exit 0.
///
/// <para>The check is intentionally exit-code-only rather than output-text
/// matching: agent CLIs report auth state differently and their wording drifts
/// between releases, so a substring like "Logged in" both false-passes ("Not
/// logged in" contains it) and risks false-benching a healthy agent on a
/// reworded message. <c>agent status</c> / <c>opencode providers</c> return
/// non-zero when the credential is absent or unreadable — which is exactly the
/// auth-path-drift signal (PR #138) — and 0 when authenticated, so the exit
/// code is the robust discriminator.</para>
/// </summary>
/// <param name="Argv">Command + args to exec inside the sandbox.</param>
/// <param name="Stdin">Optional stdin piped to the command.</param>
/// <param name="FailureHint">
/// Short human-readable label included in the failure reason so operators can
/// tell which check tripped (e.g. "binary not on PATH", "not logged in").
/// </param>
public sealed record InVmSmokeStep(
    IReadOnlyList<string> Argv,
    string? Stdin = null,
    string? FailureHint = null);
