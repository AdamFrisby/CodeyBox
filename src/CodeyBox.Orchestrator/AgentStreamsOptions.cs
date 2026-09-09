using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

public sealed class AgentStreamsOptions
{
    public bool Enabled { get; set; } = true;
    public string Path { get; set; } = "logs/agents";

    /// <summary>Per-file size cap in MB. Stream is truncated with a marker line if exceeded.</summary>
    public int MaxFileSizeMb { get; set; } = 32;

    /// <summary>
    /// Age-based retention. The daily sweep deletes files whose last-write time
    /// is older than this many days. 0 = age-based eviction disabled (keep
    /// forever). This is independent of <see cref="MaxTotalSizeMb"/>: the
    /// size-based backstop still runs when this is 0.
    /// </summary>
    public int RetainedDays { get; set; } = 14;

    /// <summary>
    /// Size-based backstop: total bytes across ALL captured stream files, in MB.
    /// When the aggregate size exceeds this cap the sweep evicts the
    /// oldest-by-last-write files first until the directory is back under the
    /// cap. Runs independently of <see cref="RetainedDays"/> so a zero or
    /// misconfigured retention window can never let the directory grow
    /// unbounded. 0 = size-based eviction disabled.
    /// </summary>
    public int MaxTotalSizeMb { get; set; } = 2048;

    public static void ValidateAtStartup(AgentStreamsOptions opts, ILogger log)
    {
        if (!opts.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(opts.Path))
            throw new InvalidOperationException("CodeyBox:AgentStreams:Path must be non-empty");
        if (opts.MaxFileSizeMb < 1)
            throw new InvalidOperationException("CodeyBox:AgentStreams:MaxFileSizeMb must be >= 1");
        if (opts.RetainedDays < 0)
            throw new InvalidOperationException("CodeyBox:AgentStreams:RetainedDays must be >= 0");
        if (opts.MaxTotalSizeMb < 0)
            throw new InvalidOperationException("CodeyBox:AgentStreams:MaxTotalSizeMb must be >= 0");

        try
        {
            Directory.CreateDirectory(opts.Path);
            var probe = System.IO.Path.Combine(opts.Path, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Agent stream directory '{opts.Path}' is not writable: {ex.Message}", ex);
        }

        log.LogInformation(
            "Agent stream capture enabled at {Path}; maxFileSizeMb={MaxFileSizeMb}, retainedDays={RetainedDays}, maxTotalSizeMb={MaxTotalSizeMb}",
            opts.Path, opts.MaxFileSizeMb, opts.RetainedDays, opts.MaxTotalSizeMb);
    }
}
