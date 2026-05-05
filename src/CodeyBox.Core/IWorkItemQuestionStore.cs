namespace CodeyBox.Core;

/// <summary>
/// Durable store of agent-emitted questions awaiting operator answers.
/// </summary>
public interface IWorkItemQuestionStore
{
    /// <summary>
    /// Persists <paramref name="question"/> only when no row with the same
    /// (workItemId, questionId) pair already exists.
    /// Returns true when a new row was inserted, false when skipped.
    /// </summary>
    Task<bool> CreateIfNotExistsAsync(WorkItemQuestion question, CancellationToken ct = default);

    /// <summary>Returns a question by its composite key, or null if not found.</summary>
    Task<WorkItemQuestion?> GetAsync(string workItemId, string questionId, CancellationToken ct = default);

    /// <summary>Lists all questions (open + answered + dismissed) for a work item, ordered by askedAt.</summary>
    Task<IReadOnlyList<WorkItemQuestion>> ListByWorkItemAsync(string workItemId, CancellationToken ct = default);

    /// <summary>
    /// Marks a question as answered. No-op when the question is already answered or dismissed.
    /// </summary>
    Task AnswerAsync(string workItemId, string questionId, string answer, string? answeredBy, CancellationToken ct = default);

    /// <summary>
    /// Marks a question as dismissed. No-op when already dismissed or answered.
    /// </summary>
    Task DismissAsync(string workItemId, string questionId, string reason, CancellationToken ct = default);
}
