namespace CodeyBox.Orchestrator;

/// <summary>
/// Linux /proc-based <see cref="IAgentActivitySource"/>. Scans /proc for
/// processes whose <c>comm</c> name matches a known agent binary, then reads
/// their CPU tick counters and open-socket count.
///
/// <para><b>CPU</b>: parsed from <c>/proc/&lt;pid&gt;/stat</c> fields
/// <c>utime</c> + <c>stime</c> (fields 14–15 after the closing parenthesis of
/// the command name). The accumulating counter is compared between consecutive
/// samples; a non-zero delta means the process burned CPU.</para>
///
/// <para><b>Network</b>: the number of open socket file descriptors in
/// <c>/proc/&lt;pid&gt;/fd</c> (symlinks whose target starts with
/// <c>socket:</c>). This is per-process and avoids false positives from other
/// processes' connections, but requires read permission on
/// <c>/proc/&lt;pid&gt;/fd</c> (root or same UID as the process). On
/// Bubblewrap sandboxes the orchestrator owns the child process, so
/// permission is granted. On Multipass (separate VM), the agent PID is not
/// visible from the host and <see cref="TryRead"/> returns <c>null</c>.</para>
///
/// <para><b>Ancestor filter</b>: only processes whose parent chain reaches
/// the orchestrator's own PID are counted. This avoids false-positives from
/// host-side <c>claude</c>/<c>codex</c>/<c>gemini</c> CLIs that the operator
/// runs interactively (e.g. Claude Code on a developer workstation), which
/// would otherwise be matched as "the agent" and cause spurious stuck-detection
/// when the operator's CLI is idle. On Multipass the agent isn't a host child
/// at all, so the filter rejects everything and the probe is correctly
/// disabled (matching the comment above).</para>
///
/// <para>In multi-worker deployments where several agents run concurrently,
/// this source aggregates stats across all matching processes. A non-zero
/// CPU delta from any one of them counts as "active", which is conservative:
/// the probe only fires when every visible agent is idle, not just one.</para>
/// </summary>
internal sealed class ProcFsAgentActivitySource : IAgentActivitySource
{
    // Known agent binary names (basename, matched against /proc/<pid>/comm).
    private static readonly string[] AgentComms =
    [
        "claude", "codex", "gemini", "copilot",
    ];

    // Cap on parent-chain walks. Sandbox process trees are very shallow
    // (orchestrator → bwrap/multipass-exec → agent), but a malformed /proc
    // (PPID cycle, etc.) could otherwise loop forever. 32 is well past any
    // realistic depth.
    private const int MaxAncestorWalk = 32;

    public ActivitySample? TryRead()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        var orchestratorPid = Environment.ProcessId;
        long totalCpuTicks = 0;
        int totalSockets = 0;
        bool found = false;

        try
        {
            foreach (var procDir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(procDir), out var pid))
                    continue;

                string comm;
                try { comm = File.ReadAllText(Path.Combine(procDir, "comm")).Trim(); }
                catch { continue; }

                if (!Array.Exists(AgentComms, n => string.Equals(n, comm, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Read CPU ticks (and PPID for the ancestor filter).
                string stat;
                try { stat = File.ReadAllText(Path.Combine(procDir, "stat")); }
                catch { continue; }

                if (!TryParseStat(stat, out var cpuTicks, out _))
                    continue;

                if (!IsDescendantOf(pid, orchestratorPid))
                    continue;

                totalCpuTicks += cpuTicks;
                found = true;

                // Count open socket FDs (best-effort; may fail without permission)
                totalSockets += CountOpenSockets(pid);
            }
        }
        catch
        {
            // /proc enumeration failed (non-Linux, permission denied) — return null
        }

        return found ? new ActivitySample(totalCpuTicks, totalSockets) : null;
    }

    /// <summary>
    /// Walks the parent-pid chain from <paramref name="pid"/> up to PID 1.
    /// Returns true if <paramref name="ancestorPid"/> appears in that chain
    /// (or equals <paramref name="pid"/>). Capped at <see cref="MaxAncestorWalk"/>
    /// hops as a defensive measure.
    /// </summary>
    private static bool IsDescendantOf(int pid, int ancestorPid)
    {
        var current = pid;
        for (var i = 0; i < MaxAncestorWalk; i++)
        {
            if (current == ancestorPid)
                return true;
            if (current <= 1)
                return false;

            string stat;
            try { stat = File.ReadAllText($"/proc/{current}/stat"); }
            catch { return false; }

            if (!TryParseStat(stat, out _, out var ppid))
                return false;
            if (ppid == current)
                return false; // shouldn't happen, but break the loop just in case
            current = ppid;
        }
        return false;
    }

    /// <summary>
    /// Parses ppid + utime+stime from /proc/&lt;pid&gt;/stat. The comm field
    /// can contain spaces and parentheses, so we anchor at the last ')' to
    /// find the start of the fixed-layout suffix.
    /// Fields after ')': state ppid pgrp session tty_nr tpgid flags
    ///   minflt cminflt majflt cmajflt utime stime ...
    /// ppid is at offset 1; utime at 11; stime at 12 (0-based) in the suffix.
    /// </summary>
    private static bool TryParseStat(string stat, out long ticks, out int ppid)
    {
        ticks = 0;
        ppid = 0;
        var close = stat.LastIndexOf(')');
        if (close < 0) return false;

        var parts = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 13) return false;
        if (!int.TryParse(parts[1], out ppid)) return false;
        if (!long.TryParse(parts[11], out var utime)) return false;
        if (!long.TryParse(parts[12], out var stime)) return false;
        ticks = utime + stime;
        return true;
    }

    /// <summary>
    /// Counts file descriptors in /proc/&lt;pid&gt;/fd that are sockets.
    /// The symlink target for a socket fd is "socket:[inode]".
    /// Returns 0 on any permission or I/O error.
    /// </summary>
    private static int CountOpenSockets(int pid)
    {
        var count = 0;
        try
        {
            foreach (var fdPath in Directory.EnumerateFiles($"/proc/{pid}/fd"))
            {
                try
                {
                    var target = new FileInfo(fdPath).LinkTarget;
                    if (target is not null &&
                        target.StartsWith("socket:", StringComparison.Ordinal))
                        count++;
                }
                catch { /* fd may have closed between enumeration and stat */ }
            }
        }
        catch { /* /proc/<pid>/fd not accessible */ }
        return count;
    }
}
