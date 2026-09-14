namespace CodeyBox.Core;

/// <summary>
/// Stable names for built-in auditors that are selected by configuration or
/// composition without depending on their concrete implementation types.
/// </summary>
public static class WellKnownAuditorNames
{
    public const string BuildScript = "process:build-script";

    /// <summary>
    /// Stable name for the human deployment reviewer. Composed like any other
    /// auditor and removable per project via <c>ExcludedAuditors</c>.
    /// </summary>
    public const string HumanDeploymentReview = "human:deployment-review";

    /// <summary>
    /// Stable prefix for the operator question backing a human deployment
    /// review. The full question id appends the audit iteration
    /// (<c>human-deployment-review-3</c>) so each iteration's verdict is
    /// recorded against its own question row.
    /// </summary>
    public const string HumanDeploymentReviewQuestionPrefix = "human-deployment-review";
}

/// <summary>
/// Stable <see cref="IAuditor.Kind"/> values the pipeline branches on by
/// declaration. A human reviewer completes asynchronously through the
/// operator park/resume path rather than inline like tool/llm auditors.
/// </summary>
public static class WellKnownAuditorKinds
{
    public const string Human = "human";
}
