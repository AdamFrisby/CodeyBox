namespace CodeyBox.Core;

/// <summary>
/// Work-item attachment foundation configuration. Bound from
/// <c>CodeyBox:Attachments</c>. Hot-reloadable: the orchestrator reads the
/// root directory, limits, and TTL on every upload, terminal-state sweep,
/// and orphan scan.
/// </summary>
public sealed class AttachmentsOptions
{
    /// <summary>
    /// Content-addressed blob root on the host filesystem. Default
    /// <c>~/.codeybox/attachments</c>. Tilde-expanded at resolve time.
    /// Operators should keep this off any executable PATH.
    /// </summary>
    public string RootDirectory { get; set; } = "~/.codeybox/attachments";

    /// <summary>
    /// Per-file maximum size in bytes. Streaming upload rejects the file
    /// the moment this threshold is crossed (no full-file buffering).
    /// Default 100 MiB.
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>
    /// Maximum number of attachments a single work item may carry.
    /// Default 32.
    /// </summary>
    public int MaxAttachmentsPerWorkItem { get; set; } = 32;

    /// <summary>
    /// Maximum sum-of-bytes across a single work item's attachments.
    /// Default 512 MiB.
    /// </summary>
    public long MaxTotalBytesPerWorkItem { get; set; } = 512L * 1024 * 1024;

    /// <summary>
    /// How long after a work item reaches a terminal state its
    /// attachments are kept before the cleanup sweep removes them.
    /// Default 7 days. Set to <see cref="TimeSpan.Zero"/> to sweep terminal
    /// items on the next pass.
    /// </summary>
    public TimeSpan TerminalCleanupTtl { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Period between terminal-state cleanup sweeps. Default 1 hour.</summary>
    public TimeSpan CleanupSweepInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Period between orphan-blob sweeps (blobs on disk with no metadata row).
    /// Default 6 hours.
    /// </summary>
    public TimeSpan OrphanSweepInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Grace window before an unreferenced blob is treated as orphaned.
    /// Protects against a race where a blob has been staged on disk but
    /// the metadata row has not been written yet. Default 10 minutes.
    /// </summary>
    public TimeSpan OrphanGracePeriod { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Expands a leading <c>~/</c> to the user's home directory.
    /// </summary>
    public static string ResolveRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("Attachments root directory is not configured.");
        if (root.StartsWith("~/", StringComparison.Ordinal) || root == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return root.Length == 1
                ? home
                : Path.Combine(home, root[2..]);
        }
        return root;
    }
}
