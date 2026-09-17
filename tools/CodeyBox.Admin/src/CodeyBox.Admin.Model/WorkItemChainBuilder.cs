namespace CodeyBox.Admin.Model;

/// <summary>
/// Shared defaults applied to every item filed from the composer.
/// Null means unset — the server default applies.
/// </summary>
public sealed record ComposerDefaults(
    string ProjectId,
    string? Agent = null,
    string? AgentClassId = null,
    string? BaseBranch = null,
    string? WorkBranch = null,
    bool PushUpstream = true,
    int? Priority = null,
    int? MinModelScore = null,
    IReadOnlyList<string>? RequiredCapabilities = null,
    int? AuditMaxIterations = null,
    string? AuditComplexity = null,
    string? AuditorProfile = null,
    IReadOnlyDictionary<string, string>? Knobs = null,
    string? ReleaseId = null);

/// <summary>
/// One chain item as reviewed in the preview: title and body as shown,
/// plus 1-based local numbers of the siblings it waits for.
/// </summary>
public sealed record ChainItemDraft(
    string Title,
    string Body,
    IReadOnlyList<int> DependsOnLocal,
    string? ExternalId = null);

/// <summary>
/// A single create fully resolved and ready to send: every field the
/// create surface accepts, with sibling edges expressed as external-id
/// references the orchestrator resolves at create time. What the preview
/// shows is exactly what this carries — the composer must submit these
/// unchanged.
/// </summary>
public sealed record PlannedCreate(
    string ProjectId,
    string Title,
    string Prompt,
    string? ExternalId,
    string? Agent,
    string? AgentClassId,
    string? BaseBranch,
    string? WorkBranch,
    bool PushUpstream,
    int? Priority,
    int? MinModelScore,
    IReadOnlyList<string>? RequiredCapabilities,
    int? AuditMaxIterations,
    string? AuditComplexity,
    string? AuditorProfile,
    IReadOnlyDictionary<string, string>? Knobs,
    string? ReleaseId,
    IReadOnlyList<string> DependsOn);

/// <summary>
/// Turns a reviewed chain preview into the ordered creates that file it:
/// one entry per item, in author order, where later items name earlier
/// siblings by locally generated external ids. The orchestrator resolves
/// those edges at create time, so filing needs no read round-trips — just
/// these creates, in order. Single items keep the operator's own external
/// id; chains stamp every member so siblings can find each other.
///
/// Pure over its inputs; <paramref name="chainKey"/> is caller-supplied
/// (8 lowercase hex chars) so results are deterministic and testable.
/// </summary>
public static class WorkItemChainBuilder
{
    /// <summary>Prefix for locally generated chain external ids.</summary>
    public const string ChainExternalIdPrefix = "cb-chain-";

    /// <summary>
    /// Builds the ordered creates for <paramref name="drafts"/> under
    /// <paramref name="defaults"/>. Local dependency numbers are 1-based
    /// positions in <paramref name="drafts"/>; out-of-range and self
    /// references are dropped. Throws <see cref="ArgumentException"/> on
    /// empty drafts or a malformed chain key.
    /// </summary>
    public static IReadOnlyList<PlannedCreate> Build(
        ComposerDefaults defaults,
        IReadOnlyList<ChainItemDraft> drafts,
        string chainKey)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(drafts);
        if (drafts.Count == 0)
        {
            throw new ArgumentException("At least one draft is required.", nameof(drafts));
        }

        var key = NormaliseChainKey(chainKey);
        var isChain = drafts.Count > 1;
        var result = new List<PlannedCreate>(drafts.Count);

        for (var i = 0; i < drafts.Count; i++)
        {
            var draft = drafts[i];
            var externalId = isChain
                ? ChainExternalId(key, i + 1)
                : BlankToNull(draft.ExternalId);

            var dependsOn = draft.DependsOnLocal
                .Where(d => d >= 1 && d <= drafts.Count && d != i + 1)
                .Distinct()
                .OrderBy(d => d)
                .Select(d => ResolveDependencyRef(key, isChain, drafts[d - 1], d))
                .ToList();

            result.Add(new PlannedCreate(
                defaults.ProjectId,
                draft.Title.Trim(),
                draft.Body,
                externalId,
                defaults.Agent,
                defaults.AgentClassId,
                defaults.BaseBranch,
                defaults.WorkBranch,
                defaults.PushUpstream,
                defaults.Priority,
                defaults.MinModelScore,
                defaults.RequiredCapabilities,
                defaults.AuditMaxIterations,
                defaults.AuditComplexity,
                defaults.AuditorProfile,
                defaults.Knobs,
                defaults.ReleaseId,
                dependsOn));
        }

        return result;
    }

    /// <summary>
    /// The locally generated external id for item <paramref name="number"/>
    /// (1-based) of chain <paramref name="chainKey"/>. ASCII, no
    /// whitespace, never a UUID — safe for the external-id surface.
    /// </summary>
    public static string ChainExternalId(string chainKey, int number) =>
        $"{ChainExternalIdPrefix}{NormaliseChainKey(chainKey)}-{number:00}";

    private static string ResolveDependencyRef(
        string key, bool isChain, ChainItemDraft target, int number)
    {
        if (isChain)
        {
            return ChainExternalId(key, number);
        }

        return BlankToNull(target.ExternalId) ?? ChainExternalId(key, number);
    }

    private static string NormaliseChainKey(string chainKey)
    {
        if (string.IsNullOrWhiteSpace(chainKey))
        {
            throw new ArgumentException("A chain key is required.", nameof(chainKey));
        }

        var key = chainKey.Trim().ToLowerInvariant();
        if (key.Length is < 4 or > 32 || !key.All(c => char.IsAsciiLetterOrDigit(c)))
        {
            throw new ArgumentException(
                "The chain key must be 4–32 ASCII letters or digits.", nameof(chainKey));
        }

        return key;
    }

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
