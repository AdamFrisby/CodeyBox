using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Api;

/// <summary>Config binding for one sandbox class.</summary>
public sealed class SandboxClassOptions
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<SandboxMemberOptions> Members { get; set; } = [];
}

/// <summary>Config binding for one member of a sandbox class.</summary>
public sealed class SandboxMemberOptions
{
    /// <summary>Stable member id, unique within the containing class.</summary>
    public string MemberId { get; set; } = string.Empty;

    /// <summary>
    /// Sandbox provider kind backing this member, e.g. "incus", "multipass".
    /// Must name a registered provider kind; anything else fails closed at load.
    /// </summary>
    public string ProviderKind { get; set; } = string.Empty;

    /// <summary>
    /// Optional host id disambiguating members that share a provider kind.
    /// Null means the provider's default host.
    /// </summary>
    public string? HostId { get; set; }

    /// <summary>
    /// Maximum concurrent sandboxes this member may hold. Must be positive,
    /// and at least twice the worker count (see
    /// <see cref="SandboxClassesConfigBuilder.Build"/>).
    /// </summary>
    public int? Capacity { get; set; }

    /// <summary>
    /// Clearance/capability tags this member is trusted to handle.
    /// Default empty.
    /// </summary>
    public List<string> Capabilities { get; set; } = [];

    /// <summary>
    /// Logical network profiles this member accepts. Empty accepts every profile.
    /// </summary>
    public List<string> NetworkProfiles { get; set; } = [];

    /// <summary>Names of the agent credential sets this member holds.</summary>
    public List<string> Credentials { get; set; } = [];

    /// <summary>
    /// Operator-curated preference score (0–200). Required; no silent default.
    /// Higher wins; ties spill deterministically.
    /// </summary>
    public int? PreferenceScore { get; set; }
}

/// <summary>
/// Builds (and validates) the in-memory <see cref="SandboxClass"/> catalog
/// from its JSON-bound config shape. Lives outside <c>Program.cs</c> so both
/// the startup wiring and the hot-reload coordinator rebuild from the latest
/// <see cref="CodeyBoxOptions"/> without duplicating the validation rules.
/// </summary>
public static class SandboxClassesConfigBuilder
{
    /// <summary>
    /// Validates <paramref name="options"/> and produces the
    /// <see cref="SandboxClass"/> catalog the placement path will consume.
    /// Throws <see cref="InvalidOperationException"/> on any validation
    /// failure — callers in the hot-reload path catch and keep the prior
    /// snapshot so a bad edit can't break running placements.
    /// </summary>
    /// <param name="options">Raw <c>CodeyBox:SandboxClasses</c> bindings.</param>
    /// <param name="maxConcurrentWorkers">
    /// Effective worker count the per-member deadlock invariant is checked
    /// against (resolved from <c>CodeyBox:WorkerPool</c> with the legacy
    /// <c>CodeyBox:Concurrency</c> fallback via
    /// <see cref="Orchestrator.OrchestratorOptionsFactory.ResolveSandboxCounts"/>).
    /// </param>
    /// <param name="registeredProviderKinds">
    /// Provider kinds the composition root can actually build (e.g.
    /// <see cref="SandboxProviderKinds.All"/>). A member naming anything
    /// else is refused — never silently re-pointed at another provider.
    /// </param>
    public static IReadOnlyList<SandboxClass> Build(
        List<SandboxClassOptions> options,
        int maxConcurrentWorkers,
        IReadOnlySet<string> registeredProviderKinds,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registeredProviderKinds);
        ArgumentNullException.ThrowIfNull(log);
        if (maxConcurrentWorkers < 1)
            throw new InvalidOperationException(
                $"SandboxClasses validation requires MaxConcurrentWorkers >= 1 but observed {maxConcurrentWorkers}. " +
                "Fix CodeyBox:WorkerPool:MaxConcurrentWorkers (or the legacy CodeyBox:Concurrency fallback).");

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SandboxClass>();

