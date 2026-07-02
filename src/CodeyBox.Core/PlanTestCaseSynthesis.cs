using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeyBox.Core;

/// <summary>
/// Turns the free-text <see cref="PlanArtifactDocument.TestStrategy"/> entries of
/// an approved plan into stable, classified <see cref="TestCase"/> identities.
///
/// The plan schema declares its test intentions as an ordered list of prose
/// strings (one per scenario the plan commits to), not as structured records,
/// so the automation kind is inferred here from lexical markers. The derived id
/// is deterministic in <c>(workItemId, ordinal)</c> so a later plan-rework can
/// reconcile the same scenarios in place instead of duplicating them.
/// </summary>
public static partial class PlanTestCaseSynthesizer
{
    private const int MaxNameChars = 120;

    // Ordered most-specific-first: an entry mentioning both "integration" and
    // "unit" is classified by the broader scope it commits to.
    [GeneratedRegex(@"\b(e2e|end[\s-]?to[\s-]?end|replay|playwright|cypress|selenium|webdriver|browser)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex E2eMarker();

    [GeneratedRegex(@"\bintegration\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntegrationMarker();

    [GeneratedRegex(@"\bunit\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnitMarker();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>
    /// Classifies a single plan test-strategy entry. Falls back to
    /// <see cref="AutomationKind.Manual"/> when no automation marker is present —
    /// an intention we cannot yet map to an automated harness is a described
    /// (manual) check rather than a guessed unit test.
    /// </summary>
    public static AutomationKind ClassifyAutomationKind(string scenario)
    {
        if (string.IsNullOrWhiteSpace(scenario))
            return AutomationKind.Manual;
        if (E2eMarker().IsMatch(scenario))
            return AutomationKind.E2eReplay;
        if (IntegrationMarker().IsMatch(scenario))
            return AutomationKind.Integration;
        if (UnitMarker().IsMatch(scenario))
            return AutomationKind.Unit;
        return AutomationKind.Manual;
    }

    /// <summary>
    /// Deterministic test-case id for the scenario at <paramref name="ordinal"/>
    /// of the plan attached to <paramref name="workItemId"/>. Stable across
    /// plan-rework so the same scenario reconciles in place; namespaced so it
    /// cannot collide with a randomly-generated id from the manual create API.
    /// </summary>
    public static string DeriveId(WorkItemId workItemId, int ordinal)
    {
        var seed = $"codeybox/plan-test-case/{workItemId}/{ordinal}";
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(seed), hash);
        return new Guid(hash[..16]).ToString("N");
    }

    /// <summary>Collapses a scenario entry to a single-line, length-bounded name.</summary>
    public static string BuildName(string scenario)
    {
        var oneLine = Whitespace().Replace(scenario ?? string.Empty, " ").Trim();
        return oneLine.Length <= MaxNameChars ? oneLine : oneLine[..MaxNameChars];
    }
}

/// <summary>Outcome of a single plan test-case reconcile pass.</summary>
public readonly record struct PlanTestCaseReconcileResult(int Created, int Updated, int Removed)
{
    public int Total => Created + Updated + Removed;
}

/// <summary>
/// Materialises the test intentions declared by an approved plan into
/// <see cref="TestCase"/> artifacts linked to the source work item, and keeps
/// them reconciled across plan-rework.
///
/// Idempotency comes from the deterministic per-ordinal id
/// (<see cref="PlanTestCaseSynthesizer.DeriveId"/>): re-running against the same
/// plan updates in place (never duplicates), a shorter plan prunes the removed
/// tail, and a longer plan appends. Manually-authored cases (random ids) and any
/// committed replay / conformance / run history an authoring or execution item
/// later fills in are left untouched.
/// </summary>
public sealed class PlanTestCaseReconciler
{
    private readonly ITestCaseStore _store;

    public PlanTestCaseReconciler(ITestCaseStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<PlanTestCaseReconcileResult> ReconcileAsync(
        WorkItemId workItemId,
        string planArtifact,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var plan = PlanArtifactDocument.ParseCanonical(planArtifact);
        var scenarios = plan.TestStrategy;
        var sourceWorkItemId = workItemId.ToString();
        var created = 0;
        var updated = 0;
        var removed = 0;

        for (var i = 0; i < scenarios.Count; i++)
        {
            var id = PlanTestCaseSynthesizer.DeriveId(workItemId, i);
            var name = PlanTestCaseSynthesizer.BuildName(scenarios[i]);
            var description = scenarios[i];
            var kind = PlanTestCaseSynthesizer.ClassifyAutomationKind(scenarios[i]);

            var existing = await _store.GetAsync(id, ct).ConfigureAwait(false);
            if (existing is null)
            {
                await _store.CreateAsync(
                    new TestCase
                    {
                        Id = id,
                        Name = name,
                        Description = description,
                        SourceWorkItemId = sourceWorkItemId,
                        CreatedAt = now,
                        UpdatedAt = now,
                        AutomationKind = kind,
                        // ExecutableArtifactJson intentionally left null: e2e-replay
                        // cases are emitted WITHOUT a committed replay; the separate
                        // authoring orchestration fills it in later.
                    },
                    ct).ConfigureAwait(false);
                created++;
                continue;
            }

            if (IsInSync(existing, name, description, kind))
                continue;

            var reconciled = existing with
            {
                Name = name,
                Description = description,
                AutomationKind = kind,
                UpdatedAt = now,
                // Everything else — CreatedAt, ExecutableArtifactJson, ConformanceJson,
                // LastRun*, Label, IsArchived — is preserved so reconcile never
                // clobbers a committed replay or execution history.
            };
            if (await _store.UpdateAsync(reconciled, ct).ConfigureAwait(false))
                updated++;
        }

        // Prune scenarios dropped since the previous plan revision. Emitted ids are
        // contiguous from ordinal 0 and capped at the plan's own list limit, so a
        // bounded sweep past the new count removes exactly the orphaned tail.
        for (var i = scenarios.Count; i < PlanArtifactDocument.MaxListItems; i++)
        {
            var id = PlanTestCaseSynthesizer.DeriveId(workItemId, i);
            if (await _store.DeleteAsync(id, ct).ConfigureAwait(false))
                removed++;
        }

        return new PlanTestCaseReconcileResult(created, updated, removed);
    }

    private static bool IsInSync(TestCase existing, string name, string description, AutomationKind kind)
        => string.Equals(existing.Name, name, StringComparison.Ordinal)
        && string.Equals(existing.Description, description, StringComparison.Ordinal)
        && existing.AutomationKind == kind;
}
