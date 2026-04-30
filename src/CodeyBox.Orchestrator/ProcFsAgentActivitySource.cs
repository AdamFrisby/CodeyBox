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

    public ActivitySample? TryRead()
    {
        if (!OperatingSystem.IsLinux())
            return null;

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

                // Read CPU ticks
                string stat;
                try { stat = File.ReadAllText(Path.Combine(procDir, "stat")); }
                catch { continue; }

                if (!TryParseCpuTicks(stat, out var cpuTicks))
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
    /// Parses utime+stime from /proc/&lt;pid&gt;/stat. The comm field can
    /// contain spaces and parentheses, so we anchor at the last ')' to find
    /// the start of the fixed-layout suffix.
    /// Fields after ')': state ppid pgrp session tty_nr tpgid flags
    ///   minflt cminflt majflt cmajflt utime stime ...
    /// utime is at offset 11 and stime at offset 12 (0-based) in the suffix.
    /// </summary>
    private static bool TryParseCpuTicks(string stat, out long ticks)
    {
        ticks = 0;
        var close = stat.LastIndexOf(')');
        if (close < 0) return false;

        var parts = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // parts[0]=state, [1]=ppid, [2]=pgrp, [3]=session, [4]=tty_nr,
        // [5]=tpgid, [6]=flags, [7]=minflt, [8]=cminflt, [9]=majflt,
        // [10]=cmajflt, [11]=utime, [12]=stime
        if (parts.Length < 13) return false;
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
