using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

public sealed class AgentStreamsOptions
{
    public bool Enabled { get; set; } = true;
    public string Path { get; set; } = "logs/agents";

    /// <summary>Per-file size cap in MB. Stream is truncated with a marker line if exceeded.</summary>
    public int MaxFileSizeMb { get; set; } = 32;

    /// <summary>Days to retain. Daily sweep deletes older files. 0 = keep forever.</summary>
    public int RetainedDays { get; set; } = 14;

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
            "Agent stream capture enabled at {Path}; maxFileSizeMb={MaxFileSizeMb}, retainedDays={RetainedDays}",
            opts.Path, opts.MaxFileSizeMb, opts.RetainedDays);
    }
}
