using CodeyBox.Core;

namespace CodeyBox.Upstream.GitHub;

/// <summary>
/// Delegating handler on the <c>github-upstream</c> named client — the single
/// seam every GitHub REST caller (upstream remote, PR enumerator, check-runs
/// client, app-token provider, connect endpoints) shares. It feeds each
/// response's rate-limit headers into <see cref="GitHubApiBudgetTracker"/>
/// and refuses to transmit into a budget window already known to be
/// exhausted, so no caller retries into a 403 before its reset.
/// </summary>
public sealed class GitHubRateLimitHandler : DelegatingHandler
{
    private readonly GitHubApiBudgetTracker _tracker;

    public GitHubRateLimitHandler(GitHubApiBudgetTracker tracker)
    {
        _tracker = tracker;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var budgetKey = GitHubApiBudgetTracker.BudgetKeyForToken(
            request.Headers.Authorization?.Parameter);

        // Hard blocks only: the per-class reserve is enforced by callers that
        // know their own priority (GitHubUpstreamRemote). Here we stop every
        // caller from retrying into a window the forge already told us is
        // closed — a send now would 403 regardless of who asks.
        var deferred = _tracker.CheckForSend(budgetKey, GitHubRequestClass.Pipeline);
        if (deferred is not null)
            throw deferred;

        var response = await base.SendAsync(request, cancellationToken);
        _tracker.ObserveResponse(budgetKey, response, BudgetPathLabel(request));
        return response;
    }

    private static string? BudgetPathLabel(HttpRequestMessage request)
    {
        // Unauthenticated calls — e.g. the GitHub App manifest-conversion
        // exchange, whose path carries the caller-supplied `code` — never
        // feed request paths into the tracker: those segments are
        // credential-bearing and would surface in logs and the admin
        // budget endpoint.
        if (request.Headers.Authorization is null)
            return null;
        return request.RequestUri?.AbsolutePath;
    }
}
