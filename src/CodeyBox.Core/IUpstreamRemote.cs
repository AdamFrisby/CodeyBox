namespace CodeyBox.Core;

/// <summary>
/// Replication target for the host bare repo, run AFTER a successful local
/// merge. This is the only component that holds upstream credentials (e.g.
/// a GitHub PAT). Sandboxes never see it. If push fails, the local repo
/// remains the source of truth and the orchestrator retries.
/// </summary>
public interface IUpstreamRemote
{
    /// <summary>Stable identifier for diagnostics ("noop", "github", "git-generic").</summary>
    string Name { get; }

    /// <summary>
    /// Pushes the named ref from the host bare repo to the upstream. The
    /// repository identifier is opaque and must be understood by the host
    /// git module that materialises it.
    /// </summary>
    Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default);

    /// <summary>
    /// Completes a work item's upstream lifecycle after a successful local merge.
    /// Implementations decide what "complete" means for their forge type: a bare
    /// push, opening a pull request, or opening and auto-merging one. Throws on
    /// transient failures so the orchestrator can retry; returns a partial outcome
    /// for graceful soft-failures (e.g. PR already exists).
    /// </summary>
    Task<UpstreamCompletionOutcome> CompleteAsync(UpstreamCompletionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Attempts to merge <paramref name="sourceBranch"/> into <paramref name="targetBranch"/>
    /// on the upstream (e.g. GitHub Merges API, or host-side git merge+push for generic git).
    /// Returns <c>true</c> when the merge succeeded or the target was already up-to-date.
    /// Returns <c>false</c> when a merge conflict is detected; the caller should emit a
    /// <c>release.sync_conflict</c> event and leave the conflict for a human to resolve.
    /// Throws on unexpected infrastructure failures (network error, auth failure, etc.).
    /// </summary>
    Task<bool> TryMergeUpstreamBranchAsync(string targetBranch, string sourceBranch, CancellationToken ct = default);

    /// <summary>
    /// Creates a tag at <paramref name="sha"/> and publishes a release named
    /// <paramref name="tagName"/> on the upstream forge. Returns the URL of the
    /// created release, or <c>null</c> when the upstream kind does not support
    /// forge releases (e.g. noop, git-generic). Never throws on unsupported — the
    /// caller logs and continues; a missing GitHub release is not a hard failure.
    /// </summary>
    Task<string?> CreateTagAndReleaseAsync(string tagName, string sha, string? releaseNotes, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// Fetches the current head of <paramref name="baseBranch"/> from the
    /// upstream forge into the host bare repo, overwriting the local ref, and
    /// returns the new commit sha. Used by the auto-merge race recovery flow
    /// in the orchestrator when GitHub returns 405 on the merge call: we
    /// refetch the upstream main to detect whether the race is real (base
    /// moved → re-run merge phase) or a different kind of unmergeability
    /// (base unchanged → branch protection or some other issue we can't fix
    /// by retrying).
    ///
    /// Default returns <c>null</c> for upstream kinds that don't model a
    /// remote base branch (noop, git-generic without a configured base).
    /// </summary>
    Task<string?> FetchBaseBranchAsync(string repositoryId, string baseBranch, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// Lists open pull requests whose head branch starts with <paramref name="branchPrefix"/>
    /// and whose mergeability is known. Used by the stale-base PR sweeper to
    /// detect CodeyBox-authored PRs whose base branch has moved and produced a
    /// conflict the auto-merger can no longer resolve.
    ///
    /// <para>Implementations only need to return PRs whose mergeability has
    /// been computed by the forge (i.e. <c>mergeable</c> is not null on
    /// GitHub). PRs whose state is still being calculated are skipped so the
    /// sweeper reconsiders them on the next tick.</para>
    ///
    /// <para>Default returns an empty list for upstream kinds that don't
    /// model PRs (noop, git-generic).</para>
    /// </summary>
    Task<IReadOnlyList<UpstreamPullRequest>> ListOpenPullRequestsAsync(
        string branchPrefix, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<UpstreamPullRequest>>([]);

    /// <summary>
    /// Reads a pull request by forge-assigned number. Returns <c>null</c> when
    /// this upstream kind does not model pull requests or the PR is unavailable.
    /// </summary>
    Task<UpstreamPullRequestState?> GetPullRequestAsync(
        int number, CancellationToken ct = default)
        => Task.FromResult<UpstreamPullRequestState?>(null);

    /// <summary>
    /// Reads the review state of a pull/merge request: individual reviews
    /// (approvals, change requests, comments), the reviewers or rules the
    /// forge still requires, and whether the forge considers the review
    /// requirements satisfied.
    /// Forge mapping: GitHub reviews, GitLab approval rules (rule names appear
    /// in <see cref="UpstreamReviewState.RequiredReviewers"/> and the quorum in
    /// <see cref="UpstreamReviewState.RequiredApprovalCount"/>), Azure DevOps
    /// reviewer policies (required reviewers plus minimum-approver count).
    ///
    /// <para>Returns <c>null</c> when this upstream kind cannot report review
    /// state. A non-<c>null</c> result with empty <c>Reviews</c> means the
    /// provider supports reviews and there are none yet — callers must not
    /// treat "no reviews" and "cannot tell" the same.</para>
    ///
    /// <para>Unsupported is non-fatal: callers log and continue without
    /// review gating.</para>
    /// </summary>
    Task<UpstreamReviewState?> GetReviewStateAsync(
        int number, CancellationToken ct = default)
        => Task.FromResult<UpstreamReviewState?>(null);

    /// <summary>
    /// Reads CI check and commit-status results for a head commit sha:
    /// each check's identity, state and human-openable URL, plus whether the
    /// forge considers the required checks satisfied (which is what gates a merge).
    /// Forge mapping: GitHub check runs + commit statuses, GitLab pipelines,
    /// Azure DevOps build validations, Gitea/Forgejo commit statuses.
    ///
    /// <para>Returns <c>null</c> when this upstream kind cannot report checks.
    /// A non-<c>null</c> result with empty <c>Checks</c> means the provider
    /// supports checks and none ran for this sha.</para>
    ///
    /// <para>Unsupported is non-fatal: callers log and continue without
    /// check gating.</para>
    /// </summary>
    Task<UpstreamCheckSummary?> GetCheckResultsAsync(
        string headSha, CancellationToken ct = default)
        => Task.FromResult<UpstreamCheckSummary?>(null);

    /// <summary>
    /// Lists comments on a pull/merge request, oldest first. Where the forge
    /// distinguishes plain discussion from code-anchored threads, both are
    /// returned: thread comments carry <see cref="UpstreamComment.FilePath"/>
    /// and <see cref="UpstreamComment.Line"/>. GitLab calls these discussions;
    /// the abstraction deliberately uses the neutral noun "comment".
    ///
    /// <para>Returns <c>null</c> when this upstream kind cannot read comments.
    /// A non-<c>null</c> empty list means the provider supports comments and
    /// there are none.</para>
    /// </summary>
    Task<IReadOnlyList<UpstreamComment>?> ListCommentsAsync(
        int number, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<UpstreamComment>?>(null);

    /// <summary>
    /// Posts a comment on a pull/merge request. Set
    /// <see cref="NewUpstreamComment.FilePath"/> and
    /// <see cref="NewUpstreamComment.Line"/> for a file-anchored review
    /// thread, <see cref="NewUpstreamComment.ReplyToId"/> to reply inside an
    /// existing thread (GitLab discussion reply); otherwise a plain
    /// top-level comment is posted.
    ///
    /// <para>Returns the posted comment, or <c>null</c> when this upstream
    /// kind cannot post comments. Unsupported is non-fatal: callers log and
    /// continue.</para>
    /// </summary>
    Task<UpstreamComment?> PostCommentAsync(
        int number, NewUpstreamComment comment, CancellationToken ct = default)
        => Task.FromResult<UpstreamComment?>(null);

    /// <summary>
    /// Lists webhook subscriptions on the forge, so CodeyBox can reconcile
    /// rather than duplicate them.
    ///
    /// <para>Returns <c>null</c> when this upstream kind cannot manage
    /// subscriptions. A non-<c>null</c> empty list means the provider
    /// supports subscriptions and none exist.</para>
    /// </summary>
    Task<IReadOnlyList<UpstreamWebhookSubscription>?> ListWebhookSubscriptionsAsync(
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<UpstreamWebhookSubscription>?>(null);

    /// <summary>
    /// Creates a webhook subscription on the forge. Scoping differs per forge
    /// (Gitea/Forgejo: repository, organisation, user, system; Azure DevOps:
    /// service-hook subscriptions): <see cref="NewUpstreamWebhookSubscription.Scope"/>
    /// carries the scope as an opaque string — see
    /// <see cref="UpstreamWebhookScopes"/> for the well-known values — and a
    /// forge with no equivalent for the requested scope must return
    /// <c>null</c> (unsupported) rather than force a translation.
    ///
    /// <para>Returns the created subscription, or <c>null</c> when this
    /// upstream kind cannot create subscriptions. Unsupported is non-fatal:
    /// callers log and continue.</para>
    /// </summary>
    Task<UpstreamWebhookSubscription?> CreateWebhookSubscriptionAsync(
        NewUpstreamWebhookSubscription subscription, CancellationToken ct = default)
        => Task.FromResult<UpstreamWebhookSubscription?>(null);

    /// <summary>
    /// Removes a webhook subscription by the forge-assigned
    /// <see cref="UpstreamWebhookSubscription.Id"/>. Returns <c>null</c> when
    /// this upstream kind cannot manage subscriptions, <c>true</c> when the
    /// subscription was removed, <c>false</c> when the id is unknown.
    /// Unsupported is non-fatal: callers log and continue.
    /// </summary>
    Task<bool?> DeleteWebhookSubscriptionAsync(
        string id, CancellationToken ct = default)
        => Task.FromResult<bool?>(null);

    /// <summary>
    /// Reads repository metadata merge readiness genuinely depends on:
    /// default branch, visibility and branch protection rules.
    /// Returns <c>null</c> when this upstream kind cannot report metadata.
    /// Unsupported is non-fatal: callers log and continue.
    /// </summary>
    Task<UpstreamRepositoryMetadata?> GetRepositoryMetadataAsync(
        CancellationToken ct = default)
        => Task.FromResult<UpstreamRepositoryMetadata?>(null);
}

public sealed record UpstreamPullRequestState(
    int Number,
    string Url,
    PullRequestStatus Status,
    string? MergeCommitSha);

/// <summary>
/// Snapshot of an open pull request as seen by an <see cref="IUpstreamRemote"/>
/// at a point in time. Fields mirror the subset of the forge PR object the
/// stale-base sweeper needs: identity (number + URL), branch endpoints, head
/// sha, and a textual mergeability classification.
/// </summary>
public sealed record UpstreamPullRequest
{
    public required int Number { get; init; }
    public required string Url { get; init; }
    public required string HeadBranch { get; init; }
    public required string HeadSha { get; init; }
    public required string BaseBranch { get; init; }
    /// <summary>
    /// True when the forge reports the PR has conflicts that need a
    /// manual rebase (GitHub: <c>mergeable=false</c> or
    /// <c>mergeable_state=dirty</c>). False when the PR is mergeable or
    /// blocked for an unrelated reason (branch protection, awaiting review).
    /// </summary>
    public required bool HasMergeConflict { get; init; }
}

public sealed record UpstreamPushResult(bool Success, string? Error);

/// <summary>
/// One review on a pull/merge request, in forge-neutral terms.
/// </summary>
public sealed record UpstreamReview
{
    /// <summary>Forge login or display name of the reviewer.</summary>
    public required string Reviewer { get; init; }

    /// <summary>What the reviewer decided.</summary>
    public required UpstreamReviewVerdict Verdict { get; init; }

    /// <summary>When the review was submitted, if the forge reports it.</summary>
    public DateTimeOffset? SubmittedAt { get; init; }
}

/// <summary>Reviewer decision, general enough for GitHub reviews, GitLab approvals and Azure DevOps votes.</summary>
public enum UpstreamReviewVerdict
{
    /// <summary>Reviewer has not decided yet (e.g. requested but pending).</summary>
    Pending,
    /// <summary>Reviewer approved (GitHub approve, GitLab approve, Azure DevOps approve/approve-with-suggestions).</summary>
    Approved,
    /// <summary>Reviewer requested changes or rejected (GitHub request-changes, Azure DevOps reject).</summary>
    ChangesRequested,
    /// <summary>Reviewer commented without approving or blocking.</summary>
    Commented,
    /// <summary>A previous decision was dismissed or reset.</summary>
    Dismissed,
}

/// <summary>
/// Review state of a pull/merge request: who said what, what the forge still
/// requires, and whether the requirements are satisfied. The provider computes
/// <see cref="RequirementsMet"/> using the forge's own rules so callers never
/// re-implement quorum logic.
/// </summary>
public sealed record UpstreamReviewState
{
    /// <summary>Individual reviews; empty when nobody has reviewed yet.</summary>
    public required IReadOnlyList<UpstreamReview> Reviews { get; init; }

    /// <summary>
    /// Reviewers or rules the forge still requires for merge: GitHub requested
    /// reviewers, GitLab approval-rule names, Azure DevOps required reviewers.
    /// Empty when nothing further is required.
    /// </summary>
    public required IReadOnlyList<string> RequiredReviewers { get; init; }

    /// <summary>
    /// Minimum number of approvals the forge requires (GitLab approval-rule
    /// quorum, Azure DevOps minimum-approver policy). Zero when the forge has
    /// no quorum rule.
    /// </summary>
    public int RequiredApprovalCount { get; init; }

    /// <summary>True when the forge considers its review requirements satisfied.</summary>
    public required bool RequirementsMet { get; init; }
}

/// <summary>State of one CI check or commit status, across forge models.</summary>
public enum UpstreamCheckState
{
    /// <summary>Queued or running; outcome unknown.</summary>
    Pending,
    /// <summary>Passed (GitHub success, GitLab pipeline success, Gitea success status).</summary>
    Passing,
    /// <summary>Failed (GitHub failure/error, GitLab pipeline failure, Azure failed validation).</summary>
    Failing,
    /// <summary>Finished without pass/fail (GitHub neutral, GitLab manual/cancelled-as-neutral).</summary>
    Neutral,
    /// <summary>Skipped by the forge or its configuration.</summary>
    Skipped,
    /// <summary>Cancelled before completion.</summary>
    Cancelled,
}

/// <summary>One CI check or commit-status result for a head sha.</summary>
public sealed record UpstreamCheckResult
{
    /// <summary>Check identity (GitHub check-run/context name, GitLab job or pipeline name).</summary>
    public required string Name { get; init; }

    /// <summary>Outcome in forge-neutral terms.</summary>
    public required UpstreamCheckState State { get; init; }

    /// <summary>URL a human can open for details, if the forge provides one.</summary>
    public string? DetailsUrl { get; init; }

    /// <summary>Short human-readable description from the forge, if any.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// CI results for a head sha. The provider computes
/// <see cref="RequiredChecksPassed"/> using the forge's own required-checks
/// configuration so callers never re-implement branch-protection logic.
/// </summary>
public sealed record UpstreamCheckSummary
{
    /// <summary>Individual check results; empty when none ran for this sha.</summary>
    public required IReadOnlyList<UpstreamCheckResult> Checks { get; init; }

    /// <summary>
    /// True when the forge considers its required checks satisfied. True with
    /// empty <see cref="Checks"/> means the forge requires nothing for this ref.
    /// </summary>
    public required bool RequiredChecksPassed { get; init; }
}

/// <summary>
/// A comment on a pull/merge request. Plain discussion comments carry only
/// <see cref="Id"/>, <see cref="Author"/> and <see cref="Body"/>; code-anchored
/// review threads (GitHub review comments, GitLab discussions on a diff) also
/// carry <see cref="FilePath"/> and <see cref="Line"/>.
/// </summary>
public sealed record UpstreamComment
{
    /// <summary>Forge-assigned comment id, as a string (forges differ: int, long, GUID).</summary>
    public required string Id { get; init; }

    /// <summary>Forge login or display name of the author.</summary>
    public required string Author { get; init; }

    /// <summary>Comment body (rendered markdown on most forges).</summary>
    public required string Body { get; init; }

    /// <summary>Repo-relative file path for code-anchored threads; null for plain comments.</summary>
    public string? FilePath { get; init; }

    /// <summary>1-based line number for code-anchored threads; null for plain comments.</summary>
    public int? Line { get; init; }

    /// <summary>When the comment was created, if the forge reports it.</summary>
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>
/// A comment to post. The constructor rejects invalid states (empty body,
/// non-positive line, line without a file) so providers never have to
/// second-guess the caller's intent.
/// </summary>
public sealed record NewUpstreamComment
{
    /// <summary>Maximum accepted body length; longer bodies are rejected before buffering.</summary>
    public const int MaxBodyLength = 65536;

    /// <summary>Maximum accepted file-path length.</summary>
    public const int MaxFilePathLength = 4096;

    /// <summary>Maximum accepted reply-to id length.</summary>
    public const int MaxReplyToIdLength = 256;

    /// <summary>Comment body.</summary>
    public string Body { get; init; }

    /// <summary>Repo-relative file path for a file-anchored review thread; null for a plain comment.</summary>
    public string? FilePath { get; init; }

    /// <summary>1-based line number for a file-anchored review thread; null for a plain comment.</summary>
    public int? Line { get; init; }

    /// <summary>Id of the comment or thread being replied to; null for a new thread.</summary>
    public string? ReplyToId { get; init; }

    /// <exception cref="ArgumentException">Thrown when the comment is invalid.</exception>
    public NewUpstreamComment(string body, string? filePath = null, int? line = null, string? replyToId = null)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("Comment body must not be empty.", nameof(body));
        if (body.Length > MaxBodyLength)
            throw new ArgumentException($"Comment body exceeds {MaxBodyLength} characters.", nameof(body));
        if (filePath is not null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path must not be blank when supplied.", nameof(filePath));
            if (filePath.Length > MaxFilePathLength)
                throw new ArgumentException($"File path exceeds {MaxFilePathLength} characters.", nameof(filePath));
            if (filePath.Contains('\0'))
                throw new ArgumentException("File path must not contain NUL.", nameof(filePath));
        }

        if (line.HasValue)
        {
            if (line.Value < 1)
                throw new ArgumentException("Line must be 1-based.", nameof(line));
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("A line-anchored comment requires a file path.", nameof(filePath));
        }

        if (replyToId is not null)
        {
            if (string.IsNullOrWhiteSpace(replyToId))
                throw new ArgumentException("Reply-to id must not be blank when supplied.", nameof(replyToId));
            if (replyToId.Length > MaxReplyToIdLength)
                throw new ArgumentException($"Reply-to id exceeds {MaxReplyToIdLength} characters.", nameof(replyToId));
        }

        Body = body;
        FilePath = filePath;
        Line = line;
        ReplyToId = replyToId;
    }
}

/// <summary>
/// Well-known webhook subscription scopes. Forges name these differently
/// (Gitea/Forgejo: repository, organisation, user, system; Azure DevOps:
/// service-hook subscriptions scoped to a project or organisation), so scope
/// travels as an opaque string: providers accept the values they understand
/// and report unsupported otherwise. These constants are the shared
/// vocabulary; a forge may additionally accept its own native scope names.
/// </summary>
public static class UpstreamWebhookScopes
{
    /// <summary>Events for one repository / project.</summary>
    public const string Repository = "repository";

    /// <summary>Events for an organisation (GitHub org, Gitea/Forgejo org, GitLab group, Azure organisation).</summary>
    public const string Organization = "organization";

    /// <summary>User-level events (Gitea/Forgejo user scope).</summary>
    public const string User = "user";

    /// <summary>System-wide events (Gitea/Forgejo system scope).</summary>
    public const string System = "system";
}

/// <summary>A webhook subscription on the forge.</summary>
public sealed record UpstreamWebhookSubscription
{
    /// <summary>Forge-assigned subscription id, as a string (forges differ: int, GUID).</summary>
    public required string Id { get; init; }

    /// <summary>Scope the subscription was created with (see <see cref="UpstreamWebhookScopes"/>).</summary>
    public required string Scope { get; init; }

    /// <summary>Forge-native event names this subscription fires on.</summary>
    public required IReadOnlyList<string> Events { get; init; }

    /// <summary>Delivery target URL.</summary>
    public required string TargetUrl { get; init; }
}

/// <summary>
/// A webhook subscription to create. The constructor rejects invalid states
/// (no events, bad target URL, blank scope) so providers receive only
/// well-formed requests. Event names are forge-native and passed through
/// untouched — callers must use the target forge's vocabulary.
/// </summary>
public sealed record NewUpstreamWebhookSubscription
{
    /// <summary>Maximum number of events per subscription.</summary>
    public const int MaxEvents = 64;

    /// <summary>Maximum accepted target-URL length.</summary>
    public const int MaxTargetUrlLength = 2048;

    /// <summary>Maximum accepted scope length.</summary>
    public const int MaxScopeLength = 128;

    /// <summary>Forge-native event names; passed through untouched.</summary>
    public IReadOnlyList<string> Events { get; init; }

    /// <summary>Absolute http(s) delivery target URL.</summary>
    public string TargetUrl { get; init; }

    /// <summary>Subscription scope (see <see cref="UpstreamWebhookScopes"/>).</summary>
    public string Scope { get; init; }

    /// <exception cref="ArgumentException">Thrown when the subscription is invalid.</exception>
    public NewUpstreamWebhookSubscription(IReadOnlyList<string> events, string targetUrl, string scope = UpstreamWebhookScopes.Repository)
    {
        if (events is null || events.Count == 0)
            throw new ArgumentException("At least one event is required.", nameof(events));
        if (events.Count > MaxEvents)
            throw new ArgumentException($"At most {MaxEvents} events are allowed.", nameof(events));
        if (events.Any(e => string.IsNullOrWhiteSpace(e)))
            throw new ArgumentException("Event names must not be blank.", nameof(events));
        if (string.IsNullOrWhiteSpace(targetUrl))
            throw new ArgumentException("Target URL must not be empty.", nameof(targetUrl));
        if (targetUrl.Length > MaxTargetUrlLength)
            throw new ArgumentException($"Target URL exceeds {MaxTargetUrlLength} characters.", nameof(targetUrl));
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Target URL must be an absolute http(s) URL.", nameof(targetUrl));
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("Scope must not be empty.", nameof(scope));
        if (scope.Length > MaxScopeLength)
            throw new ArgumentException($"Scope exceeds {MaxScopeLength} characters.", nameof(scope));

        Events = events;
        TargetUrl = targetUrl;
        Scope = scope;
    }
}

/// <summary>Well-known repository visibility values; providers pass through the forge's own level.</summary>
public static class UpstreamRepositoryVisibility
{
    /// <summary>Visible to anyone.</summary>
    public const string Public = "public";

    /// <summary>Visible to members/collaborators only.</summary>
    public const string Private = "private";

    /// <summary>Visible within the organisation or enterprise (GitHub internal, GitLab internal).</summary>
    public const string Internal = "internal";
}

/// <summary>
/// One branch protection rule, in forge-neutral terms: which branches it
/// applies to, how many approvals merge requires, and whether status checks
/// must pass. Forges without an equivalent field leave it at its default
/// rather than forcing a translation.
/// </summary>
public sealed record UpstreamBranchProtection
{
    /// <summary>Maximum accepted branch-pattern length.</summary>
    public const int MaxBranchPatternLength = 512;

    /// <summary>Branch name or pattern the rule applies to (e.g. "main", "release/*").</summary>
    public string BranchPattern { get; init; }

    /// <summary>Minimum approvals required; zero when the forge has no quorum rule.</summary>
    public int RequiredApprovalCount { get; init; }

    /// <summary>True when required status checks must pass before merge.</summary>
    public bool RequiresStatusChecks { get; init; }

    /// <exception cref="ArgumentException">Thrown when the rule is invalid.</exception>
    public UpstreamBranchProtection(string branchPattern, int requiredApprovalCount = 0, bool requiresStatusChecks = false)
    {
        if (string.IsNullOrWhiteSpace(branchPattern))
            throw new ArgumentException("Branch pattern must not be empty.", nameof(branchPattern));
        if (branchPattern.Length > MaxBranchPatternLength)
            throw new ArgumentException($"Branch pattern exceeds {MaxBranchPatternLength} characters.", nameof(branchPattern));
        if (requiredApprovalCount < 0)
            throw new ArgumentException("Required approval count must not be negative.", nameof(requiredApprovalCount));

        BranchPattern = branchPattern;
        RequiredApprovalCount = requiredApprovalCount;
        RequiresStatusChecks = requiresStatusChecks;
    }
}

/// <summary>
/// Repository metadata merge readiness genuinely depends on: default branch,
/// visibility and branch protection rules. Every field is optional — a forge
/// reports what it has and leaves the rest unset.
/// </summary>
public sealed record UpstreamRepositoryMetadata
{
    /// <summary>Default branch name (e.g. "main"); null when the forge does not report one.</summary>
    public string? DefaultBranch { get; init; }

    /// <summary>Visibility level (see <see cref="UpstreamRepositoryVisibility"/>); null when unknown.</summary>
    public string? Visibility { get; init; }

    /// <summary>Branch protection rules; empty when none or unreported.</summary>
    public IReadOnlyList<UpstreamBranchProtection> BranchProtections { get; init; } = [];
}

/// <summary>Input to <see cref="IUpstreamRemote.CompleteAsync"/>.</summary>
public sealed record UpstreamCompletionRequest
{
    public required string RepositoryId { get; init; }
    public required WorkItemId WorkItemId { get; init; }
    public required ProjectId ProjectId { get; init; }
    public required string WorkBranch { get; init; }
    public required string BaseBranch { get; init; }
    /// <summary>SHA produced by the local merge. Null when resuming past the merge phase.</summary>
    public string? MergeSha { get; init; }
    public required string Title { get; init; }
    /// <summary>Static fallback PR description. Used when LLM generation is disabled or fails.</summary>
    public string? Description { get; init; }
    /// <summary>git diff --stat output between base and work branches. Empty when unavailable.</summary>
    public string DiffStat { get; init; } = string.Empty;
    /// <summary>Full git diff between base and work branches. Empty when unavailable.</summary>
    public string FullDiff { get; init; } = string.Empty;
    /// <summary>Titles of audit findings the agent addressed across rework iterations.</summary>
    public IReadOnlyList<string> AddressedFindings { get; init; } = [];
    /// <summary>Original work item prompt, truncated to 2 KB. Null for legacy callers.</summary>
    public string? WorkItemPrompt { get; init; }
    /// <summary>Raw agent stdout. Used for AgentReasoningTail in LLM-generated descriptions. Null for legacy callers.</summary>
    public string? AgentStdout { get; init; }
    /// <summary>
    /// Full commit messages produced on the work branch (subjects plus bodies,
    /// oldest first), each pre-truncated to at most 2 KB with at most 20 entries.
    /// Forwarded to the description generator so both strategies see the agent's
    /// own per-commit summaries. Empty when unavailable; the upstream remote
    /// falls back to reading them from the host git repo.
    /// </summary>
    public IReadOnlyList<string> CommitMessages { get; init; } = [];
    /// <summary>
    /// Current work-item prompt revision at upstream time. Used only as a
    /// fallback for generated squash-merge trailers when the branch commits
    /// cannot be read from the forge.
    /// </summary>
    public int? PromptRevision { get; init; }
    /// <summary>Authenticated originator used for human attribution in delivery metadata.</summary>
    public WorkInitiator? Initiator { get; init; }
    /// <summary>
    /// Name of the environment variable holding the upstream credential (from
    /// <c>Upstream.TokenEnvVar</c> in the project config). Null when not configured.
    /// Plugin implementations read: <c>Environment.GetEnvironmentVariable(TokenEnvVar)</c>.
    /// </summary>
    public string? TokenEnvVar { get; init; }
    /// <summary>When true, merge the PR immediately after opening it.</summary>
    public bool AutoMerge { get; init; }
    /// <summary>Merge strategy: "merge", "squash", or "rebase". Matches <c>Upstream.MergeMethod</c>.</summary>
    public string MergeMethod { get; init; } = "merge";

    /// <summary>
    /// PR number already opened on the forge from a prior CompleteAsync attempt.
    /// When set, the implementation skips PR creation and proceeds directly to
    /// the merge step using this PR number. Used by the orchestrator's
    /// auto-merge race recovery (re-fetch base + re-run merge phase + retry
    /// merge against the still-open PR).
    /// </summary>
    public int? ExistingPullRequestNumber { get; init; }
}

/// <summary>Result of <see cref="IUpstreamRemote.CompleteAsync"/>.</summary>
public sealed record UpstreamCompletionOutcome
{
    /// <summary>True when the upstream kind has no push concept (noop).</summary>
    public bool Skipped { get; init; }
    /// <summary>True when a branch was successfully pushed to the remote.</summary>
    public bool BranchPushed { get; init; }
    /// <summary>URL of the pull request opened on the remote forge, if any.</summary>
    public string? PullRequestUrl { get; init; }
    /// <summary>Forge-assigned PR number, if a PR was opened.</summary>
    public int? PullRequestNumber { get; init; }
    /// <summary>SHA of the merge commit on the remote, if the PR was auto-merged.</summary>
    public string? MergedSha { get; init; }
    /// <summary>Diagnostic notes, populated on partial or graceful-degraded outcomes.</summary>
    public string? Notes { get; init; }
    /// <summary>
    /// True when auto-merge requested but the forge rejected the merge call
    /// with a "PR not mergeable" race (GitHub HTTP 405 on PUT /pulls/N/merge).
    /// The orchestrator treats this as a retryable race against upstream base
    /// motion: re-fetch base, re-run the merge phase, and retry the merge.
    /// </summary>
    public bool AutoMergeRaced { get; init; }
}
