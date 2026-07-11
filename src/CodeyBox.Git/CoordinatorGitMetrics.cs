using System.Diagnostics;
using CodeyBox.Core;

namespace CodeyBox.Git;

/// <summary>
/// Single recording point for host-side git command duration on the
/// coordinator. The coordinator is the one lightweight process fanning VMs
/// out across many executor hosts, so its host-side git operations are a
/// named scaling pinch point (alongside the SQLite single-writer and
/// agent-stream capture I/O). Every code path that launches git directly —
/// not just <see cref="LocalGitHost"/>'s shared runner but the bounded
/// streaming <c>ls-tree</c> path and the pre-merge verifier's worktree
/// commands — must report through here so the bottleneck stays measured
/// rather than silently degrading.
/// </summary>
/// <remarks>
/// Records to <see cref="CodeyBoxMeters.CoordinatorGitCommandDuration"/> with
/// tags <c>operation</c> (the git subcommand, e.g. <c>ls-tree</c>,
/// <c>worktree</c>) and <c>outcome</c> (<c>success</c> | <c>exit_nonzero</c> |
/// <c>output_limit</c> | <c>canceled</c> | <c>error</c>). Duration is read
/// from the supplied stopwatch in milliseconds.
/// </remarks>
internal static class CoordinatorGitMetrics
{
    public static void Record(string operation, Stopwatch stopwatch, string outcome) =>
        CodeyBoxMeters.CoordinatorGitCommandDuration.Record(
            stopwatch.ElapsedMilliseconds,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("outcome", outcome));
}
