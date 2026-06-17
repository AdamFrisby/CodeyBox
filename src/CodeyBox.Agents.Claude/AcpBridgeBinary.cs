using System.IO;
using System.Reflection;
using System.Text;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Accessor for the ACP bridge native binary that ships embedded in this
/// assembly. The bridge is produced by the sibling
/// <c>CodeyBox.Agents.Claude.AcpBridge</c> project as a self-contained,
/// statically-linked NativeAOT ELF and copied into
/// <c>Resources/acp-bridge</c> by <c>scripts/publish-acp-bridge.sh</c>.
///
/// <para>Replaces the prior <c>AcpBridgeScript</c> Node.js source string —
/// the C# bridge is a drop-in replacement behind <see cref="AcpClaudeTransport"/>:
/// same lockfile semantics, same <c>claude --ide</c> spawn, same JSON-RPC
/// piping, same hello/acp_send/shutdown stdin envelopes, same observable
/// stdout envelopes, so <c>ObserveBridgeOutput</c> keeps parsing unchanged
/// across the language port.</para>
///
/// <para>On build hosts that have not run the publish script, a placeholder
/// resource is embedded instead. <see cref="LoadBinary"/> detects the
/// placeholder sentinel and surfaces a friendly failure mode so the
/// orchestrator falls back to the print transport rather than execing a
/// non-binary inside the sandbox.</para>
/// </summary>
internal static class AcpBridgeBinary
{
    /// <summary>
    /// Path inside the sandbox where the orchestrator materialises the
    /// bridge binary before invoking it. Lives under <c>~/.codeybox</c>
    /// rather than the workspace so it survives between turns even when
    /// the workspace is a fresh checkout.
    /// </summary>
    public const string BridgeBinaryPath = "$HOME/.codeybox/claude-acp-bridge";

    /// <summary>
    /// Maximum bridge wall-clock per turn. Hard cap on the bridge subprocess
    /// inside the sandbox so a wedged claude / wedged WebSocket cannot pin
    /// the worker forever — the bridge auto-exits, the worker observes the
    /// failure, and the configured fallback path (print transport) picks up
    /// the next turn. Conservatively long.
    /// </summary>
    public const int TurnTimeoutSeconds = 900;

    /// <summary>
    /// LogicalName of the embedded resource carrying the bridge bytes.
    /// Same value used by both real-binary and placeholder
    /// <c>EmbeddedResource</c> items in the .csproj — exactly one is
    /// included per build.
    /// </summary>
    internal const string EmbeddedResourceName = "acp-bridge";

    /// <summary>
    /// Sentinel marker at the start of the placeholder file
    /// (see <c>Resources/acp-bridge.placeholder</c>). When the embedded
    /// resource starts with this byte sequence, the build host did not run
    /// the publish script and the runtime path must degrade.
    /// </summary>
    private static readonly byte[] PlaceholderSentinel =
        Encoding.ASCII.GetBytes("CODEYBOX_ACP_BRIDGE_PLACEHOLDER");

    /// <summary>
    /// Cached resource bytes — JIT-load once, then reuse for every turn.
    /// </summary>
    private static byte[]? _cached;
    private static bool _isPlaceholderCached;

    /// <summary>
    /// True when the build embedded the placeholder rather than a real
    /// bridge binary. The transport uses this to surface an early, clear
    /// degradation reason instead of attempting to materialise (and exec)
    /// a non-binary inside the sandbox.
    /// </summary>
    public static bool IsPlaceholderBuild
    {
        get
        {
            _ = LoadBinary();
            return _isPlaceholderCached;
        }
    }

    /// <summary>
    /// Read the embedded bridge bytes. Returns the bytes (possibly the
    /// placeholder) — callers must consult <see cref="IsPlaceholderBuild"/>
    /// before deciding whether to ship the bytes into the sandbox.
    /// </summary>
    public static byte[] LoadBinary()
    {
        if (_cached is not null) return _cached;
        var asm = typeof(AcpBridgeBinary).Assembly;
        using var stream = asm.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"ACP bridge resource '{EmbeddedResourceName}' is missing from {asm.GetName().Name}. "
                + "Build hosts must run scripts/publish-acp-bridge.sh (or commit the placeholder) before "
                + "the Claude agent assembly will embed it.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        _isPlaceholderCached = StartsWith(bytes, PlaceholderSentinel);
        _cached = bytes;
        return bytes;
    }

    private static bool StartsWith(byte[] data, byte[] prefix)
    {
        if (data.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
            if (data[i] != prefix[i]) return false;
        return true;
    }
}
