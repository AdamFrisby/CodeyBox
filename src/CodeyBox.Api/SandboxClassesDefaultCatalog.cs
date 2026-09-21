using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Api;

/// <summary>
/// Synthesizes the default sandbox-class catalog for operators who have not
/// configured any <c>CodeyBox:SandboxClasses</c>: one class with one member
/// backed by the legacy <c>CodeyBox:SandboxProvider</c> setting, so placement
/// has a member to select without requiring new configuration.
/// </summary>
public static class SandboxClassesDefaultCatalog
{
    /// <summary>Class id of the synthesized default class.</summary>
    public const string DefaultClassId = "default";

    /// <summary>Member id of the synthesized default member.</summary>
    public const string DefaultMemberId = "default";

    /// <summary>
    /// Builds the single-class catalog for <paramref name="providerKind"/>
    /// (already normalised, lowercase). The member accepts every network
    /// profile and credential — like the single provider it replaces — and
    /// declares exactly the provider's own
    /// <see cref="ISandboxProvider.DeclaredCapabilities"/>, so work needing
    /// an operation the provider implements keeps placing while work needing
    /// one it does not is refused as unplaceable naming the operation.
    /// Capacity is <paramref name="capacity"/>: callers pass the resolved
    /// <c>WorkerPool:MaxConcurrentSandboxes</c> ceiling so the single member's
    /// admission gate behaves identically to the former process-wide gate.
    /// </summary>
    public static IReadOnlyList<SandboxClass> Synthesize(
        string providerKind,
        IReadOnlyList<string> providerCapabilities,
        ILogger log,
        int capacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKind);
        ArgumentNullException.ThrowIfNull(providerCapabilities);
        ArgumentNullException.ThrowIfNull(log);
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                capacity,
                "Default sandbox member capacity must be >= 1.");
        var kind = providerKind.Trim().ToLowerInvariant();

        var capabilities = providerCapabilities
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .Select(static tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        log.LogInformation(
            "No CodeyBox:SandboxClasses configured; synthesized default single-member class from CodeyBox:SandboxProvider '{Kind}'.",
            kind);

        return
        [
            new SandboxClass
            {
                Id = DefaultClassId,
                DisplayName = $"Default (CodeyBox:SandboxProvider={kind})",
                Members =
                [
                    new SandboxMember
                    {
                        MemberId = DefaultMemberId,
                        ProviderKind = kind,
                        Capacity = capacity,
                        Capabilities = capabilities,
                        // The default member IS the orchestrator host, so it
                        // holds whatever credentials the operator gave the
                        // process. Declared as the "*" wildcard rather than
                        // left empty: an empty list means "holds nothing" to
                        // ExecutorEligibility.HoldsCredential, which would
                        // exclude this member from every credential-bearing
                        // phase and leave nothing placeable.
                        Credentials = ["*"],
                        PreferenceScore = 100,
                    },
                ],
            },
        ];
    }
}
