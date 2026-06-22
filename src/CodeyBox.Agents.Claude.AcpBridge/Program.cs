namespace CodeyBox.Agents.Claude.AcpBridge;

/// <summary>
/// In-sandbox ACP bridge sidecar — the C# port of the original Node.js
/// implementation that used to live in AcpBridgeScript.cs.
///
/// <para>Why this exists. The Agent Client Protocol is JSON-RPC 2.0 over a
/// connection-oriented transport; <c>claude --ide</c> discovers its peer via
/// the lockfile mechanism (<c>~/.claude/ide/&lt;port&gt;.lock</c>) and
/// connects to a WebSocket the "IDE" hosts. Our orchestrator runs on the
/// HOST and cannot expose a WebSocket directly visible to the in-sandbox
/// claude in a provider-portable way (process / bubblewrap / multipass each
/// have different network reachability). The pragmatic answer is a small
/// in-sandbox bridge: it hosts the WebSocket inside the sandbox (so claude
/// can connect via the lockfile), writes the lockfile, spawns
/// <c>claude --ide</c>, and pipes JSON-RPC frames between the in-sandbox
/// WebSocket and its own STDIO — which is the pipe we get from
/// <c>sandbox.ExecAsync</c>. All ACP traffic travels host ↔ bridge stdio ↔
/// in-VM WebSocket ↔ claude, with no extra network configuration.</para>
///
/// <para>Wire format on the bridge's stdio: line-delimited JSON envelopes
/// matching the original JS bridge bit-for-bit, so the host-side observer
/// (<c>AcpClaudeTransport.AcpSession.ObserveBridgeOutput</c>) keeps working
/// unchanged across the language migration.</para>
/// </summary>
internal static class Program
{
    public static async Task<int> Main()
    {
        await using var bridge = new Bridge();
        return await bridge.RunAsync().ConfigureAwait(false);
    }
}
