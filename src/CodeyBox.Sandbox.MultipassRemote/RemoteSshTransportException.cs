namespace CodeyBox.Sandbox.MultipassRemote;

/// <summary>
/// Thrown when the SSH transport itself fails (connection refused, auth
/// rejected, network partition, key permission error, etc.) — distinct from
/// the remote command running and exiting non-zero. The orchestrator maps
/// this to a sandbox-level failure (recoverable: re-pickup the work item)
/// rather than treating it as an agent crash.
/// </summary>
public sealed class RemoteSshTransportException : Exception
{
    public RemoteSshTransportException(string message) : base(message) { }
    public RemoteSshTransportException(string message, Exception inner) : base(message, inner) { }
}
