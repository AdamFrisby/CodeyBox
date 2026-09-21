using System.Text;

namespace CodeyBox.Admin.Model;

/// <summary>
/// Everything the composer is about to file, in one value: the shared
/// defaults, the items with their in-chain edges, and the existing items
/// the roots wait for. Built by the page from its controls; reviewed and
/// validated here, purely.
/// </summary>
public sealed record Composition(
    ComposerDefaults Defaults,
    IReadOnlyList<ChainItemDraft> Items,
    IReadOnlyList<string> ExistingDependsOn);

/// <summary>
/// The composer's last word before filing: the problems the orchestrator
/// would reject the request for (checked here so a nine-item chain fails
/// once in the review, not nine times at the API), and one sentence that
/// reads the whole composition back in words so what was inferred is
/// seen before it is committed. Limits mirror the create endpoint.
/// </summary>
public static class CompositionReview
{
    /// <summary>Title cap enforced by the create endpoint.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Priority band accepted by the create endpoint.</summary>
    public const int MinPriority = -1000;

    /// <summary>Priority band accepted by the create endpoint.</summary>
    public const int MaxPriority = 1000;

    /// <summary>Largest audit iteration budget the project surface allows.</summary>
    public const int MaxAuditIterations = 100;

    /// <summary>Model-score floor is clamped to this band server-side.</summary>
    public const int MaxModelScore = 200;

    /// <summary>
    /// Every reason the composition cannot be filed as it stands, in the
    /// order an operator would fix them. Empty means file away.
    /// </summary>
    public static IReadOnlyList<string> Validate(Composition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        var problems = new List<string>();
        var d = composition.Defaults;

        if (string.IsNullOrWhiteSpace(d.ProjectId))
        {
            problems.Add("Pick a project.");
        }

        if (composition.Items.Count == 0)
        {
            problems.Add("Write a prompt — there is nothing to file.");
        }

        for (var i = 0; i < composition.Items.Count; i++)
        {
            var item = composition.Items[i];
            var label = composition.Items.Count == 1 ? "The item" : $"Item {i + 1}";
            if (string.IsNullOrWhiteSpace(item.Title))
            {
                problems.Add($"{label} needs a title.");
            }
            else if (item.Title.Trim().Length > MaxTitleLength)
            {
                problems.Add($"{label}'s title is over {MaxTitleLength} characters.");
            }

            if (string.IsNullOrWhiteSpace(item.Body) && string.IsNullOrWhiteSpace(item.Title))
            {
                problems.Add($"{label} is empty.");
            }

            if (composition.Items.Count == 1 && ExternalIdProblem(item.ExternalId) is { } idProblem)
            {
                problems.Add($"External id {idProblem}.");
            }
        }

        var edges = composition.Items.Select(i => i.DependsOnLocal).ToList();
        var cycle = ChainEdges.CycleMembers(edges);
        if (cycle.Count > 0)
        {
            problems.Add($"Items {ChainEdges.Format(cycle)} wait for each other in a cycle — nothing would ever start.");
        }

        if (d.Priority is { } p && (p < MinPriority || p > MaxPriority))
        {
            problems.Add($"Priority must be between {MinPriority} and {MaxPriority}.");
        }

        if (d.AuditMaxIterations is { } a && (a < 1 || a > MaxAuditIterations))
        {
            problems.Add($"Audit budget must be between 1 and {MaxAuditIterations} iterations.");
        }

        if (d.MinModelScore is { } m && (m < 0 || m > MaxModelScore))
        {
            problems.Add($"Min model score must be between 0 and {MaxModelScore}.");
        }

        if (d.WorkTimeoutMinutes is { } w && w < 1)
        {
            problems.Add("Work timeout must be at least 1 minute.");
        }

        if (d.MergeTimeoutMinutes is { } mt && mt < 1)
        {
            problems.Add("Merge timeout must be at least 1 minute.");
        }

        if (d.AuditComplexity is { Length: > 64 })
        {
            problems.Add("Audit complexity must be 64 characters or fewer.");
        }

        if (d.IsRefactor && composition.Items.Count > 1)
        {
            problems.Add("A refactor is project-exclusive; file the chain as normal items or file one refactor.");
        }

        return problems;
    }

