using System.Security.Cryptography;

namespace CodeyBox.Core;

/// <summary>
/// Validated, immutable bytes captured from an interrupted agent's private CLI
/// scratchpad. The hard cap bounds both SQLite storage and every managed-memory
/// allocation made while saving or restoring the archive.
/// </summary>
public sealed class AgentTurnScratchpadArchive
{
    /// <summary>Private in-sandbox directory used for capture and restore staging.</summary>
    public const string GuestDirectory = "/run/codeybox/agent-turn";

    /// <summary>Atomically-published archive path inside <see cref="GuestDirectory"/>.</summary>
    public const string GuestArchivePath = GuestDirectory + "/scratchpad.tgz";

    /// <summary>Historical repository directory used before private archive storage.</summary>
    public const string LegacyRepositoryDirectory = ".codeybox";

    /// <summary>Reserved prefix for historical capture artifacts.</summary>
    public const string LegacyCapturePrefix = LegacyRepositoryDirectory + "/preempt-scratchpad";

    /// <summary>Reserved prefix for historical restore staging artifacts.</summary>
    public const string LegacyRestorePrefix = LegacyRepositoryDirectory + "/resume-scratchpad";

    /// <summary>Historical archive basename accepted only by the bounded migration path.</summary>
    public const string LegacyArchiveFileName = "preempt-scratchpad.tgz";

    /// <summary>Historical human-readable manifest basename removed during migration.</summary>
    public const string LegacyManifestFileName = "preempt-scratchpad.md";

    /// <summary>Maximum persisted archive size (32 MiB).</summary>
    public const int MaximumBytes = 32 * 1024 * 1024;

    /// <summary>Maximum uncompressed tar stream accepted during validation (32 MiB).</summary>
    public const int MaximumExpandedBytes = 32 * 1024 * 1024;

    /// <summary>Maximum bytes restored from one captured regular file (2 MiB).</summary>
    public const int MaximumFileBytes = 2 * 1024 * 1024;

    /// <summary>Maximum cumulative regular-file payload captured or restored (25 MiB).</summary>
    public const int MaximumContentBytes = 25 * 1024 * 1024;

    /// <summary>Maximum archive/manifest entry count.</summary>
    public const int MaximumEntries = 2_000;

    /// <summary>Maximum path depth below a provider scratchpad scope.</summary>
    public const int MaximumPathDepth = 16;

    /// <summary>Maximum bytes in the restore manifest.</summary>
    public const int MaximumManifestBytes = 256 * 1024;

    private readonly byte[] _content;

    public AgentTurnScratchpadArchive(byte[] content)
        : this((ReadOnlyMemory<byte>)(content ?? throw new ArgumentNullException(nameof(content))))
    {
    }

    public AgentTurnScratchpadArchive(ReadOnlyMemory<byte> content)
    {
        if (content.IsEmpty)
            throw new ArgumentException("Agent-turn scratchpad archive must be non-empty.", nameof(content));
        if (content.Length > MaximumBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(content),
                content.Length,
                $"Agent-turn scratchpad archive must not exceed {MaximumBytes} bytes.");
        }

        _content = content.ToArray();
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(_content));
    }

    /// <summary>Archive size in bytes.</summary>
    public int SizeBytes => _content.Length;

    /// <summary>Canonical lowercase SHA-256 of the archive bytes.</summary>
    public string Sha256 { get; }

    /// <summary>
    /// Returns a defensive copy. Callers cannot mutate the value retained by this
    /// instance or by a store operation already in progress.
    /// </summary>
    public byte[] ToArray() => (byte[])_content.Clone();
}
