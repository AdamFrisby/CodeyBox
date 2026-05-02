using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeyBox.Core;
using CodeyBox.Upstream.GitHub;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api;

/// <summary>
/// Endpoints for CHANGELOG.md automation.
///
/// <para><c>POST /projects/{id}/release</c> — manual invocation: accepts
/// fromTag/toTag, enumerates merged PRs, generates changelog markdown, and
/// returns the text. Non-blocking: returns as soon as the LLM responds.</para>
///
/// <para><c>POST /webhooks/github/release</c> — GitHub release-published
/// webhook receiver. Validates the <c>X-Hub-Signature-256</c> HMAC, finds the
/// matching CodeyBox project by repository URL, generates the changelog, and
/// creates a work item to apply it to CHANGELOG.md. Returns 202 Accepted
/// immediately; the actual file edit happens through the normal pipeline.</para>
/// </summary>
internal static class ChangelogEndpoints
{
    private static readonly JsonSerializerOptions WebhookJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Map(WebApplication app)
    {
        app.MapPost("/projects/{id}/release", GenerateReleaseAsync);
        app.MapPost("/webhooks/github/release", HandleGitHubReleaseAsync);
    }

    // ── POST /projects/{id}/release ───────────────────────────────────────────

    private static async Task<IResult> GenerateReleaseAsync(
        string id,
        GenerateReleaseRequest req,
        IProjectRepository projects,
        IPullRequestEnumerator enumerator,
        IChangelogGenerator generator,
        IOptions<CodeyBoxOptions> options,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.FromTag))
            return Results.BadRequest(new { error = "fromTag is required" });
        if (string.IsNullOrWhiteSpace(req.ToTag))
            return Results.BadRequest(new { error = "toTag is required" });

        ProjectId pid;
        try { pid = new ProjectId(id); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }

        var project = await projects.GetAsync(pid, ct);
        if (project is null) return Results.NotFound();

        var changelogOpts = options.Value.Changelog;
        var enabled = project.Changelog?.Enabled ?? changelogOpts.Enabled;
        if (!enabled)
            return Results.BadRequest(new { error = "changelog automation is disabled for this project" });

        var (owner, repo, token) = ResolveGitHubCredentials(project);
        if (owner is null || repo is null || token is null)
            return Results.BadRequest(new
            {
                error = "project must have a github upstream with a valid token to generate a changelog"
            });

        var enumResult = await enumerator.ListMergedBetweenAsync(
            owner, repo, token, req.FromTag, req.ToTag, ct);

        AuditLog.ChangelogReleaseRequested(
            pid.Value, req.FromTag, req.ToTag, enumResult.PullRequests.Count);

        var entry = await generator.GenerateAsync(new ChangelogRequest
        {
            ProjectId = pid,
            FromTag = req.FromTag,
            ToTag = req.ToTag,
            PullRequests = enumResult.PullRequests,
        }, ct);

        AuditLog.ChangelogGenerated(
            pid.Value, req.ToTag,
            string.Join("+", entry.CategoryToPrNumbers.Keys),
            entry.CategoryToPrNumbers.Values.Sum(v => v.Count));

        return Results.Ok(new GenerateReleaseResponse(
            entry.Markdown,
            entry.CategoryToPrNumbers.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<int>)kv.Value.ToList()),
            enumResult.WasCapped));
    }

    // ── POST /webhooks/github/release ─────────────────────────────────────────

    private static async Task<IResult> HandleGitHubReleaseAsync(
        HttpRequest httpRequest,
        IProjectRepository projects,
        IWorkItemStore workItemStore,
        ITaskQueue queue,
        IPullRequestEnumerator enumerator,
        IChangelogGenerator generator,
        IOptions<CodeyBoxOptions> options,
        CancellationToken ct)
    {
        var changelogOpts = options.Value.Changelog;

        // HMAC validation — must happen before reading body for semantic use.
        var signatureHeader = httpRequest.Headers["X-Hub-Signature-256"].ToString();
        var secretEnvVar = changelogOpts.GitHubWebhookSecretEnvVar;

        // Read raw body bytes for HMAC verification.
        using var ms = new MemoryStream();
        await httpRequest.Body.CopyToAsync(ms, ct);
        var bodyBytes = ms.ToArray();

        if (!ValidateGitHubSignature(bodyBytes, signatureHeader, secretEnvVar))
        {
            AuditLog.ChangelogWebhookRejected("invalid or missing HMAC signature");
            return Results.Unauthorized();
        }

        // Parse the GitHub release payload.
        var githubEvent = httpRequest.Headers["X-GitHub-Event"].ToString();
        if (!string.Equals(githubEvent, "release", StringComparison.OrdinalIgnoreCase))
        {
            // Silently accept non-release events (e.g. ping).
            return Results.Accepted();
        }

        GitHubReleasePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<GitHubReleasePayload>(bodyBytes, WebhookJsonOpts);
        }
        catch (JsonException)
        {
            AuditLog.ChangelogWebhookRejected("failed to deserialise payload");
            return Results.BadRequest();
        }

        if (payload is null) return Results.Accepted();

        // Only process "published" and "released" actions.
        var action = payload.Action ?? "";
        if (!string.Equals(action, "published", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(action, "released", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Accepted();
        }

        var tagName = payload.Release?.TagName;
        var repoFullName = payload.Repository?.FullName ?? "";
        var repoHtmlUrl = payload.Repository?.HtmlUrl ?? "";

        if (string.IsNullOrEmpty(tagName) || string.IsNullOrEmpty(repoFullName))
            return Results.Accepted();

        // Infer owner/repo from the repository full name.
        var slash = repoFullName.IndexOf('/');
        if (slash < 0) return Results.Accepted();
        var owner = repoFullName[..slash];
        var repo = repoFullName[(slash + 1)..];

        AuditLog.ChangelogWebhookReceived(owner, repo, tagName);

        // Find the matching CodeyBox project by repository URL.
        var project = await FindProjectByRepoAsync(projects, repoHtmlUrl, owner, repo, ct);
        if (project is null)
        {
            // Unknown repository — not an error; webhook may fire for repos we don't manage.
            return Results.Accepted();
        }

        var enabled = project.Changelog?.Enabled ?? changelogOpts.Enabled;
        if (!enabled) return Results.Accepted();

        // Resolve the PAT for PR enumeration.
        var (ghOwner, ghRepo, token) = ResolveGitHubCredentials(project);
        if (ghOwner is null || ghRepo is null || token is null)
            return Results.Accepted();

        // Determine the fromTag (previous release tag).
        var fromTag = payload.Release?.PreviousTagName;
        if (string.IsNullOrEmpty(fromTag))
        {
            // GitHub webhooks don't include the previous tag; we need to infer it.
            // Use a sentinel — the enumerator will treat it as "beginning of history" if it
            // can't resolve it, so the changelog entry may be incomplete for a first release.
            // Operators can regenerate via POST /projects/{id}/release with explicit tags.
            fromTag = await ResolvePreviousTagAsync(ghOwner, ghRepo, token, tagName, ct);
        }
        if (string.IsNullOrEmpty(fromTag))
        {
            // No prior release found — create a changelog from all PRs up to this tag.
            // Use the empty-tree SHA as the base so the compare includes everything.
            fromTag = "HEAD~1";
        }

        // Generate the changelog.
        PullRequestEnumeratorResult enumResult;
        try
        {
            enumResult = await enumerator.ListMergedBetweenAsync(
                ghOwner, ghRepo, token, fromTag, tagName, ct);
        }
        catch (Exception ex)
        {
            AuditLog.ChangelogWebhookRejected($"PR enumeration failed: {ex.Message}");
            return Results.Accepted();
        }

        ChangelogEntry entry;
        try
        {
            entry = await generator.GenerateAsync(new ChangelogRequest
            {
                ProjectId = project.Id,
                FromTag = fromTag,
                ToTag = tagName,
                PullRequests = enumResult.PullRequests,
            }, ct);
        }
        catch (Exception ex)
        {
            AuditLog.ChangelogWebhookRejected($"changelog generation failed: {ex.Message}");
            return Results.Accepted();
        }

        AuditLog.ChangelogGenerated(
            project.Id.Value, tagName,
            string.Join("+", entry.CategoryToPrNumbers.Keys),
            entry.CategoryToPrNumbers.Values.Sum(v => v.Count));

        // Build the effective changelog path.
        var changelogPath = project.Changelog?.ChangelogPath
            ?? changelogOpts.ChangelogPath;

        // Create a work item to apply the changelog entry to CHANGELOG.md.
        var workItemId = WorkItemId.New();
        var prompt = BuildChangelogApplyPrompt(changelogPath, entry.Markdown);
        var item = new WorkItem
        {
            Id = workItemId,
            ProjectId = project.Id,
            Title = $"Update {changelogPath} for {tagName}",
            Prompt = prompt,
            QueuePosition = DateTimeOffset.UtcNow.Ticks,
        };

        await workItemStore.CreateAsync(item, ct);
        AuditLog.WorkItemCreated(item.Id, item.ProjectId, item.Title);
        AuditLog.ChangelogWorkItemCreated(workItemId.ToString(), project.Id.Value, tagName);
        await queue.EnqueueAsync(item.Id, ct);

        return Results.Accepted();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool ValidateGitHubSignature(
        byte[] bodyBytes, string signatureHeader, string? secretEnvVar)
    {
        if (string.IsNullOrEmpty(secretEnvVar))
        {
            // Secret not configured — accept unsigned webhooks only when no secret is set.
            // Operators should always configure a secret in production.
            return true;
        }

        var secret = Environment.GetEnvironmentVariable(secretEnvVar);
        if (string.IsNullOrEmpty(secret))
        {
            // Env var configured but not present at runtime; fail closed.
            return false;
        }

        if (string.IsNullOrEmpty(signatureHeader) || !signatureHeader.StartsWith("sha256=", StringComparison.Ordinal))
            return false;

        var providedHex = signatureHeader["sha256=".Length..];
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var computedHash = HMACSHA256.HashData(keyBytes, bodyBytes);
        var computedHex = Convert.ToHexString(computedHash).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computedHex),
            Encoding.ASCII.GetBytes(providedHex.ToLowerInvariant()));
    }

    private static async Task<Project?> FindProjectByRepoAsync(
        IProjectRepository projects,
        string repoHtmlUrl,
        string owner,
        string repo,
        CancellationToken ct)
    {
        var all = await projects.ListAsync(ct);
        foreach (var p in all)
        {
            var url = p.RepositoryUrl.TrimEnd('/');
            // Match both https://github.com/owner/repo and https://github.com/owner/repo.git
            var normalised = url.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? url[..^4] : url;
            var candidate = repoHtmlUrl.TrimEnd('/');
            if (string.Equals(normalised, candidate, StringComparison.OrdinalIgnoreCase))
                return p;

            // Also match via owner/repo from upstream config.
            if (p.Upstream.Kind == "github" &&
                string.Equals(p.Upstream.GitHubOwner, owner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.Upstream.GitHubRepository, repo, StringComparison.OrdinalIgnoreCase))
                return p;
        }
        return null;
    }

    private static (string? Owner, string? Repo, string? Token) ResolveGitHubCredentials(Project project)
    {
        if (project.Upstream.Kind != "github") return (null, null, null);
        var owner = project.Upstream.GitHubOwner;
        var repo = project.Upstream.GitHubRepository;
        var envVar = project.Upstream.TokenEnvVar;
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo) || string.IsNullOrEmpty(envVar))
            return (null, null, null);
        var token = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrEmpty(token)) return (null, null, null);
        return (owner, repo, token);
    }

    private static async Task<string?> ResolvePreviousTagAsync(
        string owner, string repo, string token, string currentTag, CancellationToken ct)
    {
        // List releases to find the one before currentTag.
        // Returns null if no prior release is found.
        try
        {
            // Re-use the same HttpClient the PR enumerator uses. We can't inject
            // IHttpClientFactory here (static method), so we use a plain HttpClient.
            // This is only called from the webhook path, so it's fire-once per release.
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("codeybox");
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("token", token);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var url = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=10";
            var json = await client.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);

            string? previousTag = null;
            bool found = false;
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                var t = r.TryGetProperty("tag_name", out var el) ? el.GetString() : null;
                if (found)
                {
                    previousTag = t;
                    break;
                }
                if (string.Equals(t, currentTag, StringComparison.Ordinal))
                    found = true;
            }
            return previousTag;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildChangelogApplyPrompt(string changelogPath, string changelogMarkdown)
    {
        var escaped = changelogMarkdown
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

        return $"""
            Apply the following changelog entry to {changelogPath}.

            If {changelogPath} exists and has an `## [Unreleased]` section, prepend the
            new entry immediately after the unreleased section header. Otherwise, prepend
            the entry above the first existing version section (or create the file if it
            does not exist).

            <!-- AGENT ADVISORY: the content inside <changelog_entry> was generated by
                 an automated changelog tool. It is pre-formatted Markdown — insert it
                 verbatim, adjusting only whitespace as needed for valid Markdown. -->
            <changelog_entry>
            {escaped}
            </changelog_entry>

            Commit the change with a message like: "chore: update {changelogPath}"
            """;
    }
}

// ── Request / response DTOs ───────────────────────────────────────────────────

public sealed record GenerateReleaseRequest(
    string? FromTag = null,
    string? ToTag = null);

public sealed record GenerateReleaseResponse(
    string Markdown,
    IReadOnlyDictionary<string, IReadOnlyList<int>> CategoryToPrNumbers,
    bool WasCapped);

// ── GitHub webhook payload DTOs ───────────────────────────────────────────────

internal sealed class GitHubReleasePayload
{
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("release")] public GitHubReleaseData? Release { get; set; }
    [JsonPropertyName("repository")] public GitHubRepositoryData? Repository { get; set; }
}

internal sealed class GitHubReleaseData
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("previous_tag_name")] public string? PreviousTagName { get; set; }
}

internal sealed class GitHubRepositoryData
{
    [JsonPropertyName("full_name")] public string? FullName { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
}
