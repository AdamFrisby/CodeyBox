using System.Collections.Concurrent;
using CodeyBox.Core;

namespace CodeyBox.Notifications;

/// <summary>
/// Idempotency guard for inbound interactions: platforms retry deliveries,
/// so the same interaction delivered twice must answer once. The first
/// delivery claims the interaction id; replays observe the existing claim
/// and are reported as duplicates without touching the question store.
/// Entries expire so the table stays bounded.
/// </summary>
public interface IInteractionDedupStore
{
    /// <summary>Attempts to claim <paramref name="interactionId"/>.
    /// Returns true for the first delivery, false for a replay.</summary>
    bool TryClaim(string interactionId);
}

/// <summary>
/// In-memory interaction idempotency store with a bounded entry count and
/// time-based expiry. Thread-safe. Inject <see cref="TimeProvider"/> in
/// tests for deterministic expiry.
/// </summary>
public sealed class InMemoryInteractionDedupStore : IInteractionDedupStore
{
    private const int MaxEntries = 10_000;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _claimed = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly TimeSpan _entryLifetime;

    public InMemoryInteractionDedupStore(
        TimeProvider? clock = null,
        TimeSpan? entryLifetime = null)
    {
        _clock = clock ?? TimeProvider.System;
        _entryLifetime = entryLifetime ?? TimeSpan.FromHours(24);
    }

    public bool TryClaim(string interactionId)
    {
        EvictExpired();
        if (_claimed.Count >= MaxEntries)
            EvictExpired(forceOldest: true);
        return _claimed.TryAdd(interactionId, _clock.GetUtcNow());
    }

    private void EvictExpired(bool forceOldest = false)
    {
        var now = _clock.GetUtcNow();
        foreach (var (key, claimedAt) in _claimed)
        {
            if (now - claimedAt >= _entryLifetime)
                _claimed.TryRemove(key, out _);
            if (!forceOldest && _claimed.Count < MaxEntries)
                break;
        }
        if (forceOldest && _claimed.Count >= MaxEntries)
        {
            var oldest = _claimed.OrderBy(kv => kv.Value).FirstOrDefault();
            if (oldest.Key is not null)
                _claimed.TryRemove(oldest.Key, out _);
        }
    }
}

/// <summary>
/// Pure helpers binding operator questions to actionable notifications.
/// The decision logic lives here so it is directly input→output testable;
/// providers only render the result.
/// </summary>
public static class NotificationInteractionHelper
{
    /// <summary>Correlation token binding a notification to one question.
    /// Exact, parseable, and safe to echo back over the wire.</summary>
    public static string CorrelationTokenFor(string workItemId, string questionId) =>
        $"{workItemId}:{questionId}";

    public static bool TryParseCorrelationToken(string? token, out string workItemId, out string questionId)
    {
        workItemId = string.Empty;
        questionId = string.Empty;
        if (string.IsNullOrEmpty(token))
            return false;
        var split = token.IndexOf(':');
        if (split <= 0 || split == token.Length - 1)
            return false;
        workItemId = token[..split];
        questionId = token[(split + 1)..];
        return true;
    }

    /// <summary>Builds an actionable notification for an open question.
    /// <paramref name="answerBaseUrl"/> is the public base URL used to form
    /// the fallback answer link surfaced by notification-only providers.</summary>
    public static Notification ForQuestion(
        string workItemId,
        string questionId,
        string questionText,
        IReadOnlyList<string> options,
        string? answerBaseUrl)
    {
        var actions = options
            .Select(o => new NotificationAction
            {
                Label = o,
                Value = o,
                WorkItemId = workItemId,
                QuestionId = questionId,
            })
            .ToList();
        var answerUrl = string.IsNullOrWhiteSpace(answerBaseUrl)
            ? null
            : $"{answerBaseUrl.TrimEnd('/')}/workitems/{workItemId}/questions";
        return new Notification
        {
            ConditionId = "operator_question",
            Title = $"Input needed: {questionId}",
            Summary = questionText,
            Body = questionText,
            Severity = NotificationSeverity.Warning,
            Timestamp = DateTimeOffset.UtcNow,
            Actions = actions,
            AnswerUrl = answerUrl,
            CorrelationToken = CorrelationTokenFor(workItemId, questionId),
        };
    }

    /// <summary>Fallback line appended by notification-only providers so a
    /// question still surfaces with a route to answer elsewhere.</summary>
    public static string WithAnswerFallback(string body, string? answerUrl) =>
        string.IsNullOrWhiteSpace(answerUrl)
            ? body
            : $"{body}\nAnswer here: {answerUrl}";

    /// <summary>Auditable platform identity recorded in
    /// <c>answeredBy</c>: provider-qualified so a human can tell later
    /// which platform user decided. Exact format, no unescaped surprises.</summary>
    public static string FormatAnsweredBy(string provider, string userId, string? login) =>
        string.IsNullOrWhiteSpace(login)
            ? $"{provider}:{userId}"
            : $"{provider}:{userId} ({login})";
}
