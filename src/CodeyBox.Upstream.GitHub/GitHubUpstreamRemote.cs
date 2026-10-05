using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Git;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Upstream.GitHub;

/// <summary>
/// GitHub upstream remote. Phase 4 pushes the work branch to GitHub, opens a
/// pull request, and optionally auto-merges it — leaving an audit trail on the
/// forge rather than a silent base-branch update.
///
/// PAT security model (unchanged from the old push-only path):
///   - URL is bare https://github.com/owner/repo.git (no embedded token).
///   - GIT_ASKPASS points to a per-call script that reads the token from env.
///   - Token is set only as env var, never on argv or in config files.
///   - Token is scrubbed from any error message before it leaves this class.
///   - HTTP requests carry Authorization: token <PAT> as a request header;
///     the header is added per-request so the shared HttpClient is not mutated.
///
/// PR description:
///   When an <see cref="IPullRequestDescriptionGenerator"/> is supplied and
///   <see cref="PrDescriptionOptions.Enabled"/> is true, the PR body is
///   produced by the LLM generator rather than the static template.
///   On timeout or any generator failure the static template is used instead.
///   The generator call is bounded by <see cref="PrDescriptionOptions.Timeout"/>.
/// </summary>
public sealed class GitHubUpstreamRemote : IUpstreamRemote
{
    private readonly IGitHost _gitHost;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubUpstreamRemote> _log;
    private readonly GitHubUpstreamOptions _opts;
    private readonly IGitHubTokenProvider _tokenProvider;
    private readonly ITimingStore? _timings;
    private readonly IPullRequestDescriptionGenerator? _descriptionGenerator;

    public GitHubUpstreamRemote(
        IGitHost gitHost,
        IHttpClientFactory httpClientFactory,
        ILogger<GitHubUpstreamRemote> log,
        GitHubUpstreamOptions opts,
        ITimingStore? timings = null,
        IPullRequestDescriptionGenerator? descriptionGenerator = null)
    {
        _gitHost = gitHost;
        _httpClientFactory = httpClientFactory;
        _log = log;
        _opts = opts;
        _tokenProvider = opts.TokenProvider
            ?? (!string.IsNullOrEmpty(opts.Token)
                ? new FixedGitHubTokenProvider(opts.Token)
                : throw new ArgumentException("A GitHub token or token provider must be provided", nameof(opts)));
        _timings = timings;
        _descriptionGenerator = descriptionGenerator;
        if (!IsValidRemoteName(_opts.Owner))
            throw new ArgumentException($"GitHub Owner contains invalid characters: '{_opts.Owner}'", nameof(opts));
        if (!IsValidRemoteName(_opts.Repository))
            throw new ArgumentException($"GitHub Repository contains invalid characters: '{_opts.Repository}'", nameof(opts));
    }

    private static bool IsValidRemoteName(string name) =>
        !string.IsNullOrEmpty(name) &&
        !name.Contains('/') &&
        !name.Contains('?') &&
        !name.Contains('#') &&
        !name.Contains('%') &&
        !name.Contains("..") &&
        !name.Any(char.IsWhiteSpace);

    public string Name => "github";

    /// <summary>
    /// Legacy push path — kept for interface completeness. CompleteAsync is
    /// the primary path called by the orchestrator.
    /// </summary>
    public async Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
    {
        var url = RepoUrl();
        var token = await _tokenProvider.GetTokenAsync(ct);
        using var askpass = GitCredentialHelper.CreateAskPassFor(token, "x-access-token");
        try
        {
            await _gitHost.PushToUpstreamAsync(
                repositoryId,
                url,
                branch,
                askpass.Environment,
                ToReconcileStrategy(_opts.MergeMethod),
                ct);
            return new UpstreamPushResult(true, null);
        }
        catch (Exception ex)
        {
            var scrubbed = Scrub(ex.Message, token);
            return new UpstreamPushResult(false, scrubbed);
        }
    }

    /// <summary>
    /// Full GitHub completion flow:
    ///   1. Push work branch to GitHub.
    ///   2. Build PR description (LLM-generated or static fallback).
    ///   3. Open a PR (workBranch → baseBranch).
    ///   4. If AutoMerge=true, merge the PR via the GitHub API.
    ///
    /// Transient failures (network, unexpected HTTP errors) throw so the
    /// orchestrator can retry. Soft errors (422 PR already exists, 405 PR not
    /// mergeable) are logged and return a partial outcome without throwing.
    /// </summary>
    public async Task<UpstreamCompletionOutcome> CompleteAsync(UpstreamCompletionRequest request, CancellationToken ct = default)
    {
        // Reject branch names with whitespace or control characters to prevent log injection.
        // char.IsWhiteSpace alone misses non-whitespace control chars (\x01–\x08, \x0b–\x0c, \x0e–\x1f),
        // so we also check char.IsControl (excluding tab, which git allows in branch names).
        static bool HasInvalidChars(string s) =>
            s.Any(c => char.IsWhiteSpace(c) || (char.IsControl(c) && c != '\t'));

        if (string.IsNullOrEmpty(request.WorkBranch) || HasInvalidChars(request.WorkBranch))
            throw new ArgumentException(
                $"WorkBranch contains invalid characters (whitespace/control chars not allowed): '{SanitizeForLog(request.WorkBranch)}'",
                nameof(request));
        if (string.IsNullOrEmpty(request.BaseBranch) || HasInvalidChars(request.BaseBranch))
            throw new ArgumentException(
                $"BaseBranch contains invalid characters (whitespace/control chars not allowed): '{SanitizeForLog(request.BaseBranch)}'",
                nameof(request));

        // Step 1: push work branch. Work branches are CodeyBox-owned
        // (codeybox/*): a diverged remote is rewritten only under a
        // --force-with-lease bound to the sha CodeyBox last pushed, never by
        // rebasing the new head onto the stale tip (which would resurrect
        // superseded commits). A lease mismatch or an unrecorded history
        // surfaces as its own typed contract — not a merge conflict — so the
        // orchestrator parks for the operator instead of retrying or
        // reworking.
        var repoUrl = RepoUrl();
        var token = await _tokenProvider.GetTokenAsync(ct);
        using var askpass = GitCredentialHelper.CreateAskPassFor(token, "x-access-token");
        string? pushedSha;
        await using (var pushScope = await TimingScope.BeginAsync(
            _timings, request.WorkItemId, "upstream_push", "upstream.push_branch",
            log: _log))
        {
            try
            {
                pushedSha = await PushOwnedWorkBranchAsync(request, repoUrl, askpass.Environment, ct);
            }
            catch (Exception ex)
            {
                // Ownership/prefix guards fail fast and unwrapped: a refused
                // branch is a configuration error, never a push outcome, and
                // must stay distinguishable for the caller's tests and logs.
                if (ex is ArgumentException)
                    throw;

                // Preserve the typed reconcile-conflict contract across the
                // GitHub boundary so the pipeline can route into bounded
                // conflict rework instead of generic push retries. Only a
                // typed UpstreamPushReconcileConflictException in the chain
                // qualifies — arbitrary message text never does. The new
                // instance carries only validated, token-scrubbed branch and
                // strategy; the raw exception (which may echo credentials) is
                // never attached, logged, or serialized.
                if (TryBuildSafeReconcileConflict(ex, token, out var safeConflict))
                {
                    _log.LogDebug(
                        "Work-branch push to upstream hit reconcile conflict: {Message} (full exception withheld; may contain credentials)",
                        safeConflict.Message);
                    throw safeConflict;
                }

                // Lease mismatch and diverged-history are their own terminal
                // classifications: a third party moved the branch (park, do
                // not retry, do not rework) or CodeyBox has no recorded push
                // to lease from (park for operator re-drive). Rebuild without
                // the inner chain, which may echo credential material.
                if (UpstreamLeaseMismatchException.TryFindIn(ex, out var mismatch))
                    throw BuildSafeLeaseMismatch(mismatch, token);
                if (UpstreamOwnedBranchDivergedException.TryFindIn(ex, out var diverged))
                    throw BuildSafeDivergedHistory(diverged, token);

                // Log only the scrubbed message at Debug; the raw exception object is
                // withheld because git can echo credential material on auth failures.
                var scrubbed = Scrub(ex.Message, token);
                _log.LogDebug("Work-branch push to upstream threw: {Message} (full exception withheld; may contain credentials)", scrubbed);
                throw new InvalidOperationException($"Failed to push work branch '{SanitizeForLog(request.WorkBranch)}': {scrubbed}");
            }
        }

        // Step 2 + 3: build PR description and open PR (or reuse a PR from a
        // prior race-recovery attempt). When the orchestrator's auto-merge race
        // recovery re-runs CompleteAsync, it passes the PR number from the
        // first attempt so we skip create (which would 422) and go straight to
        // the merge call.
        int prNumber;
        string? prHtmlUrl;
        var prTitle = BuildPrTitle(request.Title, request.WorkBranch);
        PrDescriptionResult? prDescription = null;
        if (request.ExistingPullRequestNumber is { } existingPr)
        {
            prNumber = existingPr;
            prHtmlUrl = $"https://github.com/{_opts.Owner}/{_opts.Repository}/pull/{existingPr}";
            // The resumed PR may have been closed or merged while the item was
            // parked (retry-after-stale): re-read its authoritative state
            // before touching the merge API so a stale open assumption never
            // drives a doomed merge call. A legacy host reports no pushed tip
            // (pushedSha null), so revision proof is impossible — keep the
            // historical proceed-to-merge with the recorded number instead of
            // refusing a path that predates lease tracking.
            if (pushedSha is not null)
            {
                var resumed = await ReconcileResumedPullRequestAsync(request, existingPr, pushedSha, prTitle, ct);
                if (resumed.MergedSha is not null)
                {
                    return new UpstreamCompletionOutcome
                    {
                        BranchPushed = true,
                        PushedWorkBranchSha = pushedSha,
                        PullRequestUrl = resumed.PrHtmlUrl,
                        PullRequestNumber = resumed.PrNumber,
                        MergedSha = resumed.MergedSha,
                        Notes = resumed.Notes,
                    };
                }
                prNumber = resumed.PrNumber;
                prHtmlUrl = resumed.PrHtmlUrl;
                prDescription = resumed.PrDescription;
                if (IsSquashMerge(_opts.MergeMethod) && prDescription is null && !resumed.RecoveredFresh)
                {
                    // Reuse the title/body already fetched during resumed-PR
                    // reconciliation. No second fetch: reconciliation attempted
                    // it once, and a missing body then means "use local data".
                    if (!string.IsNullOrWhiteSpace(resumed.ExistingTitle))
                        prTitle = resumed.ExistingTitle;
                    if (!string.IsNullOrWhiteSpace(resumed.ExistingBody))
                    {
                        prDescription = new PrDescriptionResult(
                            resumed.ExistingBody,
                            Generated: ShouldReuseExistingPrBodyAsGenerated(resumed.ExistingBody, request));
                    }
                }
            }
            else if (IsSquashMerge(_opts.MergeMethod) && prDescription is null)
            {
                // Legacy host (no pushed tip, no reconciliation): the
                // historical single detail fetch for squash-message
                // composition. Unfetchable means local-data fallback.
                var existing = await TryFetchPullRequestAsync(prNumber, ct);
                if (existing is not null)
                {
                    if (!string.IsNullOrWhiteSpace(existing.HtmlUrl))
                        prHtmlUrl = existing.HtmlUrl;
                    if (!string.IsNullOrWhiteSpace(existing.Title))
                        prTitle = existing.Title;
                    if (!string.IsNullOrWhiteSpace(existing.Body))
                        prDescription = new PrDescriptionResult(
                            existing.Body,
                            Generated: ShouldReuseExistingPrBodyAsGenerated(existing.Body, request));
                }
            }
        }
        else
        {
            prDescription = await BuildDescriptionAsync(request, ct);
            ReconciledPullRequest pr;
            await using (var createPrScope = await TimingScope.BeginAsync(
                _timings, request.WorkItemId, "upstream_push", "upstream.api_create_pr",
                log: _log))
            {
                pr = await CreateOrReconcilePullRequestAsync(request, prTitle, prDescription.Body, ct);
            }

            if (pr.ReusedExisting)
                _log.LogInformation("GitHub PR #{N} reused after create conflict: {Url}", pr.Number, pr.HtmlUrl);
            else
                _log.LogInformation("GitHub PR opened: {Url}", pr.HtmlUrl);
            AuditLog.UpstreamPrOpened(pr.Number, pr.HtmlUrl, request.WorkBranch, request.BaseBranch);

            if (pr.HtmlUrl is null)
                _log.LogWarning("GitHub PR response did not include html_url; pull_request_opened webhook event will not fire");

            prNumber = pr.Number;
            prHtmlUrl = pr.HtmlUrl;

            if (pr.AuthoritativeMergeSha is not null)
            {
                // The exact PR for this work is already merged on the forge —
                // proven by head-sha match against the just-pushed tip, never by
                // branch-name reuse. No merge call is needed; the remote merge
                // commit is the delivery and MergedSha carries the forge-side
                // sha (never the local squash sha) so monitoring code resolves
                // it on the commits API.
                _log.LogInformation("GitHub PR #{N} already merged: {Sha}", prNumber, pr.AuthoritativeMergeSha);
                AuditLog.UpstreamPrMerged(prNumber, pr.AuthoritativeMergeSha);
                return new UpstreamCompletionOutcome
                {
                    BranchPushed = true,
                    PushedWorkBranchSha = pushedSha,
                    PullRequestUrl = prHtmlUrl,
                    PullRequestNumber = prNumber,
                    MergedSha = pr.AuthoritativeMergeSha,
                    Notes = "PR already merged on the forge for the exact pushed revision; reused existing merge",
                };
            }
        }

        if (!_opts.AutoMerge)
        {
            return new UpstreamCompletionOutcome
            {
                BranchPushed = true,
                PushedWorkBranchSha = pushedSha,
                PullRequestUrl = prHtmlUrl,
                PullRequestNumber = prNumber,
            };
        }

        // Step 4: auto-merge
        string? mergedSha;
        string? mergeNotes;
        bool autoMergeRaced;
        await using (var mergeScope = await TimingScope.BeginAsync(
            _timings, request.WorkItemId, "upstream_push", "upstream.api_merge_pr",
            log: _log))
        {
            (mergedSha, mergeNotes, autoMergeRaced) = await MergePullRequestAsync(
                prNumber, prTitle, prDescription, request, ct);
        }

        if (mergedSha is not null)
        {
            _log.LogInformation("GitHub PR #{N} auto-merged: {Sha}", prNumber, mergedSha);
            AuditLog.UpstreamPrMerged(prNumber, mergedSha);
        }

        return new UpstreamCompletionOutcome
        {
            BranchPushed = true,
            PushedWorkBranchSha = pushedSha,
            PullRequestUrl = prHtmlUrl,
            PullRequestNumber = prNumber,
            MergedSha = mergedSha,
            Notes = mergeNotes,
            AutoMergeRaced = autoMergeRaced,
        };
    }

