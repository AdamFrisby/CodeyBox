namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// A checked <c>incus exec</c> step failed with a non-zero exit. The CLI merges
/// guest-process stderr into the captured stream, so that stderr is
/// guest-controlled and must never drive transient classification:
/// a guest printing any allowlisted signature must not forge a transient
/// deferral. The failure message therefore omits guest output, and the
/// transient classifier skips every fault on chains containing
/// this marker (mirroring <c>isExec</c> in <c>BuildAllowFailureException</c>).
/// Derives from <see cref="InvalidOperationException"/> so callers that only
/// distinguish Incus failures keep working.
/// </summary>
internal sealed class IncusExecCommandException : InvalidOperationException
{
    public IncusExecCommandException(string message)
        : base(message)
    {
    }

    public IncusExecCommandException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
