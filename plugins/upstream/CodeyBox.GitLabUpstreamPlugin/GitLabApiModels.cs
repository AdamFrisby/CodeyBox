using System.Text.Json.Serialization;

namespace CodeyBox.GitLabUpstreamPlugin;

// JSON shapes for GitLab REST API v4 (gitlab.com and self-hosted 16.x/17.x).
// Only the fields this provider reads are modelled; unknown fields are
// ignored so newer instances stay compatible. Ids that cross the contract
// boundary are rendered as strings there (forges differ); numeric ids stay
// numeric here.

internal sealed record GitLabMergeRequest(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("iid")] long Iid,
    [property: JsonPropertyName("project_id")] long ProjectId,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("detailed_merge_status")] string? DetailedMergeStatus,
    [property: JsonPropertyName("source_branch")] string? SourceBranch,
    [property: JsonPropertyName("target_branch")] string? TargetBranch,
    [property: JsonPropertyName("sha")] string? Sha,
    [property: JsonPropertyName("merge_commit_sha")] string? MergeCommitSha,
    [property: JsonPropertyName("squash_commit_sha")] string? SquashCommitSha,
    [property: JsonPropertyName("web_url")] string? WebUrl,
    [property: JsonPropertyName("diff_refs")] GitLabDiffRefs? DiffRefs)
{
    // The merge commit is absent for squash merges; the squash sha is the
    // merge evidence then. Either identifies the merged result.
    public string? EffectiveMergeSha =>
        !string.IsNullOrWhiteSpace(MergeCommitSha) ? MergeCommitSha : SquashCommitSha;
}

internal sealed record GitLabDiffRefs(
    [property: JsonPropertyName("base_sha")] string? BaseSha,
    [property: JsonPropertyName("head_sha")] string? HeadSha,
    [property: JsonPropertyName("start_sha")] string? StartSha);

internal sealed record GitLabUser(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("name")] string? Name)
{
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Username) ? Username! : Name ?? "unknown";
}

internal sealed record GitLabApprovedBy(
    [property: JsonPropertyName("user")] GitLabUser? User);

internal sealed record GitLabApprover(
    [property: JsonPropertyName("user")] GitLabUser? User);

// GET /projects/:id/merge_requests/:iid/approvals
internal sealed record GitLabApprovals(
    [property: JsonPropertyName("approved")] bool Approved,
    [property: JsonPropertyName("approvals_required")] int ApprovalsRequired,
    [property: JsonPropertyName("approvals_left")] int ApprovalsLeft,
    [property: JsonPropertyName("approved_by")] IReadOnlyList<GitLabApprovedBy>? ApprovedBy);

// GET /projects/:id/merge_requests/:iid/approval_rules (and each rule in approval_state)
internal sealed record GitLabApprovalRule(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("rule_type")] string? RuleType,
    [property: JsonPropertyName("approvals_required")] int ApprovalsRequired,
    [property: JsonPropertyName("approved")] bool Approved,
    [property: JsonPropertyName("approved_by")] IReadOnlyList<GitLabApprovedBy>? ApprovedBy,
    [property: JsonPropertyName("approvers")] IReadOnlyList<GitLabApprover>? Approvers);

// GET /projects/:id/merge_requests/:iid/approval_state
internal sealed record GitLabApprovalStateWrapper(
    [property: JsonPropertyName("approval_state")] GitLabApprovalState? ApprovalState);

internal sealed record GitLabApprovalState(
    [property: JsonPropertyName("rules")] IReadOnlyList<GitLabApprovalRule>? Rules);

// GET /projects/:id/pipelines?sha=...
internal sealed record GitLabPipeline(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("iid")] long Iid,
    [property: JsonPropertyName("sha")] string? Sha,
    [property: JsonPropertyName("ref")] string? Ref,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("web_url")] string? WebUrl,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt);

// GET /projects/:id/pipelines/:pipeline_id/jobs
internal sealed record GitLabJob(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("stage")] string? Stage,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("web_url")] string? WebUrl,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt);

// GET /projects/:id/repository/commits/:sha/statuses
internal sealed record GitLabCommitStatus(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("sha")] string? Sha,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("target_url")] string? TargetUrl,
    [property: JsonPropertyName("description")] string? Description);