    /// <summary>
    /// Enumerates open pull requests in this repo whose head branch starts
    /// with <paramref name="branchPrefix"/> and whose mergeability has been
    /// computed by GitHub. Used by the stale-base PR sweeper.
    ///
    /// <para>GitHub computes <c>mergeable</c> asynchronously after each push
    /// or base-branch motion: the field is <c>null</c> while the calculation
    /// is in flight. PRs in that "unknown" window are skipped so the sweeper
    /// reconsiders them on the next tick — never report them as either
    /// mergeable or conflicted from stale data.</para>
    ///
    /// <para>The forge call uses the same <c>github-upstream</c> HttpClient
    /// and PAT as the rest of this class; failures throw so the caller can
    /// log and back off.</para>
    /// </summary>
    public async Task<IReadOnlyList<UpstreamPullRequest>> ListOpenPullRequestsAsync(
        string branchPrefix, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(branchPrefix))
            throw new ArgumentException("branchPrefix must be non-empty", nameof(branchPrefix));

        var listed = new List<UpstreamPullRequest>();
        // Page through /pulls?state=open until the first page returns fewer
        // than per_page entries. Cap to a sane safety limit so a broken/very
        // large repo cannot stall the sweep indefinitely.
        const int perPage = 100;
        const int maxPages = 10;
        for (var page = 1; page <= maxPages; page++)
        {
            var url = $"https://api.github.com/repos/{_opts.Owner}/{_opts.Repository}/pulls" +
                $"?state=open&per_page={perPage}&page={page}";
            using var listReq = await BuildRequestAsync(HttpMethod.Get, url, ct);
            using var listResp = await SendAsync(listReq, ct);
            if (!listResp.IsSuccessStatusCode)
            {
                AuditLog.UpstreamApiCallFailed("GET /pulls", (int)listResp.StatusCode, _opts.Owner, _opts.Repository);
                listResp.EnsureSuccessStatusCode();
            }
            var summaries = await listResp.Content.ReadFromJsonAsync<GitHubPrSummary[]>(ct);
            if (summaries is null || summaries.Length == 0) break;

            foreach (var summary in summaries)
            {
                var headRef = summary.Head?.Ref;
                if (string.IsNullOrEmpty(headRef)) continue;
                if (!headRef.StartsWith(branchPrefix, StringComparison.Ordinal)) continue;

                // /pulls (list) returns a thin object without `mergeable`;
                // we have to fetch the full PR detail to read it. Restricting
                // the fetch to PRs whose head matches the prefix keeps the
                // per-sweep API-call count proportional to the
                // CodeyBox-authored PR set rather than the full open-PR set.
                var detail = await FetchPullRequestDetailAsync(summary.Number, ct);
                if (detail is null) continue;

                // `mergeable` null means GitHub is still computing — skip.
                // The sweeper will reconsider on the next tick.
                if (detail.Mergeable is null) continue;

                var hasConflict = detail.Mergeable == false
                    || string.Equals(detail.MergeableState, "dirty", StringComparison.Ordinal);

                listed.Add(new UpstreamPullRequest
                {
                    Number = detail.Number,
                    Url = detail.HtmlUrl ?? $"https://github.com/{_opts.Owner}/{_opts.Repository}/pull/{detail.Number}",
                    HeadBranch = headRef,
                    HeadSha = summary.Head?.Sha ?? string.Empty,
                    BaseBranch = summary.Base?.Ref ?? string.Empty,
                    HasMergeConflict = hasConflict,
                });
            }

            if (summaries.Length < perPage) break;
        }
        return listed;
    }

    private async Task<GitHubPrDetailMergeable?> FetchPullRequestDetailAsync(int number, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{_opts.Owner}/{_opts.Repository}/pulls/{number}";
        using var req = await BuildRequestAsync(HttpMethod.Get, url, ct);
        using var resp = await SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            AuditLog.UpstreamApiCallFailed($"GET /pulls/{number}", (int)resp.StatusCode, _opts.Owner, _opts.Repository);
            return null;
        }
        return await resp.Content.ReadFromJsonAsync<GitHubPrDetailMergeable>(ct);
    }

    public async Task<UpstreamPullRequestState?> GetPullRequestAsync(
        int number,
        CancellationToken ct = default)
    {
        if (number <= 0)
            throw new ArgumentOutOfRangeException(nameof(number), "Pull request number must be positive.");

        var detail = await FetchPullRequestDetailAsync(number, ct);
        if (detail is null) return null;

        var status = detail.Merged
            ? PullRequestStatus.Merged
            : string.Equals(detail.State, "closed", StringComparison.OrdinalIgnoreCase)
                ? PullRequestStatus.Closed
                : PullRequestStatus.Open;
        return new UpstreamPullRequestState(
            detail.Number,
            detail.HtmlUrl ?? $"https://github.com/{_opts.Owner}/{_opts.Repository}/pull/{detail.Number}",
            status,
            detail.Merged ? detail.MergeCommitSha : null);
    }

    /// <summary>
    /// Fetches the current head of <paramref name="baseBranch"/> from this
    /// upstream GitHub repo into the host bare repo, overwriting the local
    /// ref. Returns the new sha. The orchestrator calls this on auto-merge
    /// 405 (race against upstream base motion) to decide whether the race
    /// is real (base sha changed → re-run merge phase) or a different kind
    /// of unmergeability (base unchanged → branch protection etc.).
    /// </summary>
    public async Task<string?> FetchBaseBranchAsync(string repositoryId, string baseBranch, CancellationToken ct = default)
    {
        // Reject control/whitespace in the branch name — same defence as the
        // CompleteAsync branch-name guard. A clean string is required because
        // we'll embed it in a refspec passed to git via Process argv.
        static bool HasInvalidChars(string s) =>
            s.Any(c => char.IsWhiteSpace(c) || (char.IsControl(c) && c != '\t'));
        if (string.IsNullOrEmpty(baseBranch) || HasInvalidChars(baseBranch))
            throw new ArgumentException(
                $"baseBranch contains invalid characters (whitespace/control chars not allowed): '{SanitizeForLog(baseBranch)}'",
                nameof(baseBranch));

        var repoUrl = RepoUrl();
        var token = await _tokenProvider.GetTokenAsync(ct);
        using var askpass = GitCredentialHelper.CreateAskPassFor(token, "x-access-token");
        return await _gitHost.FetchUpstreamBranchAsync(repositoryId, repoUrl, baseBranch, askpass.Environment, ct);
    }

    /// <summary>
    /// Uses the GitHub Merges API to merge <paramref name="sourceBranch"/> into
    /// <paramref name="targetBranch"/>. Returns false on 409 Conflict; true on
    /// 201 (merge commit created) or 204 (already up-to-date).
    /// </summary>
    public async Task<bool> TryMergeUpstreamBranchAsync(string targetBranch, string sourceBranch, CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{_opts.Owner}/{_opts.Repository}/merges";
        var body = new GitHubMergesRequest(targetBranch, sourceBranch,
            $"chore: sync {sourceBranch} into {targetBranch}");

        using var req = await BuildRequestAsync(HttpMethod.Post, url, ct);
        req.Content = JsonContent.Create(body);

        using var response = await SendAsync(req, ct);

        if (response.StatusCode == HttpStatusCode.Conflict) return false;
        if (response.StatusCode == HttpStatusCode.NoContent) return true; // already up-to-date
        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>
    /// Creates a lightweight tag at <paramref name="sha"/> and publishes a GitHub
    /// release via POST /repos/{owner}/{repo}/releases. GitHub creates the tag
    /// object automatically when <c>tag_name</c> does not yet exist.
    /// Returns the HTML URL of the created release, or null on 422 (already exists).
    /// Throws on unexpected HTTP failures so the caller can decide how to handle.
    /// </summary>
    public async Task<string?> CreateTagAndReleaseAsync(string tagName, string sha, string? releaseNotes, CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{_opts.Owner}/{_opts.Repository}/releases";
        var body = new GitHubCreateReleaseRequest(tagName, sha, tagName, releaseNotes ?? string.Empty);

        using var req = await BuildRequestAsync(HttpMethod.Post, url, ct);
        req.Content = JsonContent.Create(body);

        using var response = await SendAsync(req, ct);

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            _log.LogWarning("GitHub POST /releases returned 422 for tag {Tag}; release may already exist", tagName);
            return null;
        }

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<GitHubReleaseResponse>(ct);
        _log.LogInformation("GitHub release created for tag {Tag}: {Url}", tagName, result?.HtmlUrl);
        return result?.HtmlUrl;
    }

    // -------------------------------------------------------------------------
    // Description generation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Attempts LLM-generated description; falls back to the static template
    /// from <see cref="UpstreamCompletionRequest.Description"/> on any failure.
    /// Appends the standard CodeyBox footer to whichever body is used.
    /// Never throws — generator failures are warnings, not errors.
    /// </summary>
    private async Task<PrDescriptionResult> BuildDescriptionAsync(UpstreamCompletionRequest request, CancellationToken ct)
    {
        var staticBody = request.Description ?? string.Empty;
        var workItemId = request.WorkItemId.ToString();

        if (_descriptionGenerator is null || !_opts.PrDescription.Enabled)
            return new PrDescriptionResult(
                PrDescriptionBody.BuildStaticBody(workItemId, request.DiffStat, staticBody) + BuildFooter(request),
                Generated: false);

        try
        {
            using var genCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            genCts.CancelAfter(_opts.PrDescription.Timeout);

            // Prefer raw agent stdout over the formatted static body for the reasoning tail.
            var agentTailRaw = ExtractAgentReasoningTail(request.AgentStdout ?? request.Description);
            var agentTail = agentTailRaw is null ? null : RawOutputRedactor.Redact(agentTailRaw);
            // Redact before sending to LLM — diff may contain accidentally-committed tokens.
            // Truncation to MaxDiffBytes is applied inside GenerateAsync.
            var redactedDiff = RawOutputRedactor.Redact(request.FullDiff);
            // Cap DiffStat at 4 KB; large changesets can produce hundreds of KB of stat output.
            var redactedStat = RawOutputRedactor.TruncateToBytes(RawOutputRedactor.Redact(request.DiffStat), 4096);
            // Truncate prompt using UTF-8 byte count to honour the documented 2 KB cap.
            var redactedPrompt = RawOutputRedactor.Redact(
                RawOutputRedactor.TruncateToBytes(request.WorkItemPrompt ?? string.Empty, 2048));
            var commitMessages = await ResolveCommitMessagesAsync(request, ct).ConfigureAwait(false);

            var genRequest = new PullRequestDescriptionRequest
            {
                DiffSummary = redactedStat,
                FullDiff = redactedDiff,
                Title = request.Title,
                Prompt = redactedPrompt,
                AddressedFindings = request.AddressedFindings,
                CommitMessages = commitMessages,
                AgentReasoningTail = agentTail,
            };

            var generationTask = _descriptionGenerator.GenerateAsync(genRequest, genCts.Token);
            var generated = await generationTask.WaitAsync(_opts.PrDescription.Timeout, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(generated))
                throw new InvalidOperationException("PR description generator returned no output");
            // Redact the generated body — the LLM may echo secrets from the diff.
            generated = RawOutputRedactor.Redact(generated);
            _log.LogInformation("LLM-generated PR description produced ({Chars} chars)", generated.Length);
            // The generated prose never stands alone: deterministic facts (work
            // item id, changed-file list) stay adjacent and the generated
            // section is marked as machine-generated. The same generated body
            // feeds the squash commit message, so the merge commit carries
            // real content rather than a static fallback.
            var body = PrDescriptionBody.BuildGeneratedBody(workItemId, redactedStat, generated);
            return new PrDescriptionResult(body + BuildFooter(request), Generated: true);
        }
        catch (TimeoutException)
        {
            _log.LogWarning("PR description generation timed out after {Timeout}; using static template",
                _opts.PrDescription.Timeout);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("PR description generation timed out after {Timeout}; using static template",
                _opts.PrDescription.Timeout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning("PR description generation failed ({Message}); using static template", ex.Message);
        }

        return new PrDescriptionResult(
            PrDescriptionBody.BuildStaticBody(workItemId, request.DiffStat, staticBody) + BuildFooter(request),
            Generated: false);
    }

    /// <summary>
    /// Best-effort agent commit messages for the generation prompt: prefers the
    /// messages the orchestrator attached to the request, otherwise reads them
    /// from the host git repo. Each entry is redacted and capped at 2 KB with
    /// at most 20 entries. Never throws — failures yield an empty list and the
    /// generator falls back to the diff alone.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveCommitMessagesAsync(
        UpstreamCompletionRequest request, CancellationToken ct)
    {
        const int maxMessages = 20;
        const int maxMessageBytes = 2048;

        IReadOnlyList<string> messages = request.CommitMessages;
        if (messages.Count == 0)
        {
            try
            {
                messages = await _gitHost.GetCommitMessagesAsync(
                    request.RepositoryId, request.BaseBranch, request.WorkBranch, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogDebug("Could not read commit messages for PR description: {Message}", ex.Message);
                return [];
            }
        }

        var capped = new List<string>(Math.Min(messages.Count, maxMessages));
        foreach (var message in messages.Take(maxMessages))
        {
            if (string.IsNullOrWhiteSpace(message)) continue;
            capped.Add(RawOutputRedactor.Redact(
                RawOutputRedactor.TruncateToBytes(message, maxMessageBytes)));
        }
        return capped;
    }

    private sealed record PrDescriptionResult(string Body, bool Generated);

    private bool ShouldReuseExistingPrBodyAsGenerated(string body, UpstreamCompletionRequest request)
    {
        if (!_opts.PrDescription.Enabled)
            return false;

        return !LooksLikeStaticFallbackPrBody(body, request.Description);
    }

    private static bool LooksLikeStaticFallbackPrBody(string body, string? staticDescription)
    {
        var strippedBody = StripPrFooter(body);
        if (strippedBody.Length == 0)
            return false;

        // Generated bodies carry the machine-generated marker alongside the
        // deterministic facts — they are never static, even though they share
        // the "Automated via CodeyBox" header.
        if (strippedBody.Contains(PrDescriptionBody.MachineGeneratedMarker, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(staticDescription) &&
            string.Equals(StripChangedFilesSection(strippedBody), StripPrFooter(staticDescription), StringComparison.Ordinal))
            return true;

        return strippedBody.StartsWith("Automated via CodeyBox", StringComparison.Ordinal) ||
            strippedBody.Contains("Untrusted agent output", StringComparison.Ordinal);
    }

    /// <summary>
    /// Removes the deterministic "Changed files" section the static renderer
    /// appends, so a static body still compares equal to the template it was
    /// built from when deciding whether an existing PR body is generated.
    /// </summary>
    private static string StripChangedFilesSection(string text)
    {
        var index = text.IndexOf("\nChanged files:\n", StringComparison.Ordinal);
        return index < 0 ? text : text[..index].TrimEnd();
    }

    private static string StripPrFooter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var footerIndex = normalized.IndexOf("\n\n---\n*Co-Authored-By:", StringComparison.Ordinal);
        if (footerIndex >= 0)
            normalized = normalized[..footerIndex];

        return normalized.Trim();
    }

    /// <summary>Returns the last 2 KB of <paramref name="text"/> (raw agent stdout or fallback).</summary>
    private static string? ExtractAgentReasoningTail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        const int maxTailChars = 2048;
        return text.Length <= maxTailChars ? text : text[^maxTailChars..];
    }

    // Standard footer appended to every PR body (LLM-generated or static).
    // The Co-Authored-By trailer identifies CodeyBox as a co-author on the
    // forge side; the 🤖 line links back to the platform for operators.
    private const string PrFooter = "\n\n---\n*Co-Authored-By: CodeyBox <noreply@codeybox.invalid>*  \n🤖 Generated with [CodeyBox](https://codeybox.invalid)";

    private static string BuildFooter(UpstreamCompletionRequest request)
    {
        if (request.Initiator is null)
            return PrFooter;
        var github = request.Initiator.FindProvider("github");
        var attribution = github is not null && GitHubIdentity.IsValidLogin(github.Login)
            ? $"@{github.Login}"
            : EscapeMarkdown(request.Initiator.DisplayName);
        return $"\n\nInitiated by {attribution}{PrFooter}";
    }

    private static string EscapeMarkdown(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("@", "\\@", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);

    private static readonly Regex CollapseWhitespace = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PullRequestNumberOnly = new(@"^\(#\d+\)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PromptRevisionTrailer = new(
        @"(?im)^\s*\*?CodeyBox-Prompt-Revision\s*:\s*(\d+)\s*\*?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex KnownTrailerLine = new(
        @"^\*?(?:CodeyBox-[A-Za-z0-9-]+|Co-Authored-By)\s*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ChecklistPrefix = new(
        @"^\s*[-*+]\s+\[[ xX]\]\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BulletPrefix = new(
        @"^\s*[-*+]\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NumberedListPrefix = new(
        @"^\s*\d+[.)]\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MarkdownLink = new(
        @"\[([^\]]+)\]\([^)]+\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ConventionalSubjectPrefix = new(
        @"^(?:feat|fix|chore|docs|test|refactor|perf|build|ci|style|revert)(?:\([^)]+\))?!?:\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PureReworkOrAuditSubject = new(
        @"^(?:fix|address|resolve|rework)\s+(?:audit|auditor|review|reviewer|findings?|feedback)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CiSkipDirective = new(
        @"\[(?:skip ci|ci skip|no ci|skip actions|actions skip)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SkipChecksTrailerLine = new(
        @"^\s*\*?skip-checks\s*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ExcessBlankLines = new(
        @"\n{3,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly (string Prefix, string Replacement)[] ImperativePrefixes =
    [
        ("This pull request adds ", "Add "),
        ("This pull request updates ", "Update "),
        ("This pull request changes ", "Change "),
        ("This pull request fixes ", "Fix "),
        ("This pull request removes ", "Remove "),
        ("This PR adds ", "Add "),
        ("This PR updates ", "Update "),
        ("This PR changes ", "Change "),
        ("This PR fixes ", "Fix "),
        ("This PR removes ", "Remove "),
        ("This change adds ", "Add "),
        ("This change updates ", "Update "),
        ("This change changes ", "Change "),
        ("This change fixes ", "Fix "),
        ("This change removes ", "Remove "),
        ("Adds ", "Add "),
        ("Updates ", "Update "),
        ("Changes ", "Change "),
        ("Fixes ", "Fix "),
        ("Removes ", "Remove "),
        ("Introduces ", "Introduce "),
        ("Implements ", "Implement "),
        ("Refactors ", "Refactor "),
        ("Ships ", "Ship "),
    ];

    // -------------------------------------------------------------------------
    // GitHub API helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pushes a CodeyBox-owned work branch without ever rebasing or merging
    /// the stale remote tip into the new head: the new head was already
    /// composed on a fresh base by the merge phase, so the only obstacle a
    /// diverged remote presents is the previous attempt's ref. Returns the
    /// tip sha actually pushed (for the orchestrator's last-pushed record).
    /// </summary>
    private async Task<string?> PushOwnedWorkBranchAsync(
        UpstreamCompletionRequest request,
        string repoUrl,
        IReadOnlyDictionary<string, string> upstreamEnv,
        CancellationToken ct)
    {
        // No-git guards first: ownership and the work==base refusal are pure
        // string checks, so a misconfigured branch fails before any git call.
        // The exact-prefix ownership test below (plus the re-check inside the
        // lease push itself) is the req-5 enforcement: base branches and
        // non-codeybox branches can never reach a force-push.
        var owned = CodeyBoxBranchPolicy.IsOwnedWorkBranch(request.WorkBranch);
        if (owned && string.Equals(request.WorkBranch, request.BaseBranch, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Refusing to push work branch '{SanitizeForLog(request.WorkBranch)}': it is the base branch, which is never force-pushed.",
                nameof(request));

        string? localTip;
        try
        {
            localTip = await _gitHost.ResolveCommitAsync(request.RepositoryId, request.WorkBranch, ct);
        }
        catch (NotSupportedException)
        {
            // Legacy host without tip resolution: keep the historical
            // plain-push path. The pushed tip is unknowable here, so no
            // lease base is reported and a retry cannot claim the lease —
            // it degrades to the same plain push, never a blind rewrite.
            await _gitHost.PushToUpstreamAsync(
                request.RepositoryId, repoUrl, request.WorkBranch, upstreamEnv,
                ToReconcileStrategy(request.MergeMethod), ct);
            return null;
        }
        if (string.IsNullOrWhiteSpace(localTip))
            throw new InvalidOperationException(
                $"Work branch '{SanitizeForLog(request.WorkBranch)}' does not resolve to a commit in the host repo; nothing to push.");

        if (!owned)
        {
            // Not CodeyBox-owned: keep the historical plain-push-with-reconcile
            // path. Reconcile rebases/merges but never force-pushes, so
            // foreign history is preserved and the prefix guard above holds.
            await _gitHost.PushToUpstreamAsync(
                request.RepositoryId, repoUrl, request.WorkBranch, upstreamEnv,
                ToReconcileStrategy(request.MergeMethod), ct);
            return localTip;
        }

        string? remoteSha;
        try
        {
            remoteSha = await _gitHost.GetUpstreamBranchShaAsync(
                request.RepositoryId, repoUrl, request.WorkBranch, upstreamEnv, ct);
        }
        catch (NotSupportedException)
        {
            remoteSha = null;
        }
        if (string.IsNullOrWhiteSpace(remoteSha))
            remoteSha = null;

        if (remoteSha is null)
        {
            await _gitHost.PushToUpstreamAsync(
                request.RepositoryId, repoUrl, request.WorkBranch, upstreamEnv,
                ToReconcileStrategy(request.MergeMethod), ct);
            return localTip;
        }

        if (string.Equals(remoteSha, localTip, StringComparison.OrdinalIgnoreCase))
            return localTip;

        bool fastForward;
        try
        {
            fastForward = await _gitHost.IsAncestorAsync(
                request.RepositoryId, remoteSha, localTip, ct);
        }
        catch (Exception ex) when (ex is NotSupportedException || ex is InvalidOperationException)
        {
            // Conservative: the host cannot prove a fast-forward (no ancestry
            // inspection, or the stale remote tip is not even present in the
            // local repo — the normal shape after a retry on a fresh base).
            // Fall through to the diverged path, where the lease — never a
            // blind rewrite — decides.
            _log.LogDebug(
                "Could not prove fast-forward for '{Branch}' ({Message}); treating as diverged under lease rules",
                SanitizeForLog(request.WorkBranch), ex.Message);
            fastForward = false;
        }
        if (fastForward)
        {
            await _gitHost.PushToUpstreamAsync(
                request.RepositoryId, repoUrl, request.WorkBranch, upstreamEnv,
                ToReconcileStrategy(request.MergeMethod), ct);
            return localTip;
        }

        var expected = request.ExpectedRemoteHeadSha?.Trim();
        if (string.IsNullOrWhiteSpace(expected))
            throw new UpstreamOwnedBranchDivergedException(request.WorkBranch, remoteSha, localTip);
        if (!string.Equals(expected, remoteSha, StringComparison.OrdinalIgnoreCase))
            throw await BuildLeaseMismatchWithActualAsync(
                request, expected, remoteSha, upstreamEnv, ct).ConfigureAwait(false);

        try
        {
            await _gitHost.PushBranchWithLeaseAsync(
                request.RepositoryId, repoUrl, request.WorkBranch, remoteSha, upstreamEnv, ct);
        }
        catch (UpstreamLeaseMismatchException ex)
        {
            // The remote moved between the observation and the guarded push —
            // re-observe best-effort so the park message names the actual tip.
            throw await BuildLeaseMismatchWithActualAsync(
                request, ex.ExpectedSha, ex.ActualSha, upstreamEnv, ct).ConfigureAwait(false);
        }

        _log.LogInformation(
            "Work branch {WorkBranch} rewrote its previous upstream push under lease ({Old} → {New})",
            request.WorkBranch, remoteSha, localTip);
        return localTip;
    }

    /// <summary>
    /// Best-effort enrichment of a lease mismatch with the currently observed
    /// remote tip. Never throws: observation failures keep the last known
    /// actual sha (possibly unknown).
    /// </summary>
    private async Task<UpstreamLeaseMismatchException> BuildLeaseMismatchWithActualAsync(
        UpstreamCompletionRequest request,
        string expectedSha,
        string? lastActualSha,
        IReadOnlyDictionary<string, string> upstreamEnv,
        CancellationToken ct)
    {
        var actual = lastActualSha;
        try
        {
            var reobserved = await _gitHost.GetUpstreamBranchShaAsync(
                request.RepositoryId, RepoUrl(), request.WorkBranch, upstreamEnv, ct);
            if (!string.IsNullOrWhiteSpace(reobserved))
                actual = reobserved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogDebug("Could not re-observe remote tip of '{Branch}' after lease failure: {Message}",
                SanitizeForLog(request.WorkBranch), ex.Message);
        }
        return new UpstreamLeaseMismatchException(request.WorkBranch, expectedSha, actual);
    }

    /// <summary>
    /// Re-reads a resumed PR's authoritative state. A PR that was merged
    /// while the item was parked proves delivery only by head-sha match; a
    /// PR that was closed triggers the fresh-PR recovery (open a new PR for
    /// the pushed revision, comment on and close the stale one with a link).
    /// An unreadable PR falls back to the historical proceed-to-merge path so
    /// a transient GET never blocks delivery.
    /// </summary>
    private sealed record ResumedPullRequest(
        int PrNumber,
        string? PrHtmlUrl,
        PrDescriptionResult? PrDescription,
        string? MergedSha,
        string? Notes,
        bool RecoveredFresh,
        string? ExistingTitle = null,
        string? ExistingBody = null);

    private async Task<ResumedPullRequest> ReconcileResumedPullRequestAsync(
        UpstreamCompletionRequest request,
        int existingPr,
        string pushedSha,
        string prTitle,
        CancellationToken ct)
    {
        // Single authoritative GET /pulls/{n} (TryFetch projection, extended
        // with lifecycle/head fields): proves delivery, detects a
        // closed-without-merge PR, and supplies title/body for squash-message
        // composition. The pre-existing request sequence (detail → commits →
        // merge) is preserved — no second fetch.
        var fallbackUrl = $"https://github.com/{_opts.Owner}/{_opts.Repository}/pull/{existingPr}";
        var fetched = await TryFetchPullRequestAsync(existingPr, ct);
        if (fetched is null)
        {
            // Unfetchable: unprovable, never assumed — proceed to merge with
            // the recorded number and local description, the historical shape.
            _log.LogWarning(
                "Resumed GitHub PR #{N} could not be fetched; proceeding to merge with the recorded number",
                existingPr);
            return new ResumedPullRequest(existingPr, fallbackUrl, null, null, null, false);
        }

        if (fetched.Merged)
        {
            if (!string.IsNullOrWhiteSpace(fetched.MergeCommitSha)
                && string.Equals(fetched.Head?.Sha, pushedSha, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("GitHub PR #{N} already merged: {Sha}", existingPr, fetched.MergeCommitSha);
                AuditLog.UpstreamPrMerged(existingPr, fetched.MergeCommitSha);
                return new ResumedPullRequest(
                    existingPr,
                    fetched.HtmlUrl ?? fallbackUrl,
                    null,
                    fetched.MergeCommitSha,
                    "Resumed PR already merged on the forge for the pushed revision; reused existing merge",
                    false);
            }
            throw new InvalidOperationException(
                $"GitHub PR #{existingPr} is merged but its head '{fetched.Head?.Sha ?? "(unknown)"}' does not match " +
                $"the pushed revision '{pushedSha}'; refusing to claim this work as delivered. Resolve manually.");
        }

        if (string.Equals(fetched.State, "closed", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning(
                "Resumed GitHub PR #{N} is closed without merge; opening a fresh PR for the pushed revision",
                existingPr);
            var description = await BuildDescriptionAsync(request, ct);
            var fresh = await RecoverFromClosedPullRequestAsync(request, prTitle, description.Body, existingPr, ct);
            return new ResumedPullRequest(
                fresh.Number, fresh.HtmlUrl, description, null,
                $"Opened fresh PR #{fresh.Number} after resumed PR #{existingPr} was closed; stale PR linked and closed.",
                true);
        }

        // Open: carry the already-fetched title/body so squash-message
        // composition needs no second GET. HtmlUrl prefers the forge value.
        return new ResumedPullRequest(
            existingPr, fetched.HtmlUrl ?? fallbackUrl, null, null, null, false,
            ExistingTitle: fetched.Title, ExistingBody: fetched.Body);
    }

    /// <summary>
    /// Opens a fresh PR for the pushed revision when the previous PR for this
    /// branch is closed without merge, then links the stale PR to the new one
    /// with a comment and closes it. The close is best-effort (the stale PR is
    /// already closed in the expected case); a failed comment or close is a
    /// warning, never a delivery failure, since the fresh PR already exists.
    /// </summary>
    private async Task<ReconciledPullRequest> RecoverFromClosedPullRequestAsync(
        UpstreamCompletionRequest request,
        string prTitle,
        string description,
        int stalePrNumber,
        CancellationToken ct)
    {
        GitHubPrResponse? created;
        try
        {
            (_, created, _) = await RawCreateForRecoveryAsync(request, prTitle, description, stalePrNumber, ct);
        }
        catch (Exception ex) when (IsTransportUncertainty(ex, ct))
        {
            // The replacement create may have succeeded server-side while the
            // response was lost. Reconcile the exact open PR before mutating
            // the stale one so a retry does not duplicate the replacement.
            var reused = await TryReconcileAfterCreateUncertaintyAsync(request, ct);
            if (reused is null)
                throw;
            created = new GitHubPrResponse(reused.Number, reused.HtmlUrl);
        }
        if (created is null)
            throw new InvalidOperationException(
                $"GitHub replacement PR for closed PR #{stalePrNumber} (head='{SanitizeForLog(request.WorkBranch)}') " +
                "could not be proven to exist; refusing to proceed. Resolve manually.");

        _log.LogInformation("GitHub PR opened to replace closed PR #{Old}: {Url}", stalePrNumber, created.HtmlUrl);
        AuditLog.UpstreamPrOpened(created.Number, created.HtmlUrl, request.WorkBranch, request.BaseBranch);

        try
        {
            await PostSupersededCommentAsync(stalePrNumber, created.Number, created.HtmlUrl, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning("Could not comment on stale PR #{N}; the replacement PR already exists: {Message}",
                stalePrNumber, ex.Message);
        }
        try
        {
            await ClosePullRequestAsync(stalePrNumber, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning("Could not close stale PR #{N}; the replacement PR already exists: {Message}",
                stalePrNumber, ex.Message);
        }

        return new ReconciledPullRequest(created.Number, created.HtmlUrl, AuthoritativeMergeSha: null, ReusedExisting: false);
    }

    /// <summary>
    /// Single replacement-POST for the closed-PR recovery. Unlike the initial
    /// create path, a 422 here is terminal (the stale PR is already known
    /// closed, so there is no open duplicate to reconcile to); transport
    /// uncertainty propagates so the caller reconciles before mutating.
    /// </summary>
    private async Task<(HttpStatusCode Status, GitHubPrResponse? Created, string ErrorDetail)> RawCreateForRecoveryAsync(
        UpstreamCompletionRequest request, string prTitle, string description, int stalePrNumber, CancellationToken ct)
    {
        var (status, created, errorDetail) = await RawCreatePullRequestAsync(request, prTitle, description, ct);
        if (status == HttpStatusCode.UnprocessableEntity)
            throw new InvalidOperationException(
                $"GitHub POST /pulls returned 422 while replacing closed PR #{stalePrNumber} for head='{SanitizeForLog(request.WorkBranch)}' " +
                $"base='{SanitizeForLog(request.BaseBranch)}'; refusing to guess. Forge detail: {errorDetail}");
        if (created is null)
            throw new InvalidOperationException(
                $"GitHub POST /pulls for head='{SanitizeForLog(request.WorkBranch)}' returned {(int)status} while replacing " +
                $"closed PR #{stalePrNumber}; forge detail: {errorDetail}");
        return (status, created, errorDetail);
    }

    /// <summary>
    /// Single POST /pulls attempt. Returns the status, the created PR on
    /// success, and the flattened forge error detail otherwise. Transport
    /// uncertainty throws so the caller reconciles before mutating further.
    /// </summary>
    private async Task<(HttpStatusCode Status, GitHubPrResponse? Created, string ErrorDetail)> RawCreatePullRequestAsync(
        UpstreamCompletionRequest request, string prTitle, string description, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{_opts.Owner}/{_opts.Repository}/pulls";
        var body = new GitHubCreatePrRequest(prTitle, description, request.WorkBranch, request.BaseBranch);

        using var req = await BuildRequestAsync(HttpMethod.Post, url, ct);
        req.Content = JsonContent.Create(body);

        var postPrSw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(req, ct);
        }
        catch (Exception ex) when (IsTransportUncertainty(ex, ct))
        {
            postPrSw.Stop();
            _log.LogWarning(
                "GitHub POST /pulls transport failed ({Kind}); reconciling before any further mutation",
                ex.GetType().Name);
            throw;
        }
        postPrSw.Stop();

        using (response)
        {
            CodeyBoxMeters.UpstreamApiCallDuration.Record(postPrSw.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("endpoint", "POST /pulls"),
                new KeyValuePair<string, object?>("status_code", (int)response.StatusCode));

            if (!response.IsSuccessStatusCode)
            {
                AuditLog.UpstreamApiCallFailed("POST /pulls", (int)response.StatusCode, _opts.Owner, _opts.Repository);
                var detail = await ReadErrorDetailAsync(response, ct);
                return (response.StatusCode, null, detail);
            }

            var created = await response.Content.ReadFromJsonAsync<GitHubPrResponse>(ct)
                ?? throw new InvalidOperationException(
                    $"GitHub POST /pulls returned success but response body could not be deserialised (head={request.WorkBranch})");
            return (response.StatusCode, created, string.Empty);
        }
    }

    private async Task PostSupersededCommentAsync(int stalePrNumber, int freshPrNumber, string? freshPrUrl, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{_opts.Owner}/{_opts.Repository}/issues/{stalePrNumber}/comments";
        var body = $"CodeyBox: this PR was superseded by #{freshPrNumber} ({freshPrUrl ?? $"https://github.com/{_opts.Owner}/{_opts.Repository}/pull/{freshPrNumber}"}). " +
            "The work branch was re-pushed from a fresh base after a retry; review and merge continue there.";
        using var req = await BuildRequestAsync(HttpMethod.Post, url, ct);
        req.Content = JsonContent.Create(new GitHubCreateIssueCommentRequest(body));
        using var response = await SendAsync(req, ct);
        if (!response.IsSuccessStatusCode)
            AuditLog.UpstreamApiCallFailed("POST /issues/comments", (int)response.StatusCode, _opts.Owner, _opts.Repository);
        response.EnsureSuccessStatusCode();
        _log.LogInformation("GitHub PR #{Old} linked to superseding PR #{New}", stalePrNumber, freshPrNumber);
    }

    private async Task ClosePullRequestAsync(int prNumber, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{_opts.Owner}/{_opts.Repository}/pulls/{prNumber}";
        using var req = await BuildRequestAsync(new HttpMethod("PATCH"), url, ct);
        req.Content = JsonContent.Create(new GitHubClosePullRequestRequest());
        using var response = await SendAsync(req, ct);
        if (!response.IsSuccessStatusCode)
            AuditLog.UpstreamApiCallFailed("PATCH /pulls", (int)response.StatusCode, _opts.Owner, _opts.Repository);
        response.EnsureSuccessStatusCode();
        _log.LogInformation("GitHub PR #{N} closed as superseded", prNumber);
    }

    /// <summary>
    /// Outcome of PR creation: either a freshly opened PR or an existing PR
    /// reclaimed after a create conflict / transport uncertainty.
    /// <see cref="AuthoritativeMergeSha"/> is non-null only when the exact PR
    /// for the pushed revision is already merged on the forge (proven by
    /// head-sha match); the caller then reports delivery without a merge call.
    /// </summary>
    private sealed record ReconciledPullRequest(
        int Number,
        string? HtmlUrl,
        string? AuthoritativeMergeSha,
        bool ReusedExisting);

    private async Task<ReconciledPullRequest> CreateOrReconcilePullRequestAsync(
        UpstreamCompletionRequest request, string prTitle, string description, CancellationToken ct)
    {
        HttpStatusCode status;
        GitHubPrResponse? created;
        string errorDetail;
        try
        {
            (status, created, errorDetail) = await RawCreatePullRequestAsync(request, prTitle, description, ct);
        }
        catch (Exception ex) when (IsTransportUncertainty(ex, ct))
        {
            // The create may have succeeded server-side while the response was
            // lost (timeout / reset). Reconcile before any other mutation so a
            // retry does not create a duplicate PR.
            var reused = await TryReconcileAfterCreateUncertaintyAsync(request, ct);
            if (reused is not null)
                return reused;
            throw;
        }

        if (status == HttpStatusCode.UnprocessableEntity)
        {
            _log.LogWarning(
                "GitHub POST /pulls returned 422 for {Owner}/{Repo} head={WorkBranch} base={BaseBranch}; reconciling rather than assuming a duplicate",
                _opts.Owner, _opts.Repository, request.WorkBranch, request.BaseBranch);
            return await ReconcileExistingPullRequestAsync(request, errorDetail, prTitle, description, ct);
        }

        if (created is null)
            throw new InvalidOperationException(
                $"GitHub POST /pulls for head='{SanitizeForLog(request.WorkBranch)}' returned {(int)status}; forge detail: {errorDetail}");
        return new ReconciledPullRequest(created.Number, created.HtmlUrl, AuthoritativeMergeSha: null, ReusedExisting: false);
    }

    /// <summary>
    /// True for transport failures where the server may already have applied
    /// the mutation (timeout, reset, TLS teardown). Caller cancellation
    /// (<paramref name="ct"/> requested) is never uncertainty — it rethrows.
    /// </summary>
    private static bool IsTransportUncertainty(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException
        || ex is TimeoutException
        || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    /// <summary>Best-effort read of a forge error body for diagnostics. Never throws.</summary>
    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        const int maxDetailChars = 2000;
        try
        {
            var text = (await response.Content.ReadAsStringAsync(ct) ?? string.Empty).Trim();
            // Flatten line breaks: this detail is embedded in exception
            // messages that flow to LastError and webhook payloads.
            var flattened = SanitizeForLog(text);
            return flattened.Length <= maxDetailChars ? flattened : flattened[..maxDetailChars] + "…(truncated)";
        }
        catch (Exception)
        {
            // Never fail reconciliation because the error body was unreadable.
            return "(unreadable response body)";
        }
    }

    /// <summary>
    /// Resolves the just-pushed tip of the work branch in the host bare repo.
    /// Null when unresolvable — the caller then refuses to match, because
    /// revision proof is impossible.
    /// </summary>
    private async Task<string?> ResolveWorkBranchTipAsync(UpstreamCompletionRequest request, CancellationToken ct)
    {
        try
        {
            return await _gitHost.ResolveCommitAsync(request.RepositoryId, request.WorkBranch, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug("Could not resolve pushed tip of '{Branch}': {Message}", SanitizeForLog(request.WorkBranch), ex.Message);
            return null;
        }
    }

    // Bounds for 422 reconciliation: one list per state (open, then closed
    // only when no exact open PR exists) plus one detail fetch per candidate.
    // Hitting a bound is itself a refusal — never a guess.
    private const int ReconcileListPerPage = 100;
    private const int ReconcileListMaxPages = 3;
    private const int ReconcileMaxDetailFetches = 10;

    /// <summary>
    /// Classifies a POST /pulls 422 without assuming duplicate success. Only an
    /// exact PR — same repository (by construction of the listing URL), same
    /// head owner/ref, same base ref, same pushed revision — may be reused.
    /// A closed-unmerged exact match is superseded: a fresh PR is opened for
    /// the pushed revision and the stale PR is linked and closed. Anything
    /// else (validation errors, wrong owner/base/revision, ambiguous matches)
    /// throws with an actionable diagnostic so the orchestrator parks rather
    /// than reporting delivery.
    /// </summary>
    private async Task<ReconciledPullRequest> ReconcileExistingPullRequestAsync(
        UpstreamCompletionRequest request, string createErrorDetail, string prTitle, string description, CancellationToken ct)
    {
        var expectedHeadSha = await ResolveWorkBranchTipAsync(request, ct);
        if (string.IsNullOrWhiteSpace(expectedHeadSha))
            throw new InvalidOperationException(
                $"GitHub POST /pulls returned 422 but the pushed revision of '{SanitizeForLog(request.WorkBranch)}' could not be resolved, " +
                $"so no existing PR can be proven to carry this work; refusing to assume a duplicate. Forge detail: {createErrorDetail}");

        var openNumbers = await ListPullRequestNumbersForBranchAsync(request, "open", ct);
        var openMatches = await FindExactMatchesAsync(request, openNumbers, expectedHeadSha, ct);
        if (openMatches.Count > 1)
            throw new InvalidOperationException(
                $"GitHub POST /pulls returned 422 and {openMatches.Count} open PRs exactly match head='{SanitizeForLog(request.WorkBranch)}' " +
                $"base='{SanitizeForLog(request.BaseBranch)}' revision; refusing to pick one. Resolve the duplicates manually.");
        if (openMatches.Count == 1)
            return ClassifySingleMatch(request, openMatches[0]);

        // No exact open PR. The 422 may still name a closed or merged PR for
        // this branch: a closed-unmerged PR is not delivered, and an
        // already-merged PR counts only when its head is the pushed revision
        // (a reused branch name alone is insufficient).
        var closedNumbers = await ListPullRequestNumbersForBranchAsync(request, "closed", ct);
        var closedMatches = await FindExactMatchesAsync(request, closedNumbers, expectedHeadSha, ct);
        var merged = closedMatches.FirstOrDefault(d => d.Merged);
        if (merged is not null)
            return ClassifySingleMatch(request, merged);
        if (closedMatches.Count > 0)
        {
            // The previous PR for this exact revision was closed without
            // merge (e.g. an operator closed the stale PR, or a prior attempt
            // parked after the push). Supersede it: open a fresh PR and link
            // the stale one to it rather than failing the item forever.
            _log.LogWarning(
                "GitHub POST /pulls returned 422 and PR #{Old} for head={WorkBranch} base={BaseBranch} is closed without merge; opening a fresh PR",
                closedMatches[0].Number, request.WorkBranch, request.BaseBranch);
            return await RecoverFromClosedPullRequestAsync(request, prTitle, description, closedMatches[0].Number, ct);
        }
        throw new InvalidOperationException(
            $"GitHub POST /pulls returned 422 and no existing PR exactly matches head='{SanitizeForLog(request.WorkBranch)}' " +
            $"base='{SanitizeForLog(request.BaseBranch)}' pushed revision; treating as a validation failure, not a duplicate. " +
            $"Forge detail: {createErrorDetail}");
    }

    private ReconciledPullRequest ClassifySingleMatch(UpstreamCompletionRequest request, GitHubPrDetailMergeable match)
    {
        if (match.Merged)
        {
            if (string.IsNullOrWhiteSpace(match.MergeCommitSha))
                throw new InvalidOperationException(
                    $"GitHub PR #{match.Number} for head='{SanitizeForLog(request.WorkBranch)}' is marked merged but reports no merge commit; " +
                    "delivery cannot be proven. Resolve manually.");
            return new ReconciledPullRequest(
                match.Number,
                match.HtmlUrl ?? $"https://github.com/{_opts.Owner}/{_opts.Repository}/pull/{match.Number}",
                match.MergeCommitSha,
                ReusedExisting: true);
        }
        if (string.Equals(match.State, "closed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"GitHub PR #{match.Number} for head='{SanitizeForLog(request.WorkBranch)}' base='{SanitizeForLog(request.BaseBranch)}' " +
                "is closed without merge; this work is not delivered. Reopen it or open a new PR manually.");
        return new ReconciledPullRequest(
            match.Number,
            match.HtmlUrl ?? $"https://github.com/{_opts.Owner}/{_opts.Repository}/pull/{match.Number}",
            AuthoritativeMergeSha: null,
            ReusedExisting: true);
    }

    /// <summary>
    /// Single bounded reconcile attempt after a lost create response. Returns
    /// the exact PR when exactly one open match is provable, else null so the
    /// caller rethrows the original transport error into the bounded retry loop
    /// (whose next attempt reconciles through the 422 path instead of
    /// duplicating the PR).
    /// </summary>
    private async Task<ReconciledPullRequest?> TryReconcileAfterCreateUncertaintyAsync(
        UpstreamCompletionRequest request, CancellationToken ct)
    {
        try
        {
            var expectedHeadSha = await ResolveWorkBranchTipAsync(request, ct);
            if (string.IsNullOrWhiteSpace(expectedHeadSha))
                return null;
            var openNumbers = await ListPullRequestNumbersForBranchAsync(request, "open", ct);
            var matches = await FindExactMatchesAsync(request, openNumbers, expectedHeadSha, ct);
            if (matches.Count != 1)
                return null;
            try
            {
                return ClassifySingleMatch(request, matches[0]);
            }
            catch (InvalidOperationException ex)
            {
                _log.LogDebug("Create-uncertainty reconcile found PR #{N} but it is not reusable: {Message}", matches[0].Number, ex.Message);
                return null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug("Reconciliation after create uncertainty failed: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Lists candidate PR numbers for this exact head/base pair. The head/base
    /// filter runs server-side; every candidate is still verified client-side
    /// against owner/ref/revision before use.
    /// </summary>
    private async Task<IReadOnlyList<int>> ListPullRequestNumbersForBranchAsync(
        UpstreamCompletionRequest request, string state, CancellationToken ct)
    {
        var numbers = new List<int>();
        for (var page = 1; page <= ReconcileListMaxPages; page++)
        {
            var url = $"https://api.github.com/repos/{_opts.Owner}/{_opts.Repository}/pulls" +
                $"?state={state}&head={_opts.Owner}:{request.WorkBranch}&base={request.BaseBranch}" +
                $"&per_page={ReconcileListPerPage}&page={page}";
            using var listReq = await BuildRequestAsync(HttpMethod.Get, url, ct);
            using var listResp = await SendAsync(listReq, ct);
            if (!listResp.IsSuccessStatusCode)
            {
                AuditLog.UpstreamApiCallFailed($"GET /pulls?state={state}", (int)listResp.StatusCode, _opts.Owner, _opts.Repository);
                throw new InvalidOperationException(
                    $"GitHub POST /pulls conflicted but reconciliation listing (state={state}) failed with {(int)listResp.StatusCode}; " +
                    $"no existing PR can be proven to carry the pushed revision of '{SanitizeForLog(request.WorkBranch)}'.");
            }
            var summaries = await listResp.Content.ReadFromJsonAsync<GitHubPrSummary[]>(ct) ?? [];
            foreach (var summary in summaries)
                numbers.Add(summary.Number);
            if (summaries.Length < ReconcileListPerPage) break;
        }
        return numbers;
    }

    /// <summary>
    /// Fetches details for each candidate (bounded) and keeps only exact
    /// revision matches: same head ref, same base ref, same head owner when
    /// reported, same head sha as the just-pushed tip. Unfetchable candidates
    /// are skipped — they are unprovable, never assumed.
    /// </summary>
    private async Task<IReadOnlyList<GitHubPrDetailMergeable>> FindExactMatchesAsync(
        UpstreamCompletionRequest request, IReadOnlyList<int> numbers, string expectedHeadSha, CancellationToken ct)
    {
        if (numbers.Count > ReconcileMaxDetailFetches)
            throw new InvalidOperationException(
                $"GitHub POST /pulls conflicted with {numbers.Count} candidate PRs for head='{SanitizeForLog(request.WorkBranch)}'; " +
                "too many to disambiguate safely. Resolve manually.");
        var matches = new List<GitHubPrDetailMergeable>();
        foreach (var number in numbers)
        {
            var detail = await FetchPullRequestDetailAsync(number, ct);
            if (detail is null) continue;
            if (IsExactRevisionMatch(detail, request, expectedHeadSha))
                matches.Add(detail);
        }
        return matches;
    }

    private bool IsExactRevisionMatch(GitHubPrDetailMergeable detail, UpstreamCompletionRequest request, string expectedHeadSha)
    {
        if (!string.Equals(detail.Head?.Ref, request.WorkBranch, StringComparison.Ordinal))
            return false;
        if (!string.Equals(detail.Base?.Ref, request.BaseBranch, StringComparison.Ordinal))
            return false;
        var login = detail.Head?.User?.Login;
        if (!string.IsNullOrWhiteSpace(login) && !string.Equals(login, _opts.Owner, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(detail.Head?.Sha, expectedHeadSha, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private async Task<GitHubPrResponse?> TryFetchPullRequestAsync(int prNumber, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.github.com/repos/{_opts.Owner}/{_opts.Repository}/pulls/{prNumber}";
            using var req = await BuildRequestAsync(HttpMethod.Get, url, ct);

            var getPrSw = Stopwatch.StartNew();
            using var response = await SendAsync(req, ct);
            getPrSw.Stop();
            CodeyBoxMeters.UpstreamApiCallDuration.Record(getPrSw.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("endpoint", "GET /pulls"),
                new KeyValuePair<string, object?>("status_code", (int)response.StatusCode));

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "GitHub GET /pulls/{N} returned {Status}; using local request data for squash commit message",
                    prNumber, (int)response.StatusCode);
                AuditLog.UpstreamApiCallFailed("GET /pulls", (int)response.StatusCode, _opts.Owner, _opts.Repository);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<GitHubPrResponse>(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                "Could not read PR #{N} while composing squash commit message ({Message}); using local request data fallback",
                prNumber, ex.Message);
            return null;
        }
    }

    private async Task<(string? Sha, string? Notes, bool AutoMergeRaced)> MergePullRequestAsync(
        int prNumber,
        string prTitle,
        PrDescriptionResult? prDescription,
        UpstreamCompletionRequest completionRequest,
        CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{_opts.Owner}/{_opts.Repository}/pulls/{prNumber}/merge";
        var body = await BuildMergeRequestAsync(prNumber, prTitle, prDescription, completionRequest, ct);

        using var req = await BuildRequestAsync(HttpMethod.Put, url, ct);
        req.Content = JsonContent.Create(body);

        var putMergeSw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(req, ct);
        }
        catch (Exception ex) when (IsTransportUncertainty(ex, ct))
        {
            // The merge may have landed server-side while the response was
            // lost. Verify the authoritative PR state before any other
            // mutation (in particular, before retrying the PUT, which would
            // act on an already-merged PR).
            putMergeSw.Stop();
            _log.LogWarning(
                "GitHub PUT /pulls/{N}/merge transport failed ({Kind}); verifying merge state before any further mutation",
                prNumber, ex.GetType().Name);
            var verified = await TryVerifyMergeAfterUncertaintyAsync(prNumber, completionRequest, ct);
            if (verified is not null)
                return verified.Value;
            throw;
        }
        putMergeSw.Stop();

        using (response)
        {
            CodeyBoxMeters.UpstreamApiCallDuration.Record(putMergeSw.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("endpoint", "PUT /pulls/merge"),
                new KeyValuePair<string, object?>("status_code", (int)response.StatusCode));

            if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                // 405 here is conventionally "PR not mergeable" — usually a race
                // against upstream main motion (someone pushed to base between our
                // local merge phase and this PUT). The orchestrator catches the
                // AutoMergeRaced flag, re-fetches base, re-runs the merge phase
                // against the new tip, and retries this PUT. Branch protection can
                // also surface as 405; in that case re-fetching shows base unchanged
                // and the orchestrator parks the item rather than spinning.
                const string note = "GitHub PUT /pulls/N/merge returned 405 (PR not mergeable — likely a race against upstream base; orchestrator will re-fetch base and re-run merge phase)";
                _log.LogWarning(
                    "GitHub PUT /pulls/{N}/merge returned 405 (PR not mergeable); orchestrator will re-fetch base and re-run merge phase",
                    prNumber);
                AuditLog.UpstreamApiCallFailed("PUT /pulls/merge", 405, _opts.Owner, _opts.Repository);
                return (null, note, true);
            }

            if (!response.IsSuccessStatusCode)
                AuditLog.UpstreamApiCallFailed("PUT /pulls/merge", (int)response.StatusCode, _opts.Owner, _opts.Repository);

            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<GitHubMergeResponse>(ct);
            // A merge counts as delivery only with the forge's own proof: the
            // merged flag plus a non-empty authoritative sha. Branch push, an
            // unidentified PR, or a sha-less success response is not proof.
            if (result?.Merged != true || string.IsNullOrWhiteSpace(result.Sha))
                throw new InvalidOperationException(
                    $"GitHub PUT /pulls/{prNumber}/merge returned success but did not prove a merge " +
                    $"(merged={result?.Merged}, sha present={!string.IsNullOrWhiteSpace(result?.Sha)}); refusing to report delivery.");
            return (result.Sha, null, false);
        }
    }

    /// <summary>
    /// Single bounded verification after a lost merge response: the PR counts
    /// as merged only when the forge reports merged with a merge commit sha
    /// AND the PR head is still the revision we pushed (a reused branch name
    /// alone is insufficient). Null when unprovable — the caller rethrows the
    /// original transport error into the bounded retry loop.
    /// </summary>
    private async Task<(string? Sha, string? Notes, bool AutoMergeRaced)?> TryVerifyMergeAfterUncertaintyAsync(
        int prNumber, UpstreamCompletionRequest request, CancellationToken ct)
    {
        try
        {
            var expectedHeadSha = await ResolveWorkBranchTipAsync(request, ct);
            if (string.IsNullOrWhiteSpace(expectedHeadSha))
                return null;
            var detail = await FetchPullRequestDetailAsync(prNumber, ct);
            if (detail?.Merged != true || string.IsNullOrWhiteSpace(detail.MergeCommitSha))
                return null;
            if (!string.Equals(detail.Head?.Sha, expectedHeadSha, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning(
                    "GitHub PR #{N} is merged but its head no longer matches the pushed revision; refusing to claim this work as delivered",
                    prNumber);
                return null;
            }
            _log.LogInformation("GitHub PR #{N} merge verified after transport failure: {Sha}", prNumber, detail.MergeCommitSha);
            AuditLog.UpstreamPrMerged(prNumber, detail.MergeCommitSha);
            return (detail.MergeCommitSha, "Merge response was lost in transport; delivery verified by re-reading the merged PR state.", false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug("Merge verification after transport failure failed: {Message}", ex.Message);
            return null;
        }
    }

    private async Task<GitHubMergeRequest> BuildMergeRequestAsync(
        int prNumber,
        string prTitle,
        PrDescriptionResult? prDescription,
        UpstreamCompletionRequest completionRequest,
        CancellationToken ct)
    {
        if (!IsSquashMerge(_opts.MergeMethod))
            return new GitHubMergeRequest(_opts.MergeMethod);

        IReadOnlyList<string> commitMessages = [];
        try
        {
            commitMessages = await FetchPullRequestCommitMessagesAsync(prNumber, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                "Could not read commits for PR #{N} while composing squash commit message ({Message}); using available PR description fallback",
                prNumber, ex.Message);
        }

        var commitTitle = StripCiSkipControlsFromTitle(BuildSquashCommitTitle(prTitle, prNumber));
        if (string.IsNullOrWhiteSpace(commitTitle) || PullRequestNumberOnly.IsMatch(commitTitle))
            commitTitle = BuildSquashCommitTitle("chore: merge CodeyBox pull request", prNumber);

        var commitMessage = StripCiSkipControlsFromBody(
            BuildSquashCommitMessage(prDescription, commitMessages, completionRequest));
        return new GitHubMergeRequest(_opts.MergeMethod, commitTitle, commitMessage);
    }

    private async Task<IReadOnlyList<string>> FetchPullRequestCommitMessagesAsync(int prNumber, CancellationToken ct)
    {
        const int perPage = 100;
        const int maxPages = 10;
        const int maxCommitMessageBytes = 8192;
        const int maxTotalCommitMessageBytes = 65536;
        var messages = new List<string>();
        var retainedBytes = 0;

        for (var page = 1; page <= maxPages; page++)
        {
            var url = $"https://api.github.com/repos/{_opts.Owner}/{_opts.Repository}/pulls/{prNumber}/commits" +
                $"?per_page={perPage}&page={page}";
            using var req = await BuildRequestAsync(HttpMethod.Get, url, ct);
            using var response = await SendAsync(req, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "GitHub GET /pulls/{N}/commits returned {Status}; using PR description fallback for squash commit message",
                    prNumber, (int)response.StatusCode);
                AuditLog.UpstreamApiCallFailed("GET /pulls/commits", (int)response.StatusCode, _opts.Owner, _opts.Repository);
                return messages;
            }

            var commits = await response.Content.ReadFromJsonAsync<GitHubPullRequestCommitResponse[]>(ct);
            if (commits is null || commits.Length == 0)
                break;

            foreach (var commit in commits)
            {
                var message = commit.Commit?.Message;
                if (string.IsNullOrWhiteSpace(message))
                    continue;

                var remainingBytes = maxTotalCommitMessageBytes - retainedBytes;
                if (remainingBytes < 64)
                    return messages;

                var truncated = TruncateCommitMessageForSquashFallback(
                    message,
                    Math.Min(maxCommitMessageBytes, remainingBytes));
                messages.Add(truncated);
                retainedBytes += Encoding.UTF8.GetByteCount(truncated);
            }

            if (commits.Length < perPage)
                break;
        }

        return messages;
    }

    private static string BuildSquashCommitTitle(string prTitle, int prNumber)
    {
        var title = CollapseWhitespace.Replace(prTitle, " ").Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = "chore: merge CodeyBox pull request";

        var expectedSuffix = string.Create(CultureInfo.InvariantCulture, $" (#{prNumber})");
        return title.EndsWith(expectedSuffix, StringComparison.Ordinal)
            ? title
            : title + expectedSuffix;
    }

    private static string BuildSquashCommitMessage(
        PrDescriptionResult? prDescription,
        IReadOnlyList<string> commitMessages,
        UpstreamCompletionRequest completionRequest)
    {
        var promptRevision =
            ExtractLastPromptRevision(commitMessages) ??
            completionRequest.PromptRevision ??
            ExtractLastPromptRevision(prDescription?.Body);

        var body = prDescription?.Generated == true
            ? CleanProseForCommitMessage(prDescription.Body)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(body))
            body = CleanCommitMessagesForFallback(commitMessages);

        if (string.IsNullOrWhiteSpace(body))
            body = CleanProseForCommitMessage(prDescription?.Body ?? completionRequest.Description);

        if (string.IsNullOrWhiteSpace(body))
            body = "Apply the CodeyBox work item changes.";

        return $"{body.Trim()}\n\n{BuildSquashTrailerBlock(promptRevision)}";
    }

    private static string CleanCommitMessagesForFallback(IReadOnlyList<string> commitMessages)
    {
        var paragraphs = new List<string>();
        foreach (var message in commitMessages)
            paragraphs.AddRange(ExtractCommitMessageParagraphs(message));

        return FormatCommitBody(paragraphs);
    }

    private static IEnumerable<string> ExtractCommitMessageParagraphs(string message)
    {
        if (HasMechanicalFixerTrailer(message))
            yield break;

        foreach (var paragraph in ExtractCleanParagraphs(message, stopAtPrFooter: false))
        {
            if (!IsPureIterationNoise(paragraph))
                yield return paragraph;
        }
    }

    /// <summary>
    /// True when the commit message's RFC-5322 trailer block carries the
    /// <see cref="CodeyBoxTrailers.MechanicalFixerTrailerKey"/> trailer.
    /// Mechanical-fixer commits should be treated as iteration noise
    /// regardless of their subject line, so a future fixer with a
    /// different <c>CommitSubject</c> still drops out of the squashed PR
    /// body. The match is intentionally scoped to the last paragraph and
    /// requires every non-empty line in that paragraph to look like a
    /// trailer (<c>Token: value</c>, or a whitespace-led continuation),
    /// so prose elsewhere in the body that happens to mention the trailer
    /// key does not silently opt the commit out of the squash body.
    /// </summary>
    private static bool HasMechanicalFixerTrailer(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var lines = message.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        var end = lines.Length - 1;
        while (end >= 0 && string.IsNullOrWhiteSpace(lines[end]))
            end--;
        if (end < 0)
            return false;

        var start = end;
        while (start > 0 && !string.IsNullOrWhiteSpace(lines[start - 1]))
            start--;

        var foundMechanical = false;
        for (var i = start; i <= end; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
                continue;
            if (char.IsWhiteSpace(line[0]))
                continue; // RFC-5322 trailer continuation line.
            if (!TrailerTokenLine.IsMatch(line))
                return false;
            if (line.StartsWith(CodeyBoxTrailers.MechanicalFixerTrailerKey + ":", StringComparison.Ordinal))
                foundMechanical = true;
        }

        return foundMechanical;
    }

    private static readonly Regex TrailerTokenLine = new(
        @"^[A-Za-z][A-Za-z0-9-]*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string CleanProseForCommitMessage(string? text)
        => FormatCommitBody(ExtractCleanParagraphs(StripDeterministicScaffolding(text), stopAtPrFooter: true));

    /// <summary>
    /// Removes deterministic PR-body scaffolding (the work item header and the
    /// changed-files caption) before extracting commit-message prose, so merge
    /// commits carry the narrative rather than template lines. Fenced file
    /// lists and the machine-generated notice are already skipped by paragraph
    /// extraction (fence tracking, blockquote lines); only the caption lines
    /// need explicit removal here.
    /// </summary>
    private static string? StripDeterministicScaffolding(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;
        var kept = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Automated via CodeyBox", StringComparison.Ordinal))
                continue;
            if (trimmed.Equals("Changed files:", StringComparison.Ordinal))
                continue;
            kept.Add(line);
        }
        return string.Join("\n", kept);
    }

    private static IEnumerable<string> ExtractCleanParagraphs(string? text, bool stopAtPrFooter)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var current = new StringBuilder();
        var inFence = false;

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmedRaw = rawLine.Trim();
            if (stopAtPrFooter && trimmedRaw == "---")
                break;

            if (trimmedRaw.StartsWith("```", StringComparison.Ordinal))
            {
                if (current.Length > 0)
                {
                    yield return ToImperativeSentence(current.ToString());
                    current.Clear();
                }

                inFence = !inFence;
                continue;
            }

            if (inFence)
                continue;

            if (IsSkippableCommitMessageLine(trimmedRaw))
            {
                if (current.Length > 0)
                {
                    yield return ToImperativeSentence(current.ToString());
                    current.Clear();
                }

                continue;
            }

            var line = CleanMarkdownLine(trimmedRaw);
            if (string.IsNullOrWhiteSpace(line))
            {
                if (current.Length > 0)
                {
                    yield return ToImperativeSentence(current.ToString());
                    current.Clear();
                }

                continue;
            }

            if (IsPureIterationNoise(line))
                continue;

            if (current.Length > 0)
                current.Append(' ');
            current.Append(line);
        }

        if (current.Length > 0)
            yield return ToImperativeSentence(current.ToString());
    }

    private static bool IsSkippableCommitMessageLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (line.StartsWith("#", StringComparison.Ordinal)) return true;
        if (line.StartsWith(">", StringComparison.Ordinal)) return true;
        if (line.StartsWith("```", StringComparison.Ordinal)) return true;
        if (line.Contains("Generated with [CodeyBox]", StringComparison.Ordinal)) return true;
        if (line.Contains(CodeyBoxTrailers.CoAuthoredBy, StringComparison.Ordinal)) return true;
        if (KnownTrailerLine.IsMatch(line)) return true;
        if (SkipChecksTrailerLine.IsMatch(line)) return true;
        if (ChecklistPrefix.IsMatch(line)) return true;
        return false;
    }

    private static string CleanMarkdownLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return string.Empty;
        line = ChecklistPrefix.Replace(line, string.Empty);
        line = BulletPrefix.Replace(line, string.Empty);
        line = NumberedListPrefix.Replace(line, string.Empty);
        line = MarkdownLink.Replace(line, "$1");
        line = ConventionalSubjectPrefix.Replace(line, string.Empty);
        line = line.Replace("`", string.Empty, StringComparison.Ordinal);
        line = line.Trim(' ', '\t', '*', '_');
        return CollapseWhitespace.Replace(line, " ").Trim();
    }

    private static string FormatCommitBody(IEnumerable<string> paragraphs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<string>();

        foreach (var paragraph in paragraphs)
        {
            var normalized = CollapseWhitespace.Replace(paragraph, " ").Trim();
            if (string.IsNullOrWhiteSpace(normalized)) continue;
            normalized = EnsureSentence(normalized);
            if (!seen.Add(normalized)) continue;
            cleaned.Add(WrapParagraph(normalized, 72));
            if (cleaned.Count >= 8) break;
        }

        return string.Join("\n\n", cleaned).Trim();
    }

    private static string WrapParagraph(string paragraph, int width)
    {
        var words = CollapseWhitespace.Split(paragraph.Trim());
        var sb = new StringBuilder();
        var lineLength = 0;

        foreach (var word in words)
        {
            if (word.Length == 0) continue;

            if (lineLength == 0)
            {
                sb.Append(word);
                lineLength = word.Length;
                continue;
            }

            if (lineLength + 1 + word.Length > width)
            {
                sb.Append('\n').Append(word);
                lineLength = word.Length;
            }
            else
            {
                sb.Append(' ').Append(word);
                lineLength += 1 + word.Length;
            }
        }

        return sb.ToString();
    }

    private static string ToImperativeSentence(string text)
    {
        var cleaned = CollapseWhitespace.Replace(text, " ").Trim();
        if (cleaned.Length == 0) return cleaned;

        foreach (var (prefix, replacement) in ImperativePrefixes)
        {
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return replacement + cleaned[prefix.Length..];
        }

        return char.ToUpperInvariant(cleaned[0]) + cleaned[1..];
    }

    private static string EnsureSentence(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return text;
        var last = text[^1];
        return last is '.' or '!' or '?' ? text : text + ".";
    }

    private static bool IsPureIterationNoise(string line)
    {
        var normalized = CollapseWhitespace.Replace(line, " ").Trim().TrimEnd('.');
        if (normalized.Length == 0) return true;
        var lower = normalized.ToLowerInvariant();

        if (lower.StartsWith("codeybox: merge ", StringComparison.Ordinal)) return true;
        if (lower.StartsWith("codeybox:", StringComparison.Ordinal))
            return IsPureIterationNoise(lower["codeybox:".Length..]);
        if (lower.StartsWith("codeybox rework:", StringComparison.Ordinal))
            return IsPureIterationNoise(lower["codeybox rework:".Length..]);
        if ((lower.StartsWith("stamp ", StringComparison.Ordinal) ||
             lower.StartsWith("restamp ", StringComparison.Ordinal)) &&
            (lower.Contains("prompt-revision trailer", StringComparison.Ordinal) ||
             lower.Contains("prompt revision trailer", StringComparison.Ordinal)))
            return true;
        if (lower.StartsWith("merge branch ", StringComparison.Ordinal)) return true;
        if (lower.StartsWith("merge main", StringComparison.Ordinal)) return true;
        if (lower.Contains("merge conflict", StringComparison.Ordinal) &&
            (lower.StartsWith("chore:", StringComparison.Ordinal) ||
             lower.StartsWith("fix:", StringComparison.Ordinal) ||
             lower.StartsWith("resolve", StringComparison.Ordinal)))
            return true;
        if (lower is "rework" or "audit fix" or "fix audit" or "address audit feedback")
            return true;

        return PureReworkOrAuditSubject.IsMatch(lower);
    }

    private static string BuildSquashTrailerBlock(int? promptRevision)
    {
        if (promptRevision is null)
            return CodeyBoxTrailers.CoAuthoredBy;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{CodeyBoxTrailers.PromptRevisionTrailerKey}: {promptRevision}\n{CodeyBoxTrailers.CoAuthoredBy}");
    }

    private static string StripCiSkipControlsFromTitle(string title)
        => CollapseWhitespace.Replace(CiSkipDirective.Replace(title, string.Empty), " ").Trim();

    private static string StripCiSkipControlsFromBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return body;

        var normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal);
        var cleaned = new StringBuilder(normalized.Length);
        foreach (var rawLine in normalized.Split('\n'))
        {
            if (SkipChecksTrailerLine.IsMatch(rawLine.Trim()))
                continue;

            cleaned.Append(CiSkipDirective.Replace(rawLine, string.Empty).TrimEnd()).Append('\n');
        }

        return ExcessBlankLines.Replace(cleaned.ToString().Trim(), "\n\n");
    }

    private static string TruncateCommitMessageForSquashFallback(string message, int maxBytes)
    {
        if (ExtractLastPromptRevision(message) is not { } promptRevision)
            return RawOutputRedactor.TruncateToBytes(message, maxBytes);

        var promptRevisionTrailer = string.Create(
            CultureInfo.InvariantCulture,
            $"\n\n{CodeyBoxTrailers.PromptRevisionTrailerKey}: {promptRevision}");
        var trailerBytes = Encoding.UTF8.GetByteCount(promptRevisionTrailer);
        if (maxBytes <= trailerBytes + 64)
            return promptRevisionTrailer.Trim();

        var truncated = RawOutputRedactor.TruncateToBytes(message, maxBytes - trailerBytes);
        if (ExtractLastPromptRevision(truncated) is not null)
            return truncated;

        return truncated.TrimEnd() + promptRevisionTrailer;
    }

    private static int? ExtractLastPromptRevision(IReadOnlyList<string> commitMessages)
    {
        int? result = null;
        foreach (var message in commitMessages)
        {
            var revisions = ExtractPromptRevisions(message);
            if (revisions.Count > 1)
                return null;
            if (revisions.Count == 1)
                result = revisions[0];
        }
        return result;
    }

    private static int? ExtractLastPromptRevision(string? text)
    {
        var revisions = ExtractPromptRevisions(text);
        return revisions.Count == 0 ? null : revisions[^1];
    }

    private static IReadOnlyList<int> ExtractPromptRevisions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var revisions = new List<int>();
        foreach (Match match in PromptRevisionTrailer.Matches(text))
            if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var rev))
                revisions.Add(rev);
        return revisions;
    }

    private static bool IsSquashMerge(string mergeMethod)
        => mergeMethod.Equals("squash", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Audit check-run publication is supported on GitHub via the Checks API
    /// (<c>checks:write</c> for GitHub App tokens, or Checks read+write on a
    /// fine-grained PAT / <c>repo</c> scope on a classic PAT). The caller owns
    /// the opt-in decision; this only reports capability.
    /// </summary>
    public Task<AuditCheckPublicationSupport> GetAuditCheckPublicationSupportAsync(
        CancellationToken ct = default)
        => Task.FromResult(AuditCheckPublicationSupport.Yes);

    /// <summary>
    /// Publishes structured audit findings as a GitHub check run for the exact
    /// audited commit. Reuses this remote's token plumbing and
    /// <c>github-upstream</c> client; reconciliation by <c>external_id</c>
    /// converges concurrent delivery, restarts, and lost create responses
    /// instead of duplicating check runs.
    /// </summary>
    public Task<AuditCheckPublicationResult> PublishAuditCheckAsync(
        AuditCheckPublicationRequest request,
        AuditCheckPublicationOptions options,
        CancellationToken ct = default)
    {
        var client = new GitHubCheckRunsClient(_httpClientFactory, _tokenProvider, _opts.Owner, _opts.Repository, _log);
        return new GitHubAuditCheckPublisher(client).PublishAsync(request, options, ct);
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(
        HttpMethod method,
        string url,
        CancellationToken cancellationToken)
    {
        var req = new HttpRequestMessage(method, url);
        var token = await _tokenProvider.GetTokenAsync(cancellationToken);
        req.Headers.Authorization = new AuthenticationHeaderValue("token", token);
        return req;
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        => _httpClientFactory.CreateClient("github-upstream").SendAsync(req, ct);

    private string RepoUrl() => $"https://github.com/{_opts.Owner}/{_opts.Repository}.git";

    private static string Scrub(string message, string token) =>
        string.IsNullOrEmpty(token)
            ? message
            : message.Replace(token, "***", StringComparison.Ordinal);

    private static bool TryBuildSafeReconcileConflict(
        Exception ex, string token, out UpstreamPushReconcileConflictException safeConflict)
    {
        safeConflict = null!;
        if (!UpstreamPushReconcileConflictException.TryFindIn(ex, out var typed))
            return false;

        if (!typed.Strategy.Equals("merge", StringComparison.Ordinal)
            && !typed.Strategy.Equals("rebase", StringComparison.Ordinal))
            return false;

        var scrubbedBranch = Scrub(typed.Branch, token);
        try
        {
            Validation.ValidateBranchName(scrubbedBranch, nameof(typed.Branch));
        }
        catch (ArgumentException)
        {
            return false;
        }

        safeConflict = new UpstreamPushReconcileConflictException(scrubbedBranch, typed.Strategy);
        return true;
    }

    /// <summary>
    /// Rebuilds a lease mismatch without the inner chain (which may echo
    /// credential material from git transport errors). Shas are validated as
    /// hex; anything unverifiable is replaced with a placeholder rather than
    /// propagated, and a validation failure never blocks the park — the
    /// branch name alone is sufficient to route the operator.
    /// </summary>
    private static UpstreamLeaseMismatchException BuildSafeLeaseMismatch(
        UpstreamLeaseMismatchException mismatch, string token)
    {
        var branch = Scrub(mismatch.Branch, token);
        try
        {
            Validation.ValidateBranchName(branch, nameof(mismatch.Branch));
        }
        catch (ArgumentException)
        {
            branch = "(unverifiable branch)";
        }
        var expected = SafeSha(mismatch.ExpectedSha);
        var actual = string.IsNullOrEmpty(mismatch.ActualSha) ? null : SafeSha(mismatch.ActualSha);
        return new UpstreamLeaseMismatchException(branch, expected, actual);
    }

    private static UpstreamOwnedBranchDivergedException BuildSafeDivergedHistory(
        UpstreamOwnedBranchDivergedException diverged, string token)
    {
        var branch = Scrub(diverged.Branch, token);
        try
        {
            Validation.ValidateBranchName(branch, nameof(diverged.Branch));
        }
        catch (ArgumentException)
        {
            branch = "(unverifiable branch)";
        }
        return new UpstreamOwnedBranchDivergedException(branch, SafeSha(diverged.RemoteSha), SafeSha(diverged.LocalSha));
    }

    private static string SafeSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
            return "(unknown)";
        try
        {
            Validation.ValidateCommitSha(sha, "sha");
            return sha;
        }
        catch (ArgumentException)
        {
            return "(unverifiable)";
        }
    }

    private static string SanitizeForLog(string? value) =>
        value?.Replace("\n", "\\n", StringComparison.Ordinal)
              .Replace("\r", "\\r", StringComparison.Ordinal) ?? "(null)";

    private string BuildPrTitle(string title, string workBranch)
    {
        if (string.IsNullOrEmpty(_opts.PullRequestTitleTemplate))
            return title;
        // Replace {branch} from the template first so that a user-supplied title
        // containing the literal text "{branch}" is not expanded in the second pass.
        return _opts.PullRequestTitleTemplate
            .Replace("{branch}", workBranch, StringComparison.Ordinal)
            .Replace("{title}", title, StringComparison.Ordinal);
    }

    private static UpstreamPushReconcileStrategy ToReconcileStrategy(string mergeMethod)
        => mergeMethod.Equals("rebase", StringComparison.OrdinalIgnoreCase)
            ? UpstreamPushReconcileStrategy.Rebase
            : UpstreamPushReconcileStrategy.Merge;
}

public sealed record GitHubUpstreamOptions
{
    public required string Owner { get; init; }
    public required string Repository { get; init; }
    /// <summary>GitHub PAT or fine-grained token. Never logged, never on argv.</summary>
    public string? Token { get; init; }
    public IGitHubTokenProvider? TokenProvider { get; init; }
    public string MergeMethod { get; init; } = "merge";
    public bool AutoMerge { get; init; }
    public string? PullRequestTitleTemplate { get; init; }

    /// <summary>LLM-generated PR description settings.</summary>
    public PrDescriptionOptions PrDescription { get; init; } = new();

    // Prevent the auto-generated record ToString() from rendering Token in plaintext
    // (e.g. when the instance is passed to a structured logger via {Opts}).
    public override string ToString() =>
        $"GitHubUpstreamOptions {{ Owner = {Owner}, Repository = {Repository}, Token = ***, MergeMethod = {MergeMethod}, AutoMerge = {AutoMerge} }}";
}

internal sealed class FixedGitHubTokenProvider(string token) : IGitHubTokenProvider
{
    public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(token);
}

// Internal DTOs — only used for GitHub REST serialisation, never exposed.

internal sealed record GitHubCreatePrRequest(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("head")] string Head,
    [property: JsonPropertyName("base")] string Base);

internal sealed record GitHubCreateIssueCommentRequest(
    [property: JsonPropertyName("body")] string Body);

internal sealed record GitHubClosePullRequestRequest(
    [property: JsonPropertyName("state")] string State = "closed");

internal sealed record GitHubMergeRequest(
    [property: JsonPropertyName("merge_method")] string MergeMethod,
    [property: JsonPropertyName("commit_title")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CommitTitle = null,
    [property: JsonPropertyName("commit_message")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CommitMessage = null);

internal sealed record GitHubPrResponse(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("html_url")] string? HtmlUrl,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("body")] string? Body = null,
    // Lifecycle/head projection: the resumed-PR path proves delivery and
    // reuses title/body for squash composition off this single GET instead
    // of issuing a second detail fetch.
    [property: JsonPropertyName("state")] string? State = null,
    [property: JsonPropertyName("merged")] bool Merged = false,
    [property: JsonPropertyName("merge_commit_sha")] string? MergeCommitSha = null,
    [property: JsonPropertyName("head")] GitHubPrEndpointDetail? Head = null,
    [property: JsonPropertyName("base")] GitHubPrEndpointDetail? Base = null);

internal sealed record GitHubMergeResponse(
    [property: JsonPropertyName("sha")] string? Sha,
    [property: JsonPropertyName("merged")] bool Merged = false);

internal sealed record GitHubPullRequestCommitResponse(
    [property: JsonPropertyName("commit")] GitHubPullRequestCommitDetail? Commit);

internal sealed record GitHubPullRequestCommitDetail(
    [property: JsonPropertyName("message")] string? Message);

internal sealed record GitHubMergesRequest(
    [property: JsonPropertyName("base")] string Base,
    [property: JsonPropertyName("head")] string Head,
    [property: JsonPropertyName("commit_message")] string CommitMessage);

internal sealed record GitHubCreateReleaseRequest(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("target_commitish")] string TargetCommitish,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("body")] string Body);

internal sealed record GitHubReleaseResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("html_url")] string? HtmlUrl);

