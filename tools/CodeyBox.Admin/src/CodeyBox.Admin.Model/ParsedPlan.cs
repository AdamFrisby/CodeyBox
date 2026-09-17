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
/// The result of parsing a pasted plan: items in author order plus the
/// edges between them. <see cref="HadExplicitEdges"/> is true when at
/// least one item named its dependencies; otherwise the edges form the
/// default straight line (each item waits for the previous one).
/// </summary>
public sealed record ParsedPlan(
    IReadOnlyList<ParsedPlanItem> Items,
    bool HadExplicitEdges);
