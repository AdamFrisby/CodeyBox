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
    /// Maximum UTF-16 code units accepted for one attachment caption.
    /// Default 2,000.
    /// </summary>
    public int MaxCaptionChars { get; set; } = 2_000;

    /// <summary>
    /// Maximum UTF-16 code units accepted for a sanitized attachment display
    /// filename. Default 255.
    /// </summary>
    public int MaxFileNameChars { get; set; } = 255;

    /// <summary>
    /// Maximum characters accepted for a multipart file section Content-Type
    /// header before it is parsed and stored. Default 255.
    /// </summary>
    public int MaxContentTypeChars { get; set; } = 255;

    /// <summary>
    /// Maximum number of headers accepted on one multipart section.
    /// Default 256.
    /// </summary>
    public int MultipartHeadersCountLimit { get; set; } = 256;

    /// <summary>
    /// Maximum aggregate header bytes accepted on one multipart section.
    /// Default 8 KiB.
    /// </summary>
    public int MultipartHeadersLengthLimitBytes { get; set; } = 8 * 1024;

    /// <summary>
    /// Maximum characters returned from multipart parser exception text in
    /// client-facing 400 responses. Default 240.
    /// </summary>
    public int MaxMultipartErrorMessageChars { get; set; } = 240;

    /// <summary>
    /// When true, a work item's attachments are staged into its sandbox VM and
    /// announced to the agent (via the attachment manifest injected into the
    /// prompt) for the phases listed in <see cref="DeliverToPhases"/>. When
    /// false, attachments stay host-only (upload/download API) and no bytes are
    /// staged into any sandbox — behaviour identical to the storage-only
    /// foundation. Default true.
    /// </summary>
    public bool DeliverToSandbox { get; set; } = true;

    /// <summary>
    /// Agent prompt phases whose invocations stage attachments into the sandbox
    /// and inject the attachment manifest. Compared case-insensitively against
    /// the phase value (<c>work</c>, <c>rework</c>, <c>audit</c>, …). A phase
    /// not listed behaves as if the item had no attachments (nothing staged,
    /// no manifest). Default: work, rework, audit.
    /// </summary>
    public IReadOnlyList<string> DeliverToPhases { get; set; } = ["work", "rework", "audit"];

    /// <summary>
    /// TTL for attachments on non-terminal work items whose owning item has
    /// not been updated recently. Terminal work items are eligible on the
    /// next cleanup sweep regardless of this value. Default 7 days.
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

    public void Validate()
    {
        if (MaxFileSizeBytes <= 0)
            throw new InvalidOperationException("CodeyBox:Attachments:MaxFileSizeBytes must be positive");
        if (MaxAttachmentsPerWorkItem <= 0)
            throw new InvalidOperationException("CodeyBox:Attachments:MaxAttachmentsPerWorkItem must be positive");
        if (MaxTotalBytesPerWorkItem <= 0)
            throw new InvalidOperationException("CodeyBox:Attachments:MaxTotalBytesPerWorkItem must be positive");
        if (MaxCaptionChars <= 0)
            throw new InvalidOperationException("CodeyBox:Attachments:MaxCaptionChars must be positive");
        if (MaxFileNameChars <= 0)
            throw new InvalidOperationException("CodeyBox:Attachments:MaxFileNameChars must be positive");
        if (MaxContentTypeChars <= 0)
            throw new InvalidOperationException("CodeyBox:Attachments:MaxContentTypeChars must be positive");
        if (MultipartHeadersCountLimit <= 0)
            throw new InvalidOperationException("CodeyBox:Attachments:MultipartHeadersCountLimit must be positive");
        if (MultipartHeadersLengthLimitBytes <= 0)
            throw new InvalidOperationException("CodeyBox:Attachments:MultipartHeadersLengthLimitBytes must be positive");
        if (MaxMultipartErrorMessageChars <= 0)
            throw new InvalidOperationException("CodeyBox:Attachments:MaxMultipartErrorMessageChars must be positive");
        if (DeliverToPhases is null)
            throw new InvalidOperationException("CodeyBox:Attachments:DeliverToPhases must not be null");
        if (TerminalCleanupTtl < TimeSpan.Zero)
            throw new InvalidOperationException("CodeyBox:Attachments:TerminalCleanupTtl must be non-negative");
        if (CleanupSweepInterval < TimeSpan.Zero)
            throw new InvalidOperationException("CodeyBox:Attachments:CleanupSweepInterval must be non-negative");
        if (OrphanSweepInterval < TimeSpan.Zero)
            throw new InvalidOperationException("CodeyBox:Attachments:OrphanSweepInterval must be non-negative");
        if (OrphanGracePeriod < TimeSpan.Zero)
            throw new InvalidOperationException("CodeyBox:Attachments:OrphanGracePeriod must be non-negative");
    }

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