        foreach (var classOpts in options)
        {
            if (string.IsNullOrWhiteSpace(classOpts.Id))
                throw new InvalidOperationException("Each SandboxClass must have a non-empty Id");
            var classId = classOpts.Id.Trim();
            if (!seenIds.Add(classId))
                throw new InvalidOperationException($"SandboxClass Id '{classId}' is not unique");
            if (classOpts.Members.Count == 0)
                throw new InvalidOperationException($"SandboxClass '{classId}' must have at least one member");

            var members = new List<SandboxMember>();
            var seenMemberIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in classOpts.Members)
            {
                if (string.IsNullOrWhiteSpace(m.MemberId))
                    throw new InvalidOperationException($"SandboxClass '{classId}': member MemberId must be non-empty");
                var memberId = m.MemberId.Trim();
                if (!seenMemberIds.Add(memberId))
                    throw new InvalidOperationException(
                        $"SandboxClass '{classId}': duplicate member id '{memberId}'. " +
                        "MemberIds must be unique within a class.");

                if (string.IsNullOrWhiteSpace(m.ProviderKind))
                    throw new InvalidOperationException(
                        $"SandboxClass '{classId}': member '{memberId}' must name a sandbox provider kind");
                var providerKind = m.ProviderKind.Trim().ToLowerInvariant();
                if (!registeredProviderKinds.Contains(providerKind))
                    throw new InvalidOperationException(
                        $"SandboxClass '{classId}': member '{memberId}' references unregistered sandbox provider " +
                        $"'{m.ProviderKind.Trim()}'. Registered providers: {FormatRegistered(registeredProviderKinds)}. " +
                        "Add the provider or remove the member reference. " +
                        "The member is NOT silently falling back to another provider.");

                if (m.Capacity is not { } capacity)
                    throw new InvalidOperationException(
                        $"SandboxClass '{classId}': member '{memberId}' is missing Capacity. " +
                        "Add Capacity=N (a positive integer at least twice the worker count).");
                if (capacity <= 0)
                    throw new InvalidOperationException(
                        $"SandboxClass '{classId}': member '{memberId}' has Capacity={capacity} " +
                        "which must be > 0.");

                // A worker holds its work sandbox while acquiring a second
                // sandbox for audit, so a member whose capacity is less than
                // twice the number of workers that can target it can deadlock
                // the pipeline: every permit held by a worker waiting for its
                // second sandbox, none available, no progress — and deleting a
                // sandbox does not release the permit, only a restart does.
                var minimumCapacity = checked(2 * maxConcurrentWorkers);
                if (capacity < minimumCapacity)
                    throw new InvalidOperationException(
                        $"SandboxClass '{classId}': member '{memberId}' has Capacity={capacity} " +
                        $"which is below the deadlock-safe minimum {minimumCapacity} for " +
                        $"{maxConcurrentWorkers} worker(s): a worker holds its work sandbox while acquiring " +
                        "a second sandbox for audit, so each member needs Capacity >= 2x the worker count. " +
                        $"Raise Capacity to at least {minimumCapacity} or lower MaxConcurrentWorkers.");

                if (m.PreferenceScore is not { } score)
                    throw new InvalidOperationException(
                        $"SandboxClass '{classId}': member '{memberId}' is missing PreferenceScore. " +
                        "Add PreferenceScore=N (0–200); higher wins, ties spill deterministically.");
                if (score is < 0 or > 200)
                    throw new InvalidOperationException(
                        $"SandboxClass '{classId}': member '{memberId}' has PreferenceScore={score} " +
                        "which is outside the valid range 0–200.");

                members.Add(new SandboxMember
                {
                    MemberId = memberId,
                    ProviderKind = providerKind,
                    HostId = string.IsNullOrWhiteSpace(m.HostId) ? null : m.HostId.Trim(),
                    Capacity = capacity,
                    Capabilities = NormalizeTags(m.Capabilities, StringComparer.OrdinalIgnoreCase),
                    NetworkProfiles = NormalizeTags(m.NetworkProfiles, StringComparer.Ordinal),
                    Credentials = NormalizeTags(m.Credentials, StringComparer.Ordinal),
                    PreferenceScore = score,
                });
            }

            log.LogInformation(
                "SandboxClass '{ClassId}' resolved members: [{Members}]",
                classId,
                string.Join(", ", members.Select(m => $"{m.MemberId}/{m.ProviderKind}(cap={m.Capacity},score={m.PreferenceScore})")));

            result.Add(new SandboxClass
            {
                Id = classId,
                DisplayName = string.IsNullOrWhiteSpace(classOpts.DisplayName)
                    ? classId
                    : classOpts.DisplayName.Trim(),
                Members = members,
            });
        }

        return result;
    }

    private static IReadOnlyList<string> NormalizeTags(List<string> raw, StringComparer comparer)
    {
        var tags = new List<string>();
        var seen = new HashSet<string>(comparer);
        foreach (var entry in raw)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            var tag = entry.Trim();
            if (seen.Add(tag))
                tags.Add(tag);
        }
        return tags;
    }

    private static string FormatRegistered(IReadOnlySet<string> registered) =>
        registered.Count == 0 ? "(none)" : string.Join(", ", registered.OrderBy(static s => s, StringComparer.Ordinal));
}
