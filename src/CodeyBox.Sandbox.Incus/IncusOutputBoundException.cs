namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// An Incus CLI invocation was terminated (or its output cut) for exceeding
/// its configured output bound. This is the diagnosable, infrastructure-side
/// volume signal — distinct from timeouts and non-zero exits — so callers can
/// report it as the <c>output-bound</c> failure kind instead of generic
/// infrastructure termination.
/// <para>
/// Derives from <see cref="InvalidOperationException"/> so callers that only
/// distinguish Incus failures keep working; <see cref="Operation"/> names the
/// incus subcommand or lifecycle step whose output overflowed.
/// </para>
/// </summary>
internal sealed class IncusOutputBoundException : InvalidOperationException
{
    public IncusOutputBoundException(string operation, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        Operation = operation;
    }

    /// <summary>
    /// The incus subcommand (e.g. <c>exec</c>, <c>file pull</c>) or lifecycle
    /// step whose output exceeded its bound. Never contains untrusted
    /// argument values.
    /// </summary>
    public string Operation { get; }
}
