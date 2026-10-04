using System.Collections.Concurrent;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Process-lifetime record of work items the graceful-shutdown path checkpointed
/// to a resumable state before VM teardown, plus the predicates that recognize
/// shutdown-caused sandbox failures.
/// </summary>
/// <remarks>
/// <para>On graceful shutdown, in-flight items are checkpointed first and their
/// sandboxes torn down afterwards. Workers still parked inside a sandbox
/// operation then observe the teardown's own doing — a disposed sandbox
/// (<c>The Incus sandbox is stopping, preserved, or disposed.</c>) or a VM start
/// SIGTERMed mid-flight (exit 143) — and must not classify that as a work
/// failure: the checkpoint is authoritative for the rest of the process
/// lifetime and any terminal write over it is refused.</para>
/// <para>The record is in-memory only, so the next process starts empty and
/// legitimately failing items can still reach <c>Failed</c> after a restart.
/// One entry per checkpointed item; the process exits right after shutdown, so
/// the set cannot grow without bound. Tests reset it via <see cref="Clear"/>
/// (internal) or simply use distinct item ids.</para>
/// </remarks>
public static class ShutdownCheckpointGuard
{
    /// <summary>
    /// Message fragment thrown by <c>IncusSandbox</c> once its lifecycle moves
    /// past running (both the <c>is stopping</c> and <c>is already
    /// stopping</c> variants end with this suffix). Kept as a fragment match
    /// so both call sites are covered without restating each full sentence.
    /// </summary>
    private const string SandboxDisposalMessageFragment = "stopping, preserved, or disposed";

    /// <summary>
    /// Exit code 128+15: the VM start process was SIGTERMed by host shutdown,
    /// not rejected by the infrastructure. Matched case-insensitively anywhere
    /// in the exception chain.
    /// </summary>
    private const string VmStartSigtermExitFragment = "exit code 143";

    private static readonly ConcurrentDictionary<WorkItemId, byte> Checkpointed = new();

    /// <summary>
    /// Records that the shutdown path checkpointed <paramref name="id"/> to
    /// its resumable state. Idempotent.
    /// </summary>
    public static void MarkCheckpointed(WorkItemId id) => Checkpointed[id] = 0;

    /// <summary>
    /// Returns true when <paramref name="id"/> was checkpointed by the
    /// shutdown path in this process. A checkpointed item must never move to
    /// a terminal state or lose its clone for the rest of the process
    /// lifetime.
    /// </summary>
    public static bool IsCheckpointed(WorkItemId id) => Checkpointed.ContainsKey(id);

    /// <summary>Test seam: drops all checkpoint marks in this process.</summary>
    internal static void Clear() => Checkpointed.Clear();

    /// <summary>
    /// Returns true when <paramref name="ex"/> (or its cause chain) is the
    /// sandbox reporting that its own lifecycle already ended — the shape a
    /// worker parked inside a sandbox operation observes after shutdown
    /// teardown disposes the sandbox underneath it.
    /// </summary>
    public static bool IsSandboxDisposalFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is InvalidOperationException or ObjectDisposedException
                && current.Message.Contains(SandboxDisposalMessageFragment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="ex"/> (or its cause chain) reports a
    /// sandbox-create/VM-start that died with exit code 143 — SIGTERM from
    /// host shutdown, not an infrastructure defect.
    /// </summary>
    public static bool IsShutdownVmStartInterruption(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains(VmStartSigtermExitFragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when a terminal failure write for <paramref name="id"/>
    /// must be skipped: host shutdown is in progress and the failure is either
    /// for an item the shutdown already checkpointed or is itself
    /// shutdown-caused (disposed sandbox underneath a parked worker, or a
    /// SIGTERMed VM start). Callers leave the checkpointed state alone so the
    /// next boot restarts cleanly.
    /// </summary>
    public static bool ShouldSuppressTerminalFailure(
        Exception ex,
        CancellationToken hostShutdownToken,
        WorkItemId id)
    {
        if (!hostShutdownToken.IsCancellationRequested)
        {
            return false;
        }

        return IsCheckpointed(id) || IsSandboxDisposalFailure(ex) || IsShutdownVmStartInterruption(ex);
    }
}
