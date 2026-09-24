using System.Reflection;
using CodeyBox.Agents;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Accessor for the in-sandbox ACP client shim that ships embedded in this
/// assembly (<c>Resources/devin-acp-client.py</c>). The shim is a Python 3
/// script — Python 3 is already a hard requirement of every CodeyBox sandbox
/// image (the credential-file writer execs it) — that spawns
/// <c>devin acp</c>, drives the JSON-RPC handshake, and folds every ACP
/// frame into a single-line <c>devin.acp</c> NDJSON envelope on stdout so
/// the agent-stream file keeps advancing while the agent works.
///
/// <para>Unlike the Claude ACP path (a NativeAOT bridge that impersonates an
/// IDE WebSocket for <c>claude --ide</c>), <c>devin acp</c> IS the ACP
/// server — it needs a plain stdio JSON-RPC client, which is what this shim
/// is. The two share only the framed-stdin delivery shape (base64 payload +
/// terminator + prompt), implemented once in
/// <c>CodeyBox.Agents.FramedStdin</c>.</para>
/// </summary>
internal static class DevinAcpShim
{
    internal const string EmbeddedResourceName = "devin-acp-client.py";

    /// <summary>
    /// Line that terminates the base64 shim block in the dispatch stdin
    /// frame; everything after it is the verbatim prompt. Base64 output can
    /// never contain this value, and a matching line inside the prompt is
    /// harmless — only the FIRST occurrence ends the shim block, and it is
    /// written before the prompt by construction.
    /// </summary>
    internal const string StdinEndMarker = "__CODEYBOX_DEVIN_ACP_SHIM_END__";

    /// <summary>
    /// The embedded shim bytes, loaded once. Returned to callers only as
    /// <see cref="ReadOnlyMemory{T}"/> so no caller can mutate the shared
    /// copy that every later dispatch ships to the sandbox.
    /// </summary>
    private static readonly Lazy<byte[]> ScriptBytes = new(LoadScriptBytesCore);

    /// <summary>Read the embedded shim script bytes (loaded once, then cached).</summary>
    public static ReadOnlyMemory<byte> LoadScriptBytes() => ScriptBytes.Value;

    private static byte[] LoadScriptBytesCore()
    {
        var asm = typeof(DevinAcpShim).Assembly;
        using var stream = asm.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"Devin ACP shim resource '{EmbeddedResourceName}' is missing from {asm.GetName().Name}.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// The dispatch exec stdin frame, built by
    /// <see cref="FramedStdin.Build"/>: the base64-encoded shim (wrapped at
    /// <see cref="FramedStdin.Base64LineWidth"/>), the end marker on its own
    /// line, then the prompt verbatim. The wrapper script decodes the shim
    /// to a temp file and pipes the rest to the prompt file, so the prompt
    /// never enters argv, the environment, or
    /// <c>/proc/&lt;pid&gt;/environ</c> — the same delivery guarantee the
    /// print-mode prompt file had.
    /// </summary>
    public static string BuildDispatchStdin(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        return FramedStdin.Build(
            Convert.ToBase64String(LoadScriptBytes().Span),
            StdinEndMarker,
            prompt);
    }
}
