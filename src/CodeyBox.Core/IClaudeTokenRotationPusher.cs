namespace CodeyBox.Core;

/// <summary>
/// Tracks sandboxes that are actively running a Claude agent so that, when the
/// host's <c>~/.claude/.credentials.json</c> rotates mid-run, the fresh access
/// token can be pushed into each running VM before its next Anthropic call
/// 401s. Closes the gap left by PR #98 (which closed the host-vs-VM refresh
/// race, but stopped at "the VM will fail with 401 if its access_token goes
/// stale mid-run"). The pusher preserves PR #98's invariant: only the host's
/// claude CLI ever sees the refresh_token; the VM only ever receives the
/// sanitised access_token bundle.
/// </summary>
public interface IClaudeTokenRotationPusher
{
    /// <summary>
    /// Marks <paramref name="sandbox"/> as actively running a Claude agent.
    /// Subsequent rotations of the watched credentials file will push the
    /// fresh sanitised bundle into the VM's <c>~/.claude/.credentials.json</c>.
    /// Disposing the returned token unregisters the sandbox (it must no
    /// longer be considered an active Claude-running VM).
    /// </summary>
    IDisposable RegisterActiveSandbox(ISandbox sandbox);
}
