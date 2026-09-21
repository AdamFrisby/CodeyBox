using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Logging;

namespace CodeyBox.ForgejoUpstreamPlugin;

/// <summary>
/// Upstream remote for Forgejo (self-hosted). One project, one plugin.
/// Off unless an operator enables it: the assembly must be allowlisted and a
/// project must set <c>Upstream.Kind = "forgejo"</c>.
///
/// <para>Core lifecycle (via <see cref="IUpstreamRemote"/>): pushes the work
/// branch through the host git module, opens a pull request with Forgejo's
/// <c>/api/v1/repos/{owner}/{repo}/pulls</c> endpoint, optionally auto-merges
/// it, merges upstream branches for release sync, fetches base branches, and
/// lists/reads open PRs. Extended surfaces (reviews, checks, comments,
/// webhooks, metadata) live in <c>ForgejoUpstreamRemote.Extended.cs</c>.</para>
///
/// <para>Configuration: per-project <c>Upstream.PluginConfig</c> keys
/// <c>BaseUrl</c>, <c>Owner</c>, <c>Repository</c> (optional <c>PageSize</c>,
/// <c>MaxListPages</c>), read via
/// <c>IUpstreamPluginHost.GetProjectUpstreamConfig</c>. Calls without a
/// project context (sweeps, reads, merges) fall back to the plugin-scoped
/// <c>CodeyBox:Plugins:codeybox.forgejo-upstream</c> section. Credentials
/// never come from configuration: the token is read from the environment
/// variable named by <c>Upstream.TokenEnvVar</c> (forwarded on the completion
/// request) and is held only by this remote — sandboxes never see it.</para>
/// </summary>
[CodeyBoxPlugin(
    id: "codeybox.forgejo-upstream",
    displayName: "Forgejo Upstream Remote",
    minHostApiVersion: "1.0")]
public sealed partial class ForgejoUpstreamRemote : IUpstreamRemote, IPluginInitializer
{
    public const string HttpClientName = "forgejo-upstream";

    private const int DefaultPageSize = 50;
    private const int DefaultMaxListPages = 10;
    private const int MaxTitleLength = 512;
    private const int MaxBodyLength = 100_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IGitHost _gitHost;
    private readonly IHttpClientFactory _httpClientFactory;

    private IPluginHost _host = null!;
    private IUpstreamPluginHost _upstreamHost = null!;

    public ForgejoUpstreamRemote(IGitHost gitHost, IHttpClientFactory httpClientFactory)
    {
        _gitHost = gitHost;
        _httpClientFactory = httpClientFactory;
    }

