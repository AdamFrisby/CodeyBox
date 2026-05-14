using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Tests.Uat.WorkItemApisAndQueueControls;

internal static class WorkItemApisAndQueueControlsHelpers
{
    public const string ProjectId = "test-project";

    public static WorkItem Item(
        WorkItemState state = WorkItemState.Queued,
        string projectId = ProjectId,
        string title = "Work item API UAT",
        string prompt = "perform the requested change") => new()
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId(projectId),
            Title = title,
            Prompt = prompt,
            BaseBranch = "main",
            WorkBranch = "feature/" + Guid.NewGuid().ToString("N")[..8],
            Agent = AgentKind.Claude,
            State = state,
            QueuePosition = DateTimeOffset.UtcNow.Ticks,
        };

    public static WorkItem QuestionItem(WorkItemState state = WorkItemState.NeedsOperatorInput) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId(AnswerEndpointFactory.ProjectId),
        Title = "Question UAT",
        Prompt = "ask the operator",
        Agent = AgentKind.Claude,
        State = state,
        StartedAt = DateTimeOffset.UtcNow,
    };

    public static WorkItemQuestion Question(WorkItem item, string questionId = "q-uat") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        WorkItemId = item.Id.ToString(),
        QuestionId = questionId,
        QuestionText = "Which implementation path should be used?",
    };

    public static Suggestion Suggestion(
        string? title = null,
        string? rationale = null,
        string projectId = SuggestionsApiFactory.ProjectId,
        string state = "open") => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            SourceWorkItemId = Guid.NewGuid().ToString(),
            ProjectId = projectId,
            Title = title ?? "Add validation tests",
            Rationale = rationale ?? "The adjacent validation path needs explicit coverage.",
            Category = "test-coverage",
            Severity = "notable",
            EstimatedEffort = "small",
            CreatedAt = DateTimeOffset.UtcNow,
            State = state,
        };

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json;
    }

    public static async Task<List<WorkItem>> ListAllAsync(this IWorkItemStore store)
    {
        var items = new List<WorkItem>();
        await foreach (var item in store.ListAsync())
            items.Add(item);
        return items;
    }

    public static async Task CreateBareRepoWithSingleFileDiffAsync(
        string gitRoot,
        WorkItemId id,
        string baseBranch,
        string workBranch)
    {
        var barePath = Path.Combine(gitRoot, id + ".git");
        var tempWork = Path.Combine(Path.GetTempPath(), "codeybox-uat-diff-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempWork);
            await TestSupport.RunGit(tempWork, "init", "-b", baseBranch);
            await TestSupport.RunGit(tempWork, "config", "user.email", "test@test.com");
            await TestSupport.RunGit(tempWork, "config", "user.name", "Test");

            await File.WriteAllTextAsync(Path.Combine(tempWork, "README.md"), "base\n");
            await TestSupport.RunGit(tempWork, "add", "README.md");
            await TestSupport.RunGit(tempWork, "commit", "-m", "initial");

            await TestSupport.RunGit(tempWork, "checkout", "-b", workBranch);
            await File.WriteAllTextAsync(Path.Combine(tempWork, "README.md"), "base\nwork item change\n");
            await TestSupport.RunGit(tempWork, "add", "README.md");
            await TestSupport.RunGit(tempWork, "commit", "-m", "work item change");

            await TestSupport.RunGit(Path.GetTempPath(), "clone", "--bare", "--local", tempWork, barePath);
        }
        finally
        {
            if (Directory.Exists(tempWork))
                Directory.Delete(tempWork, recursive: true);
        }
    }
}

internal sealed record WorkItemDto(
    string Id,
    string? ExternalId,
    string ProjectId,
    string Title,
    string Prompt,
    string Agent,
    string? AuditorProfile,
    string? RepositoryUrl,
    string? BaseBranch,
    string? WorkBranch,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? LastError,
    int UpstreamPushAttempts,
    IReadOnlyList<string> DependsOn,
    bool DependsOnSatisfied,
    IReadOnlyDictionary<string, string?> DependsOnExternalIds,
    long QueuePosition,
    string? ReplayOfWorkItemId,
    string? AgentClassId,
    int? AuditIterations,
    int? FinalAuditBlockingFindings,
    string? MergeSha,
    int MinModelScore,
    string? ReleaseId,
    string? FailureKind,
    DateTimeOffset? QuotaResetAt,
    DateTimeOffset? NextQuotaRetryAt,
    int QuotaRetryAttempts);
