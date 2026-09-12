using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Questions parking + suggestions pickup for the pipeline. Owns the
/// TryParkForQuestionsAsync / TryReadSuggestionsFileAsync /
/// PickUpSuggestionsAsync cluster; <see cref="PipelineRunner"/> delegates to it.
/// The NeedsOperatorInput <c>Transition</c> stays on the runner (the spine), so
/// the runner injects it here as an explicit seam.
/// Extracted mechanically from <see cref="PipelineRunner"/>; behavior is unchanged.
/// </summary>
internal sealed class QuestionsSuggestionsParker
{
    private readonly IWorkItemQuestionStore? _questionStore;
    private readonly ISuggestionStore? _suggestions;
    private readonly IWorkItemStore _store;
    private readonly IWebhookDispatcher _webhooks;
    private readonly PipelineTuningSnapshot _pipelineTuning;
    private readonly ILogger<PipelineRunner> _log;
    private readonly Func<WorkItem, WorkItemState, CancellationToken, Project?, Task> _transitionAsync;

    public QuestionsSuggestionsParker(
        IWorkItemQuestionStore? questionStore,
        ISuggestionStore? suggestions,
        IWorkItemStore store,
        IWebhookDispatcher webhooks,
        PipelineTuningSnapshot pipelineTuning,
        ILogger<PipelineRunner> log,
        Func<WorkItem, WorkItemState, CancellationToken, Project?, Task> transitionAsync)
    {
        _questionStore = questionStore;
        _suggestions = suggestions;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _webhooks = webhooks ?? throw new ArgumentNullException(nameof(webhooks));
        _pipelineTuning = pipelineTuning ?? throw new ArgumentNullException(nameof(pipelineTuning));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _transitionAsync = transitionAsync ?? throw new ArgumentNullException(nameof(transitionAsync));
    }

    /// <summary>
    /// Parses agent stdout for question blocks, persists new ones, and transitions
    /// the work item to NeedsOperatorInput if at least one new question was created.
    /// Returns true when the work item was parked; false otherwise.
    /// </summary>
    public async Task<bool> TryParkForQuestionsAsync(
        WorkItem item, Project project, string agentStdout, CancellationToken ct)
    {
        var parsed = QuestionParser.Parse(agentStdout, _log);
        if (parsed.Count == 0) return false;

        // Count existing questions to enforce the per-work-item cap.
        var existing = await _questionStore!.ListByWorkItemAsync(item.Id.ToString(), ct);
        var existingCount = existing.Count;

        var newQuestions = new List<WorkItemQuestion>();
        foreach (var p in parsed)
        {
            if (existingCount + newQuestions.Count >= _pipelineTuning.Current.MaxQuestionsPerWorkItem)
            {
                _log.LogWarning(
                    "Work item {Id}: question cap ({Max}) reached; ignoring additional <codeybox-question> blocks",
                    item.Id, _pipelineTuning.Current.MaxQuestionsPerWorkItem);
                break;
            }

            var question = new WorkItemQuestion
            {
                Id = Guid.NewGuid().ToString(),
                WorkItemId = item.Id.ToString(),
                QuestionId = p.QuestionId,
                QuestionText = p.QuestionText,
                AskedAt = DateTimeOffset.UtcNow,
            };

            var created = await _questionStore.CreateIfNotExistsAsync(question, ct);
            if (created)
                newQuestions.Add(question);
        }

        if (newQuestions.Count == 0) return false;

        // Transition to NeedsOperatorInput and fire one webhook per new question.
        await _transitionAsync(item, WorkItemState.NeedsOperatorInput, ct, project);

        foreach (var q in newQuestions)
        {
            AuditLog.WorkItemTransitioned(item.Id, $"question_asked:{q.QuestionId}");
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.question_asked",
                WorkItem = await _store.GetAsync(item.Id, CancellationToken.None) ?? item,
                Project = project,
                Details = new QuestionAskedDetails(item.Id.ToString(), project.Id.Value, q.QuestionId, q.QuestionText),
            }, CancellationToken.None);
        }

        _log.LogInformation(
            "Work item {Id} parked at NeedsOperatorInput with {Count} open question(s)",
            item.Id, newQuestions.Count);
        return true;
    }

    /// <summary>
    /// Tries to read <c>.codeybox/suggestions.json</c> from the sandbox working
    /// directory. Returns the raw content string when the file exists and is
    /// within the 256 KB size limit; null otherwise.
    /// </summary>
    public async Task<string?> TryReadSuggestionsFileAsync(ISandbox sandbox, CancellationToken ct)
    {
        const int MaxBytes = 256 * 1024;
        const string SuggestionsPath = SandboxConventions.WorkDir + "/.codeybox/suggestions.json";

        // Read at most MaxBytes+1 bytes at the source so the sandbox provider's
        // stdout buffer is bounded before the size check fires (prevents OOM on
        // a multi-gigabyte file written by a compromised agent).
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["head", "-c", (MaxBytes + 1).ToString(), SuggestionsPath],
        }, ct);

        if (!result.Success) return null;

        var byteCount = System.Text.Encoding.UTF8.GetByteCount(result.Stdout);
        if (byteCount > MaxBytes)
        {
            _log.LogWarning("suggestions.json exceeds 256 KB ({Bytes} bytes); skipping", byteCount);
            return null;
        }

        return result.Stdout;
    }

    /// <summary>
    /// Parses raw suggestions JSON, persists valid entries, and fires one
    /// <c>work_item.suggestion</c> webhook per suggestion.
    /// </summary>
    public async Task PickUpSuggestionsAsync(
        WorkItem item, Project project, string rawJson, CancellationToken ct)
    {
        if (_suggestions is null) return;

        var entries = SuggestionsFileParser.Parse(rawJson, _log);
        if (entries.Count == 0) return;

        foreach (var entry in entries)
        {
            var suggestion = new Suggestion
            {
                Id = Guid.NewGuid().ToString(),
                SourceWorkItemId = item.Id.ToString(),
                ProjectId = item.ProjectId.Value,
                Title = entry.Title,
                Rationale = entry.Rationale,
                Category = entry.Category,
                Severity = entry.Severity,
                EstimatedEffort = entry.EstimatedEffort,
                FilesReferenced = entry.FilesReferenced,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            try
            {
                await _suggestions.CreateAsync(suggestion, ct);
                AuditLog.SuggestionCreated(suggestion.Id, suggestion.SourceWorkItemId, suggestion.ProjectId);
                _log.LogInformation(
                    "Suggestion {SuggestionId} persisted from work item {WorkItemId}: {Title}",
                    suggestion.Id, item.Id, suggestion.Title.ReplaceLineEndings(" "));

                await _webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "work_item.suggestion",
                    WorkItem = item,
                    Project = project,
                    Details = new SuggestionWebhookDetails(
                        suggestion.Id,
                        suggestion.Title,
                        suggestion.Category,
                        suggestion.Severity,
                        suggestion.EstimatedEffort,
                        suggestion.FilesReferenced),
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Failed to persist or dispatch suggestion '{Title}' from work item {WorkItemId}; skipping",
                    suggestion.Title.ReplaceLineEndings(" "), item.Id);
            }
        }
    }
}