    public string Name => "forgejo";

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        _host = context.Host;
        _upstreamHost = _host as IUpstreamPluginHost
            ?? throw new InvalidOperationException(
                "Forgejo upstream remote requires a host exposing IUpstreamPluginHost.");
        context.Logger.LogInformation("ForgejoUpstreamRemote initialized");
        return Task.CompletedTask;
    }

    // Push-only flows carry no project context, so per-project PluginConfig
    // (BaseUrl/Owner/Repository) is unreachable here. The orchestrator's
    // primary path is CompleteAsync, which pushes the work branch itself.
    public Task<UpstreamPushResult> PushAsync(
        string repositoryId, string branch, CancellationToken ct = default)
        => Task.FromResult(new UpstreamPushResult(
            false, "push-only not supported by this plugin; use CompleteAsync"));

    /// <summary>
    /// Full Forgejo completion flow: push work branch, open a PR (or reuse
    /// <see cref="UpstreamCompletionRequest.ExistingPullRequestNumber"/> from
    /// a prior race-recovery attempt), optionally auto-merge it.
    /// Transient forge failures throw so the orchestrator retries; soft
    /// outcomes (PR already exists, merge blocked) return partial results.
    /// </summary>
    public async Task<UpstreamCompletionOutcome> CompleteAsync(
        UpstreamCompletionRequest request, CancellationToken ct = default)
    {
        ValidateBranch(request.WorkBranch, nameof(request.WorkBranch));
        ValidateBranch(request.BaseBranch, nameof(request.BaseBranch));

        var config = ResolveProjectConfig(request.ProjectId);
        var token = ResolveToken(request.TokenEnvVar);
        if (token is null)
            _host.Logger.LogWarning(
                "Forgejo project {Project}: no token resolved; attempting anonymous access",
                request.ProjectId);

        using var auth = ForgejoGitAuthScope.Create(token);
        try
        {
            await _gitHost.PushToUpstreamAsync(
                request.RepositoryId,
                config.GitUrl,
                request.WorkBranch,
                auth.Environment,
                ToReconcileStrategy(request.MergeMethod),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ForgejoUpstreamException(
                $"Failed to push work branch '{SanitizeForLog(request.WorkBranch)}': {Scrub(ex.Message, token)}",
                ex);
        }

        var mergeMethod = ToForgejoMergeAction(request.MergeMethod);
        long prNumber;
        string? prUrl;
        if (request.ExistingPullRequestNumber is { } existing)
        {
            var reused = await GetPullAsync(config, token, existing, ct)
                ?? throw new ForgejoUpstreamException(
                    $"Forgejo PR #{existing} from a prior attempt is no longer available.");
            prNumber = reused.EffectiveNumber;
            prUrl = reused.HtmlUrl ?? config.PullUrl(prNumber);
        }
        else
        {
            var created = await CreatePullAsync(config, token, request, ct);
            if (created is null)
            {
                return new UpstreamCompletionOutcome
                {
                    BranchPushed = true,
                    Notes = "PR creation skipped (branch already has an open PR); leaving it for a human",
                };
            }

            prNumber = created.EffectiveNumber;
            prUrl = created.HtmlUrl ?? config.PullUrl(prNumber);
            _host.Logger.LogInformation("Forgejo PR #{Number} opened: {Url}", prNumber, prUrl);
        }

        if (!request.AutoMerge)
        {
            return new UpstreamCompletionOutcome
            {
                BranchPushed = true,
                PullRequestUrl = prUrl,
                PullRequestNumber = (int)prNumber,
            };
        }

        var (mergedSha, mergeNotes) = await MergePullAsync(config, token, prNumber, mergeMethod, ct);
        if (mergedSha is not null)
            _host.Logger.LogInformation("Forgejo PR #{Number} auto-merged: {Sha}", prNumber, mergedSha);

        return new UpstreamCompletionOutcome
        {
            BranchPushed = true,
            PullRequestUrl = prUrl,
            PullRequestNumber = (int)prNumber,
            MergedSha = mergedSha,
            Notes = mergeNotes,
        };
    }

    /// <summary>
    /// Merges <paramref name="sourceBranch"/> into <paramref name="targetBranch"/>
    /// on the Forgejo instance via a host-side temp clone (Forgejo exposes no
    /// server-side branch-to-branch merge; the release flow only reaches this
    /// provider through the contract, which carries no project context, so the
    /// plugin-scoped repository is used). Returns false on merge conflict,
    /// throws on infrastructure failures.
    /// </summary>
    public async Task<bool> TryMergeUpstreamBranchAsync(
        string targetBranch, string sourceBranch, CancellationToken ct = default)
    {
        ValidateBranch(targetBranch, nameof(targetBranch));
        ValidateBranch(sourceBranch, nameof(sourceBranch));
        var config = ResolveScopedConfig()
            ?? throw new InvalidOperationException(
                "Forgejo upstream merge requires plugin-scoped BaseUrl/Owner/Repository " +
                "under CodeyBox:Plugins:codeybox.forgejo-upstream.");
        var token = ResolveToken(null);

        var stagingRoot = Path.Combine(Path.GetTempPath(), "codeybox-forgejo-sync-" + Guid.NewGuid().ToString("N")[..8]);
        var cloneDir = Path.Combine(stagingRoot, "repo");
        try
        {
            Directory.CreateDirectory(cloneDir);
            using var auth = ForgejoGitAuthScope.Create(token);

            var clone = await ForgejoGitRunner.RunAsync(
                stagingRoot, auth.Environment, ct,
                "clone", "--branch", targetBranch, "--single-branch", "--", config.GitUrl, cloneDir);
            if (clone.ExitCode != 0)
                throw new ForgejoUpstreamException(
                    $"Forgejo git clone of '{SanitizeForLog(targetBranch)}' failed: {Scrub(clone.Stderr, token)}");

            var fetch = await ForgejoGitRunner.RunAsync(
                cloneDir, auth.Environment, ct, "fetch", "origin", sourceBranch);
            if (fetch.ExitCode != 0)
                throw new ForgejoUpstreamException(
                    $"Forgejo git fetch of '{SanitizeForLog(sourceBranch)}' failed: {Scrub(fetch.Stderr, token)}");

            var merge = await ForgejoGitRunner.RunAsync(
                cloneDir, auth.Environment, ct, "merge", "FETCH_HEAD", "--no-edit", "--no-ff");
            if (merge.ExitCode != 0)
            {
                await ForgejoGitRunner.RunAsync(cloneDir, auth.Environment, ct, "merge", "--abort");
                return false;
            }

            var push = await ForgejoGitRunner.RunAsync(
                cloneDir, auth.Environment, ct, "push", "origin", targetBranch);
            if (push.ExitCode != 0)
                throw new ForgejoUpstreamException(
                    $"Forgejo git push of '{SanitizeForLog(targetBranch)}' failed: {Scrub(push.Stderr, token)}");

            return true;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingRoot))
                    Directory.Delete(stagingRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of a temp directory.
            }
        }
    }

    /// <summary>
    /// Fetches <paramref name="baseBranch"/> from the plugin-scoped Forgejo
    /// repository into the host bare repo. Returns null when the plugin is
    /// not scoped to a repository (unsupported, non-fatal) or the branch is
    /// not advertised upstream.
    /// </summary>
    public async Task<string?> FetchBaseBranchAsync(
        string repositoryId, string baseBranch, CancellationToken ct = default)
    {
        var config = ResolveScopedConfig();
        if (config is null)
            return null;
        var token = ResolveToken(null);
        using var auth = ForgejoGitAuthScope.Create(token);
        try
        {
            return await _gitHost.FetchUpstreamBranchAsync(
                repositoryId, config.GitUrl, baseBranch, auth.Environment, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ForgejoUpstreamException(
                $"Failed to fetch base branch '{SanitizeForLog(baseBranch)}' from Forgejo: {Scrub(ex.Message, token)}",
                ex);
        }
    }

    // ------------------------------------------------------------------
    // Pull request core: create / read / merge / list
    // ------------------------------------------------------------------

    private async Task<ForgejoPull?> CreatePullAsync(
        ForgejoEndpointConfig config, string? token, UpstreamCompletionRequest request, CancellationToken ct)
    {
        using var response = await SendForgejoAsync(
            HttpMethod.Post, config.ReposPath("pulls"), token, ct,
            new
            {
                title = Truncate(request.Title, MaxTitleLength),
                body = Truncate(request.Description ?? string.Empty, MaxBodyLength),
                head = request.WorkBranch,
                @base = request.BaseBranch,
            });

        // The branch already has an open PR (or head==base / no diff): a soft
        // outcome, not an infrastructure failure — leave it for a human.
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
        {
            _host.Logger.LogWarning(
                "Forgejo: PR create for branch '{Branch}' returned {Status}; treating as already-exists",
                SanitizeForLog(request.WorkBranch), (int)response.StatusCode);
            return null;
        }

        await EnsureSuccessAsync(response, "create pull request", ct);
        return await ReadPullAsync(response, ct);
    }

    private async Task<ForgejoPull?> GetPullAsync(
        ForgejoEndpointConfig config, string? token, long number, CancellationToken ct)
    {
        using var response = await SendForgejoAsync(
            HttpMethod.Get, config.ReposPath($"pulls/{number}"), token, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, "read pull request", ct);
        return await ReadPullAsync(response, ct);
    }

    private async Task<(string? Sha, string? Notes)> MergePullAsync(
        ForgejoEndpointConfig config, string? token, long number, string doAction, CancellationToken ct)
    {
        using var response = await SendForgejoAsync(
            HttpMethod.Post, config.ReposPath($"pulls/{number}/merge"), token, ct,
            new { Do = doAction });

        // Not mergeable right now (checks pending, conflicts, protection):
        // soft outcome — leave the PR open for a human.
        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict)
        {
            _host.Logger.LogWarning(
                "Forgejo POST /pulls/{Number}/merge returned {Status}; leaving PR open",
                number, (int)response.StatusCode);
            return (null, "Auto-merge blocked (PR not mergeable or branch protection); PR left open");
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new ForgejoUpstreamException($"Forgejo PR #{number} was not found for merging.");

        await EnsureSuccessAsync(response, "merge pull request", ct);

        var refreshed = await GetPullAsync(config, token, number, ct);
        if (refreshed?.Merged == true)
            return (refreshed.MergeCommitSha, null);
        return (null, "Merge call succeeded but the PR does not report merged; leaving it for a human");
    }

    // ------------------------------------------------------------------
    // Configuration
    // ------------------------------------------------------------------

    internal sealed record ForgejoEndpointConfig(
        string ApiBaseUrl, string WebBaseUrl, string Owner, string Repository, int PageSize, int MaxListPages)
    {
        public string ReposPath(string relative) =>
            $"repos/{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(Repository)}/{relative}";

        public string GitUrl => $"{WebBaseUrl}/{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(Repository)}.git";

        public string PullUrl(long number) =>
            $"{WebBaseUrl}/{Owner}/{Repository}/pulls/{number}";
    }

    private ForgejoEndpointConfig ResolveProjectConfig(ProjectId projectId)
    {
        var project = _upstreamHost.GetProjectUpstreamConfig(projectId);
        var scoped = _host.ScopedConfig;
        string? Get(string key) =>
            project.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
                ? v
                : scoped[key];

        if (!TryBuildConfig(Get("BaseUrl"), Get("Owner"), Get("Repository"), Get("PageSize"), Get("MaxListPages"),
                out var config, out var error))
            throw new InvalidOperationException($"Project {projectId}: Forgejo upstream {error}");
        return config;
    }

    private ForgejoEndpointConfig? ResolveScopedConfig()
    {
        var scoped = _host.ScopedConfig;
        if (!TryBuildConfig(scoped["BaseUrl"], scoped["Owner"], scoped["Repository"],
                scoped["PageSize"], scoped["MaxListPages"], out var config, out _))
            return null;
        return config;
    }

    private static bool TryBuildConfig(
        string? baseUrl, string? owner, string? repository, string? pageSize, string? maxPages,
        out ForgejoEndpointConfig config, out string error)
    {
        config = null!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository))
        {
            error = "requires BaseUrl, Owner and Repository (Upstream.PluginConfig, falling back to plugin-scoped config)";
            return false;
        }

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var apiUri)
            || (apiUri.Scheme != Uri.UriSchemeHttp && apiUri.Scheme != Uri.UriSchemeHttps))
        {
            error = $"BaseUrl '{baseUrl}' must be an absolute http(s) URL";
            return false;
        }
        if (!string.IsNullOrEmpty(apiUri.UserInfo))
        {
            error = "BaseUrl must not embed credentials";
            return false;
        }

        // Accept the instance root or the API root; normalize to the API base.
        var apiBase = apiUri.ToString().TrimEnd('/');
        if (!apiBase.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
            apiBase += "/api/v1";
        var webBase = apiBase.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase)
            ? apiBase[..^"/api/v1".Length]
            : apiBase;

        if (owner.Any(c => char.IsWhiteSpace(c) || (char.IsControl(c) && c != '\t'))
            || repository.Any(c => char.IsWhiteSpace(c) || (char.IsControl(c) && c != '\t')))
        {
            error = "Owner and Repository must not contain whitespace or control characters";
            return false;
        }

        if (!TryBoundedInt(pageSize, DefaultPageSize, 1, 100, out var pageSizeValue))
        {
            error = "PageSize must be an integer 1..100";
            return false;
        }
        if (!TryBoundedInt(maxPages, DefaultMaxListPages, 1, 50, out var maxPagesValue))
        {
            error = "MaxListPages must be an integer 1..50";
            return false;
        }

        config = new ForgejoEndpointConfig(apiBase, webBase, owner.Trim(), repository.Trim(), pageSizeValue, maxPagesValue);
        return true;
    }

    private static bool TryBoundedInt(string? raw, int @default, int min, int max, out int value)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = @default;
            return true;
        }
        if (!int.TryParse(raw.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out value)
            || value < min || value > max)
            return false;
        return true;
    }

    private string? ResolveToken(string? requestTokenEnvVar)
    {
        var name = !string.IsNullOrWhiteSpace(requestTokenEnvVar)
            ? requestTokenEnvVar
            : _host.ScopedConfig["TokenEnvVar"];
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return Environment.GetEnvironmentVariable(name);
    }

    // ------------------------------------------------------------------
    // HTTP plumbing: auth, failure classification, pagination
    // ------------------------------------------------------------------

    private async Task<HttpResponseMessage> SendForgejoAsync(
        HttpMethod method, string relativePath, string? token, CancellationToken ct, object? body = null)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, relativePath);
        request.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", token);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            throw new ForgejoUpstreamException(
                $"Forgejo instance unreachable: {Scrub(ex.Message, token)}", ex);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            throw new ForgejoUpstreamException(
                "Forgejo rejected the request as unauthorised (401): check the token and its scopes.",
                HttpStatusCode.Unauthorized);
        }
        if (response.StatusCode == HttpStatusCode.Forbidden && IsRateLimited(response))
        {
            var retryAfter = ParseRetryAfter(response);
            response.Dispose();
            throw new ForgejoUpstreamException(
                $"Forgejo rate limit exceeded{(retryAfter is null ? string.Empty : $"; retry after {retryAfter}s")}.",
                HttpStatusCode.Forbidden, retryAfter);
        }
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            response.Dispose();
            throw new ForgejoUpstreamException(
                "Forgejo forbade the request (403): the token lacks permission for this operation.",
                HttpStatusCode.Forbidden);
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = ParseRetryAfter(response);
            response.Dispose();
            throw new ForgejoUpstreamException(
                $"Forgejo rate limit exceeded{(retryAfter is null ? string.Empty : $"; retry after {retryAfter}s")}.",
                HttpStatusCode.TooManyRequests, retryAfter);
        }
        if ((int)response.StatusCode >= 500)
        {
            response.Dispose();
            throw new ForgejoUpstreamException(
                $"Forgejo instance failed with {(int)response.StatusCode} {response.StatusCode}.",
                response.StatusCode);
        }

        return response;
    }

    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var values))
        {
            foreach (var value in values)
            {
                if (value.Trim() == "0")
                    return true;
            }
        }
        return false;
    }

    private static int? ParseRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
            return null;
        foreach (var value in values)
        {
            if (int.TryParse(value.Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                return seconds;
            if (DateTimeOffset.TryParse(value.Trim(), out var date))
            {
                var delta = date - DateTimeOffset.UtcNow;
                return delta.TotalSeconds > 0 ? (int)delta.TotalSeconds : 0;
            }
        }
        return null;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var detail = await ReadErrorDetailAsync(response, ct);
        throw new ForgejoUpstreamException(
            $"Forgejo {operation} failed with {(int)response.StatusCode} {response.StatusCode}{detail}.",
            response.StatusCode);
    }

    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
                return string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(message.GetString()))
                    return $": {Truncate(message.GetString()!, 300)}";
            }
            catch (JsonException)
            {
                // Fall through to the length-only detail below.
            }
            return $" (response body {body.Length} chars, not machine-readable)";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<IReadOnlyList<T>> GetPagedAsync<T>(
        ForgejoEndpointConfig config, string? token, string pathAndQuery, CancellationToken ct)
    {
        var items = new List<T>();
        for (var page = 1; page <= config.MaxListPages; page++)
        {
            var separator = pathAndQuery.Contains('?') ? "&" : "?";
            using var response = await SendForgejoAsync(
                HttpMethod.Get,
                $"{pathAndQuery}{separator}page={page}&limit={config.PageSize}",
                token, ct);
            await EnsureSuccessAsync(response, "list paged results", ct);
            var pageItems = await DeserializeAsync<IReadOnlyList<T>>(response, ct) ?? [];
            items.AddRange(pageItems);
            // A short page means the forge has nothing more; without this the
            // provider would either stop early (partial answer) or loop
            // pointlessly. The MaxListPages cap bounds the total instead.
            if (pageItems.Count < config.PageSize)
                break;
        }
        return items;
    }

    private static async Task<T?> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ForgejoUpstreamException(
                $"Forgejo returned a response this provider cannot parse: {ex.Message}", ex);
        }
    }

    private static async Task<ForgejoPull> ReadPullAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var pull = await DeserializeAsync<ForgejoPull>(response, ct);
        if (pull is null || pull.EffectiveNumber <= 0)
            throw new ForgejoUpstreamException("Forgejo returned a pull request without a usable number.");
        return pull;
    }

    // ------------------------------------------------------------------
    // Small pure helpers
    // ------------------------------------------------------------------

    private static void ValidateBranch(string branch, string paramName)
    {
        static bool HasInvalidChars(string s) =>
            s.Any(c => char.IsWhiteSpace(c) || (char.IsControl(c) && c != '\t'));
        if (string.IsNullOrEmpty(branch) || HasInvalidChars(branch))
            throw new ArgumentException(
                $"Branch contains invalid characters (whitespace/control chars not allowed): '{SanitizeForLog(branch)}'",
                paramName);
    }

    private static string ToForgejoMergeAction(string mergeMethod) =>
        mergeMethod.ToLowerInvariant() switch
        {
            "merge" => "merge",
            "squash" => "squash",
            "rebase" => "rebase",
            _ => throw new InvalidOperationException(
                $"Upstream MergeMethod '{SanitizeForLog(mergeMethod)}' is invalid for Forgejo; valid values: merge, squash, rebase"),
        };

    private static UpstreamPushReconcileStrategy ToReconcileStrategy(string mergeMethod) =>
        mergeMethod.Equals("rebase", StringComparison.OrdinalIgnoreCase)
            ? UpstreamPushReconcileStrategy.Rebase
            : UpstreamPushReconcileStrategy.Merge;

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "(empty)";
        var scrubbed = new string(value.Select(c => char.IsControl(c) ? '?' : c).ToArray());
        return scrubbed.Length > 256 ? scrubbed[..256] + "…" : scrubbed;
    }

    private static string Scrub(string message, string? token) =>
        string.IsNullOrEmpty(token) ? message : message.Replace(token, "[redacted]", StringComparison.Ordinal);

    private static string Truncate(string value, int maxLength) =>
        value.Length > maxLength ? value[..maxLength] : value;
}