    /// <summary>
    /// Why an operator-supplied external id would be rejected, or null.
    /// Mirrors the orchestrator's rule so the composer fails once, here.
    /// </summary>
    public static string? ExternalIdProblem(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var v = id.Trim();
        if (v.Length > 256)
        {
            return "must be 256 characters or fewer";
        }

        if (v.Any(ch => ch is < '!' or > '~') || v.IndexOfAny(['/', '?', ';', '<', '=', '>']) >= 0)
        {
            return "must be printable ASCII with no whitespace, /, ?, ;, <, = or >";
        }

        if (v.StartsWith("wi-", StringComparison.OrdinalIgnoreCase))
        {
            return "must not start with “wi-” (reserved)";
        }

        return Guid.TryParse(v, out _) ? "must not look like a UUID" : null;
    }

    /// <summary>
    /// The composition read back as one sentence: what, where, who, how
    /// audited, at what priority, waiting for what. Only non-default
    /// settings are spoken; the sentence says "defaults" when everything
    /// is inherited, so silence means nothing surprising.
    /// </summary>
    public static string Describe(
        Composition composition,
        string? projectDisplayName,
        Func<string, string>? existingItemLabel = null)
    {
        ArgumentNullException.ThrowIfNull(composition);
        var d = composition.Defaults;
        var count = composition.Items.Count;
        var sb = new StringBuilder();

        sb.Append(count switch
        {
            0 => "Nothing to file yet",
            1 => d.IsRefactor ? "Files 1 refactor" : "Files 1 item",
            _ => $"Files {count} items",
        });

        if (!string.IsNullOrWhiteSpace(projectDisplayName))
        {
            sb.Append(" into ").Append(projectDisplayName);
        }

        if (count > 1)
        {
            sb.Append(" as a chain (")
              .Append(ChainEdges.Describe(composition.Items.Select(i => i.DependsOnLocal).ToList()))
              .Append(')');
        }

        var clauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(d.AgentClassId))
        {
            clauses.Add($"class {d.AgentClassId}");
        }
        else if (!string.IsNullOrWhiteSpace(d.Agent))
        {
            clauses.Add($"agent {d.Agent}");
        }

        if (d.RequiredCapabilities is { Count: > 0 } caps)
        {
            clauses.Add($"needs {string.Join(", ", caps)}");
        }

        if (d.MinModelScore is { } score)
        {
            clauses.Add($"model score ≥ {score}");
        }

        if (!string.IsNullOrWhiteSpace(d.BaseBranch))
        {
            clauses.Add($"from {d.BaseBranch}");
        }

        if (!string.IsNullOrWhiteSpace(d.WorkBranch))
        {
            clauses.Add($"on {d.WorkBranch}");
        }

        if (!d.PushUpstream)
        {
            clauses.Add("no upstream push");
        }

        if (d.AuditMaxIterations is { } budget)
        {
            clauses.Add($"audit ×{budget}"
                + (string.IsNullOrWhiteSpace(d.AuditorProfile) ? string.Empty : $" ({d.AuditorProfile})"));
        }
        else if (!string.IsNullOrWhiteSpace(d.AuditorProfile))
        {
            clauses.Add($"auditors {d.AuditorProfile}");
        }

        if (!string.IsNullOrWhiteSpace(d.AuditComplexity))
        {
            clauses.Add($"complexity {d.AuditComplexity}");
        }

        if (d.Priority is { } priority && priority != 0)
        {
            clauses.Add($"priority {priority}");
        }

        if (d.WorkTimeoutMinutes is { } wt)
        {
            clauses.Add($"work timeout {wt} min");
        }

        if (d.MergeTimeoutMinutes is { } mtm)
        {
            clauses.Add($"merge timeout {mtm} min");
        }

        if (d.Knobs is { Count: > 0 } knobs)
        {
            clauses.Add(string.Join(", ", knobs.Select(k => $"{k.Key}={k.Value}")));
        }

        if (!string.IsNullOrWhiteSpace(d.ReleaseId))
        {
            clauses.Add("in a release");
        }

        if (composition.ExistingDependsOn.Count > 0)
        {
            var labels = composition.ExistingDependsOn
                .Select(id => existingItemLabel?.Invoke(id) ?? (id.Length >= 8 ? id[..8] : id))
                .ToList();
            var target = count > 1 ? "the chain's roots wait for" : "waits for";
            clauses.Add(labels.Count <= 3
                ? $"{target} {string.Join(", ", labels)}"
                : $"{target} {labels.Count} existing items");
        }

        sb.Append(clauses.Count == 0 ? " with project defaults" : " · " + string.Join(" · ", clauses));
        sb.Append('.');
        return sb.ToString();
    }
}
