using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
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

        // Step 2: build PR description (LLM or static fallback)
        var description = await BuildDescriptionAsync(request, ct);

        // Step 3: open PR
        var prTitle = BuildPrTitle(request.Title, request.WorkBranch);
        GitHubPrResponse? pr;
        await using (var createPrScope = await TimingScope.BeginAsync(
            _timings, request.WorkItemId, "upstream_push", "upstream.api_create_pr",
            log: _log))
        {
            pr = await CreatePullRequestAsync(request, prTitle, description, ct);
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

        if (!_opts.AutoMerge)
        {
            return new UpstreamCompletionOutcome
            {
                BranchPushed = true,
                PullRequestUrl = pr.HtmlUrl,
                PullRequestNumber = pr.Number,
            };
        }

        // Step 4: auto-merge
        string? mergedSha;
        string? mergeNotes;
        await using (var mergeScope = await TimingScope.BeginAsync(
            _timings, request.WorkItemId, "upstream_push", "upstream.api_merge_pr",
            log: _log))
        {
            (mergedSha, mergeNotes) = await MergePullRequestAsync(pr.Number, ct);
        }

        if (mergedSha is not null)
        {
            _log.LogInformation("GitHub PR #{N} auto-merged: {Sha}", pr.Number, mergedSha);
            AuditLog.UpstreamPrMerged(pr.Number, mergedSha);
        }

        return new UpstreamCompletionOutcome
        {
            BranchPushed = true,
            PullRequestUrl = pr.HtmlUrl,
            PullRequestNumber = pr.Number,
            MergedSha = mergedSha,
            Notes = mergeNotes,
        };
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
    private async Task<string> BuildDescriptionAsync(UpstreamCompletionRequest request, CancellationToken ct)
    {
        var staticBody = request.Description ?? string.Empty;

        if (_descriptionGenerator is null || !_opts.PrDescription.Enabled)
            return staticBody + PrFooter;

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

            var generated = await _descriptionGenerator.GenerateAsync(genRequest, genCts.Token);
            // Redact the generated body — the LLM may echo secrets from the diff.
            generated = RawOutputRedactor.Redact(generated);
            _log.LogInformation("LLM-generated PR description produced ({Chars} chars)", generated.Length);
            return generated + PrFooter;
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

        return staticBody + PrFooter;
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

    private async Task<(string? Sha, string? Notes)> MergePullRequestAsync(int prNumber, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{_opts.Owner}/{_opts.Repository}/pulls/{prNumber}/merge";
        var body = new GitHubMergeRequest(_opts.MergeMethod);

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
            const string note = "Auto-merge blocked (405 — branch protection or PR not mergeable); PR left open";
            _log.LogWarning(
                "GitHub PUT /pulls/{N}/merge returned 405 (PR not mergeable, e.g. branch protection); leaving PR open",
                prNumber);
            AuditLog.UpstreamApiCallFailed("PUT /pulls/merge", 405, _opts.Owner, _opts.Repository);
            return (null, note);
        }

        if (!response.IsSuccessStatusCode)
            AuditLog.UpstreamApiCallFailed("PUT /pulls/merge", (int)response.StatusCode, _opts.Owner, _opts.Repository);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<GitHubMergeResponse>(ct);
        return (result?.Sha, null);
    }

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
    [property: JsonPropertyName("merge_method")] string MergeMethod);

internal sealed record GitHubPrResponse(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("html_url")] string? HtmlUrl);

internal sealed record GitHubMergeResponse(
    [property: JsonPropertyName("sha")] string? Sha);

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
