namespace CodeyBox.Sandbox.MultipassRemote;

/// <summary>
/// Thrown by remote SSH staging/exec paths. Most instances represent the SSH
/// transport itself failing (connection refused, auth rejected, network
/// partition, key permission error, etc.), but stage-out validation can also
/// reject sandbox-controlled archive content. Callers must only mark executor
/// hosts unhealthy for <see cref="Kind"/> values that indicate transport health.
/// </summary>
public sealed class RemoteSshTransportException : Exception
{
    public RemoteSshTransportException(string message)
        : this(message, RemoteSshTransportFailureKind.Transport)
    { }

    public RemoteSshTransportException(string message, Exception inner)
        : this(message, RemoteSshTransportFailureKind.Transport, inner)
    { }

    public RemoteSshTransportException(string message, RemoteSshTransportFailureKind kind)
        : base(message)
        => Kind = kind;

    public RemoteSshTransportException(string message, RemoteSshTransportFailureKind kind, Exception inner)
        : base(message, inner)
        => Kind = kind;

    public RemoteSshTransportFailureKind Kind { get; }
    public bool IsHostTransportFailure => Kind == RemoteSshTransportFailureKind.Transport;
}

public enum RemoteSshTransportFailureKind
{
    Transport,
    RemoteCommand,
    ContentValidation,
    ResourceLimit,
}
