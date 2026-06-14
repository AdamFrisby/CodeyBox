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
        _timings = timings;
        _descriptionGenerator = descriptionGenerator;
        if (string.IsNullOrEmpty(_opts.Token))
            throw new ArgumentException("GitHub PAT must be provided", nameof(opts));
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
        using var askpass = GitCredentialHelper.CreateAskPassFor(_opts.Token, "x-access-token");
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
            var scrubbed = Scrub(ex.Message);
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

        // Step 1: push work branch
        var repoUrl = RepoUrl();
        using var askpass = GitCredentialHelper.CreateAskPassFor(_opts.Token, "x-access-token");
        await using (var pushScope = await TimingScope.BeginAsync(
            _timings, request.WorkItemId, "upstream_push", "upstream.push_branch",
            log: _log))
        {
            try
            {
                await _gitHost.PushToUpstreamAsync(
                    request.RepositoryId,
                    repoUrl,
                    request.WorkBranch,
                    askpass.Environment,
                    ToReconcileStrategy(request.MergeMethod),
                    ct);
            }
            catch (Exception ex)
            {
                // Log only the scrubbed message at Debug; the raw exception object is
                // withheld because git can echo credential material on auth failures.
                var scrubbed = Scrub(ex.Message);
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
            if (IsSquashMerge(_opts.MergeMethod))
            {
                var existing = await TryFetchPullRequestAsync(existingPr, ct);
                if (existing is not null)
                {
                    if (!string.IsNullOrWhiteSpace(existing.HtmlUrl))
                        prHtmlUrl = existing.HtmlUrl;
                    if (!string.IsNullOrWhiteSpace(existing.Title))
                        prTitle = existing.Title;
                    if (!string.IsNullOrWhiteSpace(existing.Body))
                        prDescription = new PrDescriptionResult(existing.Body, Generated: _opts.PrDescription.Enabled);
                }
            }
        }
        else
        {
            prDescription = await BuildDescriptionAsync(request, ct);
            GitHubPrResponse? pr;
            await using (var createPrScope = await TimingScope.BeginAsync(
                _timings, request.WorkItemId, "upstream_push", "upstream.api_create_pr",
                log: _log))
            {
                pr = await CreatePullRequestAsync(request, prTitle, prDescription.Body, ct);
            }

            if (pr is null)
            {
                // 422 — branch already has an open PR or the request was otherwise
                // unprocessable; leave it open for a human to sort out.
                return new UpstreamCompletionOutcome
                {
                    BranchPushed = true,
                    Notes = "PR creation skipped (422 — branch may already have an open PR)",
                };
            }

            _log.LogInformation("GitHub PR opened: {Url}", pr.HtmlUrl);
            AuditLog.UpstreamPrOpened(pr.Number, pr.HtmlUrl, request.WorkBranch, request.BaseBranch);

            if (pr.HtmlUrl is null)
                _log.LogWarning("GitHub PR response did not include html_url; pull_request_opened webhook event will not fire");

            prNumber = pr.Number;
            prHtmlUrl = pr.HtmlUrl;
        }

        if (!_opts.AutoMerge)
        {
            return new UpstreamCompletionOutcome
            {
                BranchPushed = true,
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
            using var listReq = BuildRequest(HttpMethod.Get, url);
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
        using var req = BuildRequest(HttpMethod.Get, url);
        using var resp = await SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            AuditLog.UpstreamApiCallFailed($"GET /pulls/{number}", (int)resp.StatusCode, _opts.Owner, _opts.Repository);
            return null;
        }
        return await resp.Content.ReadFromJsonAsync<GitHubPrDetailMergeable>(ct);
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
        using var askpass = GitCredentialHelper.CreateAskPassFor(_opts.Token, "x-access-token");
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

        using var req = BuildRequest(HttpMethod.Post, url);
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

        using var req = BuildRequest(HttpMethod.Post, url);
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

        if (_descriptionGenerator is null || !_opts.PrDescription.Enabled)
            return new PrDescriptionResult(staticBody + PrFooter, Generated: false);

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

            var genRequest = new PullRequestDescriptionRequest
            {
                DiffSummary = redactedStat,
                FullDiff = redactedDiff,
                Title = request.Title,
                Prompt = redactedPrompt,
                AddressedFindings = request.AddressedFindings,
                AgentReasoningTail = agentTail,
            };

            var generationTask = _descriptionGenerator.GenerateAsync(genRequest, genCts.Token);
            var generated = await generationTask.WaitAsync(_opts.PrDescription.Timeout, ct);
            // Redact the generated body — the LLM may echo secrets from the diff.
            generated = RawOutputRedactor.Redact(generated);
            _log.LogInformation("LLM-generated PR description produced ({Chars} chars)", generated.Length);
            return new PrDescriptionResult(generated + PrFooter, Generated: true);
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

        return new PrDescriptionResult(staticBody + PrFooter, Generated: false);
    }

    private sealed record PrDescriptionResult(string Body, bool Generated);

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

    private static readonly Regex CollapseWhitespace = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PullRequestNumberSuffix = new(@"\s\(#\d+\)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
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

    private async Task<GitHubPrResponse?> CreatePullRequestAsync(
        UpstreamCompletionRequest request, string prTitle, string description, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{_opts.Owner}/{_opts.Repository}/pulls";
        var body = new GitHubCreatePrRequest(prTitle, description, request.WorkBranch, request.BaseBranch);

        using var req = BuildRequest(HttpMethod.Post, url);
        req.Content = JsonContent.Create(body);

        var postPrSw = Stopwatch.StartNew();
        using var response = await SendAsync(req, ct);
        postPrSw.Stop();
        CodeyBoxMeters.UpstreamApiCallDuration.Record(postPrSw.ElapsedMilliseconds,
            new KeyValuePair<string, object?>("endpoint", "POST /pulls"),
            new KeyValuePair<string, object?>("status_code", (int)response.StatusCode));

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            _log.LogWarning(
                "GitHub POST /pulls returned 422 for {Owner}/{Repo} head={WorkBranch} base={BaseBranch}; skipping PR creation",
                _opts.Owner, _opts.Repository, request.WorkBranch, request.BaseBranch);
            AuditLog.UpstreamApiCallFailed("POST /pulls", 422, _opts.Owner, _opts.Repository);
            return null;
        }

        if (!response.IsSuccessStatusCode)
            AuditLog.UpstreamApiCallFailed("POST /pulls", (int)response.StatusCode, _opts.Owner, _opts.Repository);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GitHubPrResponse>(ct)
            ?? throw new InvalidOperationException(
                $"GitHub POST /pulls returned success but response body could not be deserialised (head={request.WorkBranch})");
    }

    private async Task<GitHubPrResponse?> TryFetchPullRequestAsync(int prNumber, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.github.com/repos/{_opts.Owner}/{_opts.Repository}/pulls/{prNumber}";
            using var req = BuildRequest(HttpMethod.Get, url);

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

        using var req = BuildRequest(HttpMethod.Put, url);
        req.Content = JsonContent.Create(body);

        var putMergeSw = Stopwatch.StartNew();
        using var response = await SendAsync(req, ct);
        putMergeSw.Stop();
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
        return (result?.Sha, null, false);
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

        var commitTitle = BuildSquashCommitTitle(prTitle, prNumber);
        var commitMessage = BuildSquashCommitMessage(prDescription, commitMessages, completionRequest);
        return new GitHubMergeRequest(_opts.MergeMethod, commitTitle, commitMessage);
    }

    private async Task<IReadOnlyList<string>> FetchPullRequestCommitMessagesAsync(int prNumber, CancellationToken ct)
    {
        const int perPage = 100;
        const int maxPages = 10;
        var messages = new List<string>();

        for (var page = 1; page <= maxPages; page++)
        {
            var url = $"https://api.github.com/repos/{_opts.Owner}/{_opts.Repository}/pulls/{prNumber}/commits" +
                $"?per_page={perPage}&page={page}";
            using var req = BuildRequest(HttpMethod.Get, url);
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
                if (!string.IsNullOrWhiteSpace(commit.Commit?.Message))
                    messages.Add(commit.Commit.Message);

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

        return PullRequestNumberSuffix.IsMatch(title)
            ? title
            : $"{title} (#{prNumber})";
    }

    private static string BuildSquashCommitMessage(
        PrDescriptionResult? prDescription,
        IReadOnlyList<string> commitMessages,
        UpstreamCompletionRequest completionRequest)
    {
        var promptRevision =
            ExtractLastPromptRevision(commitMessages) ??
            ExtractLastPromptRevision(prDescription?.Body) ??
            completionRequest.PromptRevision;

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
        foreach (var paragraph in ExtractCleanParagraphs(message, stopAtPrFooter: false))
        {
            if (!IsPureIterationNoise(paragraph))
                yield return paragraph;
        }
    }

    private static string CleanProseForCommitMessage(string? text)
        => FormatCommitBody(ExtractCleanParagraphs(text, stopAtPrFooter: true));

    private static IEnumerable<string> ExtractCleanParagraphs(string? text, bool stopAtPrFooter)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var current = new StringBuilder();

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmedRaw = rawLine.Trim();
            if (stopAtPrFooter && trimmedRaw == "---")
                break;

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

    private static int? ExtractLastPromptRevision(IReadOnlyList<string> commitMessages)
    {
        int? result = null;
        foreach (var message in commitMessages)
            if (ExtractLastPromptRevision(message) is { } rev)
                result = rev;
        return result;
    }

    private static int? ExtractLastPromptRevision(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        int? result = null;
        foreach (Match match in PromptRevisionTrailer.Matches(text))
            if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var rev))
                result = rev;
        return result;
    }

    private static bool IsSquashMerge(string mergeMethod)
        => mergeMethod.Equals("squash", StringComparison.OrdinalIgnoreCase);

    private HttpRequestMessage BuildRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("token", _opts.Token);
        return req;
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        => _httpClientFactory.CreateClient("github-upstream").SendAsync(req, ct);

    private string RepoUrl() => $"https://github.com/{_opts.Owner}/{_opts.Repository}.git";

    private string Scrub(string message) =>
        message.Replace(_opts.Token, "***", StringComparison.Ordinal);

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
    public required string Token { get; init; }
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

// Internal DTOs — only used for GitHub REST serialisation, never exposed.

internal sealed record GitHubCreatePrRequest(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("head")] string Head,
    [property: JsonPropertyName("base")] string Base);

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
    [property: JsonPropertyName("body")] string? Body = null);

internal sealed record GitHubMergeResponse(
    [property: JsonPropertyName("sha")] string? Sha);

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
    [property: JsonPropertyName("mergeable_state")] string? MergeableState);