internal sealed record GitHubPrSummary(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("head")] GitHubPrRef? Head,
    [property: JsonPropertyName("base")] GitHubPrRef? Base);

internal sealed record GitHubPrRef(
    [property: JsonPropertyName("ref")] string? Ref,
    [property: JsonPropertyName("sha")] string? Sha);

internal sealed record GitHubPrDetailMergeable(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("html_url")] string? HtmlUrl,
    [property: JsonPropertyName("mergeable")] bool? Mergeable,
    [property: JsonPropertyName("mergeable_state")] string? MergeableState,
    [property: JsonPropertyName("state")] string? State = null,
    [property: JsonPropertyName("merged")] bool Merged = false,
    [property: JsonPropertyName("merge_commit_sha")] string? MergeCommitSha = null,
    [property: JsonPropertyName("head")] GitHubPrEndpointDetail? Head = null,
    [property: JsonPropertyName("base")] GitHubPrEndpointDetail? Base = null);

internal sealed record GitHubPrEndpointDetail(
    [property: JsonPropertyName("ref")] string? Ref,
    [property: JsonPropertyName("sha")] string? Sha,
    [property: JsonPropertyName("user")] GitHubPrUser? User = null);

internal sealed record GitHubPrUser(
    [property: JsonPropertyName("login")] string? Login);
