using System;
using System.Collections.Generic;
using System.Linq;
using CodeyBox.Core;

namespace CodeyBox.Projects;

/// <summary>
/// Composes a self-review checklist from the active set of auditors at runtime.
/// </summary>
public static class SelfReviewChecklistComposer
{
    public static string Compose(IEnumerable<IAuditor>? auditors)
    {
        if (auditors == null)
            return string.Empty;

        var guidances = auditors
            .Select(a => a.SelfReviewGuidance)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (guidances.Count == 0)
            return string.Empty;

        return string.Join("\n\n", guidances);
    }
}