// A discussion thread on a merge request (a reply target and a container
// for file-anchored notes).
internal sealed record GitLabDiscussion(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("individual_note")] bool IndividualNote,
    [property: JsonPropertyName("notes")] IReadOnlyList<GitLabNote>? Notes);

// Notes on a merge request (flat discussion view).
internal sealed record GitLabNotePosition(
    [property: JsonPropertyName("position_type")] string? PositionType,
    [property: JsonPropertyName("old_path")] string? OldPath,
    [property: JsonPropertyName("new_path")] string? NewPath,
    [property: JsonPropertyName("old_line")] int? OldLine,
    [property: JsonPropertyName("new_line")] int? NewLine)
{
    // Code-anchored threads carry a new-side path and line; plain
    // discussion notes have a null position.
    public string? AnchorPath =>
        !string.IsNullOrWhiteSpace(NewPath) ? NewPath
        : !string.IsNullOrWhiteSpace(OldPath) ? OldPath
        : null;

    public int? AnchorLine => NewLine ?? OldLine;
}

internal sealed record GitLabNote(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("author")] GitLabUser? Author,
    [property: JsonPropertyName("system")] bool System,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("discussion_id")] string? DiscussionId,
    [property: JsonPropertyName("position")] GitLabNotePosition? Position,
    [property: JsonPropertyName("resolvable")] bool Resolvable);

// Project, group and system hooks share the trigger-flag shape.
internal sealed record GitLabHook(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("push_events")] bool PushEvents,
    [property: JsonPropertyName("tag_push_events")] bool TagPushEvents,
    [property: JsonPropertyName("merge_requests_events")] bool MergeRequestsEvents,
    [property: JsonPropertyName("pipeline_events")] bool PipelineEvents,
    [property: JsonPropertyName("job_events")] bool JobEvents,
    [property: JsonPropertyName("note_events")] bool NoteEvents,
    [property: JsonPropertyName("wiki_page_events")] bool WikiPageEvents,
    [property: JsonPropertyName("deployment_events")] bool DeploymentEvents,
    [property: JsonPropertyName("member_events")] bool MemberEvents,
    [property: JsonPropertyName("subgroup_events")] bool SubgroupEvents,
    [property: JsonPropertyName("release_events")] bool ReleaseEvents,
    [property: JsonPropertyName("push_events_branch_filter")] string? PushEventsBranchFilter)
{
    public IReadOnlyList<string> EnabledEvents
    {
        get
        {
            var events = new List<string>();
            if (PushEvents) events.Add("push");
            if (TagPushEvents) events.Add("tag_push");
            if (MergeRequestsEvents) events.Add("merge_request");
            if (PipelineEvents) events.Add("pipeline");
            if (JobEvents) events.Add("job");
            if (NoteEvents) events.Add("note");
            if (WikiPageEvents) events.Add("wiki_page");
            if (DeploymentEvents) events.Add("deployment");
            if (MemberEvents) events.Add("member");
            if (SubgroupEvents) events.Add("subgroup");
            if (ReleaseEvents) events.Add("release");
            return events;
        }
    }
}

internal sealed record GitLabNamespace(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("kind")] string? Kind);

// GET /projects/:id
internal sealed record GitLabProject(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("path_with_namespace")] string? PathWithNamespace,
    [property: JsonPropertyName("default_branch")] string? DefaultBranch,
    [property: JsonPropertyName("visibility")] string? Visibility,
    [property: JsonPropertyName("web_url")] string? WebUrl,
    [property: JsonPropertyName("only_allow_merge_if_pipeline_succeeds")] bool OnlyAllowMergeIfPipelineSucceeds,
    [property: JsonPropertyName("namespace")] GitLabNamespace? Namespace);

// GET /projects/:id/protected_branches
internal sealed record GitLabProtectedBranch(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("code_owner_approval_required")] bool CodeOwnerApprovalRequired);

// GET /projects/:id/approvals (project-level merge approval settings)
internal sealed record GitLabProjectApprovals(
    [property: JsonPropertyName("approvals_before_merge")] int ApprovalsBeforeMerge);
