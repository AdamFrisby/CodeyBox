namespace CodeyBox.Admin.Model;

/// <summary>
/// One item found in a pasted plan. <see cref="Number"/> is the 1-based
/// position in the parsed chain; <see cref="DependsOn"/> holds 1-based
/// numbers of earlier items this one waits for.
/// </summary>
public sealed record ParsedPlanItem(
    int Number,
    string Title,
    string Body,
    IReadOnlyList<int> DependsOn);

/// <summary>
/// How sure the parser is that the text is a plan of several items rather
/// than one prompt that happens to contain structure. The composer uses
/// this for its default only; the operator can always split or merge.
/// </summary>
public enum PlanConfidence
{
    /// <summary>Zero or one item — nothing to split.</summary>
    None,

    /// <summary>
    /// Several items were found, but only from signals a single long
    /// prompt also carries: unnumbered headings, or markers that arrive
    /// after a paragraph of prose. Default to one item; offer the split.
    /// </summary>
    Suggested,

    /// <summary>
    /// Several items with strong plan signals: numbered markers or
    /// numbered headings opening the text, explicit dependency lines, or
    /// horizontal rules. Default to a chain.
    /// </summary>
    Structured,
}

/// <summary>
/// The result of parsing a pasted plan: items in author order plus the
/// edges between them. <see cref="HadExplicitEdges"/> is true when at
/// least one item named its dependencies; otherwise the edges form the
/// default straight line (each item waits for the previous one).
/// </summary>
public sealed record ParsedPlan(
    IReadOnlyList<ParsedPlanItem> Items,
    bool HadExplicitEdges,
    PlanConfidence Confidence = PlanConfidence.None);
