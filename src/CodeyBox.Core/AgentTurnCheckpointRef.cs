namespace CodeyBox.Core;

/// <summary>
/// Immutable reference binding one work item, the source Git commit containing
/// its dirty tree, and the SHA-256 of its host-private CLI scratchpad archive.
/// </summary>
public sealed record AgentTurnCheckpointRef
{
    public const string Prefix = "refs/heads/codeybox/preempt/";
    private const int WorkItemIdLength = 32;
    private const int Sha256Length = 64;
    private const int Sha1Length = 40;

    private AgentTurnCheckpointRef(
        WorkItemId workItemId,
        string sourceCommitSha,
        string archiveSha256)
    {
        WorkItemId = workItemId;
        SourceCommitSha = sourceCommitSha;
        ArchiveSha256 = archiveSha256;
        Value = $"{Prefix}{workItemId}/{sourceCommitSha}-{archiveSha256}";
    }

    public WorkItemId WorkItemId { get; }
    public string SourceCommitSha { get; }
    public string ArchiveSha256 { get; }
    public string Value { get; }

    /// <summary>Builds the one canonical ref shape accepted by durable turn recovery.</summary>
    public static AgentTurnCheckpointRef Create(
        WorkItemId workItemId,
        string sourceCommitSha,
        AgentTurnScratchpadArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ValidateWorkItemId(workItemId);
        var canonicalCommit = ValidateAndCanonicalizeCommitSha(sourceCommitSha);
        return new AgentTurnCheckpointRef(workItemId, canonicalCommit, archive.Sha256);
    }

    /// <summary>Parses and validates an already-persisted canonical checkpoint ref.</summary>
    public static AgentTurnCheckpointRef Parse(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
            throw new FormatException("Agent-turn checkpoint ref has an invalid prefix.");

        var payload = value.AsSpan(Prefix.Length);
        if (payload.Length <= WorkItemIdLength || payload[WorkItemIdLength] != '/')
            throw new FormatException("Agent-turn checkpoint ref has an invalid work-item segment.");

        var workItemText = payload[..WorkItemIdLength];
        if (!Guid.TryParseExact(workItemText, "N", out var workItemGuid)
            || workItemGuid == Guid.Empty
            || !IsLowerHex(workItemText))
        {
            throw new FormatException("Agent-turn checkpoint ref has a non-canonical work-item id.");
        }

        var binding = payload[(WorkItemIdLength + 1)..];
        if (binding.Length <= Sha256Length || binding[^(Sha256Length + 1)] != '-')
            throw new FormatException("Agent-turn checkpoint ref has an invalid content binding.");

        var commit = binding[..^(Sha256Length + 1)];
        var archiveHash = binding[^Sha256Length..];
        if (commit.Length is not (Sha1Length or Sha256Length) || !IsLowerHex(commit))
            throw new FormatException("Agent-turn checkpoint ref has an invalid source commit SHA.");
        if (!IsLowerHex(archiveHash))
            throw new FormatException("Agent-turn checkpoint ref has an invalid archive SHA-256.");

        var parsed = new AgentTurnCheckpointRef(
            new WorkItemId(workItemGuid),
            commit.ToString(),
            archiveHash.ToString());
        if (!string.Equals(parsed.Value, value, StringComparison.Ordinal))
            throw new FormatException("Agent-turn checkpoint ref is not canonical.");
        return parsed;
    }

    public static bool TryParse(string? value, out AgentTurnCheckpointRef? checkpointRef)
    {
        if (value is null)
        {
            checkpointRef = null;
            return false;
        }

        try
        {
            checkpointRef = Parse(value);
            return true;
        }
        catch (FormatException)
        {
            checkpointRef = null;
            return false;
        }
    }

    public override string ToString() => Value;

    private static void ValidateWorkItemId(WorkItemId workItemId)
    {
        if (workItemId.Value == Guid.Empty)
            throw new ArgumentException("Checkpoint work-item id must be populated.", nameof(workItemId));
    }

    private static string ValidateAndCanonicalizeCommitSha(string sourceCommitSha)
    {
        if (string.IsNullOrEmpty(sourceCommitSha)
            || sourceCommitSha.Length is not (Sha1Length or Sha256Length)
            || !IsHex(sourceCommitSha.AsSpan()))
        {
            throw new ArgumentException(
                "Source commit SHA must be a full 40- or 64-character hexadecimal object id.",
                nameof(sourceCommitSha));
        }

        return sourceCommitSha.ToLowerInvariant();
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')
                and not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }
        return true;
    }
}
