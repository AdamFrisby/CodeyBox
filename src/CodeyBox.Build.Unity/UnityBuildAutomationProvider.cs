using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeyBox.Core.ExternalBuilds;

namespace CodeyBox.Build.Unity;

/// <summary>
/// Unity Build Automation adapter behind the neutral
/// <see cref="IExternalBuildProvider"/> contract. Disabled by default and
/// registered under <c>unity-build-automation</c>; all dispatch goes through
/// the shared external-build lifecycle (intent-before-dispatch, uncertain
/// reconciliation by request identity, fenced writes, budgets, park/resume,
/// evidence gate), which this adapter reuses instead of forking.
/// Only operator-approved org/project/target/editor/platform combinations are
/// accepted (exact match); the API base address is a fixed constant so no
/// caller can redirect credential-bearing requests; the checkout commit comes
/// only from the host-published candidate ref. All provider output is
/// untrusted: bounded, redacted, and re-verified at each sink.
/// </summary>
public sealed class UnityBuildAutomationProvider : IExternalBuildProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        MaxDepth = 8,
    };

    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int MaxListedBuildsScanned = 50;
    private const string RequestIdPrefix = "request:";

    private readonly HttpClient _http;
    private readonly Func<UnityBuildAutomationOptions> _options;
    private readonly Func<ExternalBuildOptions> _frameworkOptions;
    private readonly Func<string, ExternalBuildTargetApproval?> _approvalLookup;
    private readonly IUnityBuildCredentialProvider _credentials;
    private readonly TimeProvider _clock;

    private readonly object _dedupGate = new();
    private readonly Dictionary<string, string> _requestRuns = new(StringComparer.Ordinal);

    /// <param name="http">Client whose <see cref="HttpClient.BaseAddress"/> must be the fixed API base.</param>
    /// <param name="options">Hot-reloadable adapter options (read per call).</param>
    /// <param name="frameworkOptions">Shared framework bounds (artifacts, diagnostics).</param>
    /// <param name="approvalLookup">
    /// Resolves the operator-approved neutral approval by Unity build-target
    /// id. In production this reads the shared
    /// <c>ExternalBuildOptions.ApprovedTargets</c> entry; tests supply isolated
    /// approvals. Never derived from repository content.
    /// </param>
    public UnityBuildAutomationProvider(
        HttpClient http,
        Func<UnityBuildAutomationOptions> options,
        Func<ExternalBuildOptions> frameworkOptions,
        Func<string, ExternalBuildTargetApproval?> approvalLookup,
        IUnityBuildCredentialProvider? credentials = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.BaseAddress is null
            || !string.Equals(http.BaseAddress.GetLeftPart(UriPartial.Authority),
                UnityBuildAutomationOptions.ApiBaseUrl, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Unity HttpClient must target the fixed API base '{UnityBuildAutomationOptions.ApiBaseUrl}'.",
                nameof(http));
        _http = http;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _frameworkOptions = frameworkOptions ?? throw new ArgumentNullException(nameof(frameworkOptions));
        _approvalLookup = approvalLookup ?? throw new ArgumentNullException(nameof(approvalLookup));
        _credentials = credentials ?? new NullUnityBuildCredentialProvider();
        _clock = clock ?? TimeProvider.System;
    }

    public string ProviderId => UnityBuildAutomationOptions.ProviderId;

    /// <summary>Unity builds from hosted Git; snapshot uploads are unsupported.</summary>
    public bool SupportsGitPublication => true;

    public bool SupportsSnapshotUpload => false;

    public async Task<ExternalBuildSubmitResult> SubmitAsync(
        ExternalBuildRecord intent, ExternalBuildSubmitInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(input);
        var options = _options();
        if (!options.Enabled)
            throw new ExternalBuildNotEnabledException();
        var approval = _approvalLookup(intent.Target.TargetId)
            ?? throw new UnityBuildTargetRejectedException(
                $"Unity build target '{intent.Target.TargetId}' is not operator-approved.");
        var target = UnityBuildTarget.Resolve(intent.Target.TargetId, approval, options);
        var commit = UnityBuildSourceValidator.RequireCommit(intent.Source, input, options);
        var sourceDigest = intent.Source.SourceDigestSha256;

        lock (_dedupGate)
        {
            if (!string.IsNullOrWhiteSpace(intent.RequestId)
                && _requestRuns.TryGetValue(intent.RequestId, out var known))
                return new ExternalBuildSubmitResult(true, known, null);
        }

        var token = await _credentials.GetApiTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new UnityBuildAuthException("Unity credential provider returned no token.");

        var existing = await FindBuildByLabelAsync(target, intent.RequestId, token, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.Commit?.Trim(), commit, StringComparison.OrdinalIgnoreCase))
                return new ExternalBuildSubmitResult(false, null,
                    "provider already holds a different commit under this request identity; refusing to adopt a stranger's build",
                    Uncertain: true);
            var adopted = RunId(target, existing.Build, commit, sourceDigest);
            Remember(intent.RequestId, adopted, options);
            return new ExternalBuildSubmitResult(true, adopted, null);
        }

        var body = JsonSerializer.Serialize(new UnityCreateBuildRequest
        {
            Commit = commit,
            CleanBuild = target.CleanBuild,
            Label = intent.RequestId,
            SourceRef = intent.Source.CandidateRef ?? string.Empty,
        }, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildPath(target, "builds"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        ApplyAuth(request, token);
        ApplyIdempotency(request, intent.RequestId);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not UnityBuildException)
        {
            throw new UnityBuildUnavailableException("Unity submit transport failed before acceptance.", ex);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                throw new UnityBuildAuthException(
                    $"Unity rejected submit credentials ({(int)response.StatusCode}).");
            if ((int)response.StatusCode == 429)
                throw RateLimited(await ReadErrorAsync(response, ct).ConfigureAwait(false), response);
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
                throw new UnityBuildUnavailableException(
                    "Unity rejected the build (unsupported editor/platform/configuration): "
                    + await ReadErrorAsync(response, ct).ConfigureAwait(false));
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var conflicted = await ReadJsonAsync<UnityCreateBuildResponse>(response, ct).ConfigureAwait(false);
                if (conflicted is not null && conflicted.Build > 0)
                {
                    var conflictRunId = RunId(target, conflicted.Build, commit, sourceDigest);
                    Remember(intent.RequestId, conflictRunId, options);
                    return new ExternalBuildSubmitResult(true, conflictRunId, null);
                }
                return new ExternalBuildSubmitResult(false, null,
                    "provider reported a conflicting build without identity", Uncertain: true);
            }
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout)
                    return new ExternalBuildSubmitResult(false, null,
                        "provider unavailable: " + await ReadErrorAsync(response, ct).ConfigureAwait(false),
                        Uncertain: true);
                throw new UnityBuildUnavailableException(
                    $"Unity submit failed ({(int)response.StatusCode}): "
                    + await ReadErrorAsync(response, ct).ConfigureAwait(false));
            }
            var created = await ReadJsonAsync<UnityCreateBuildResponse>(response, ct).ConfigureAwait(false);
            if (created is null || created.Build <= 0)
                return new ExternalBuildSubmitResult(false, null,
                    "provider accepted without a usable build number; refusing silent pass", Uncertain: true);
            var runId = RunId(target, created.Build, commit, sourceDigest);
            Remember(intent.RequestId, runId, options);
            return new ExternalBuildSubmitResult(true, runId, null);
        }
    }

    public async Task<ExternalBuildProviderStatus> GetStatusAsync(string providerRunId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerRunId);
        var options = _options();
        var framework = _frameworkOptions();
        if (providerRunId.StartsWith(RequestIdPrefix, StringComparison.Ordinal))
            return Unknown("no known run for this request identity; the service retries submit, which dedups by request identity");
        if (!TryParseRunId(providerRunId, out var coords, out var commit, out var sourceDigest))
            return Unknown("unrecognized Unity run identity; refusing silent pass");
        var approval = _approvalLookup(coords.BuildTargetId)
            ?? throw new UnityBuildTargetRejectedException(
                $"Unity build target '{coords.BuildTargetId}' is not operator-approved.");
        var target = UnityBuildTarget.Resolve(coords.BuildTargetId, approval, options);

        var token = await _credentials.GetApiTokenAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, BuildPath(target, $"builds/{coords.BuildNumber}"));
        ApplyAuth(request, token);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not UnityBuildException)
        {
            throw new UnityBuildUnavailableException("Unity status transport failed.", ex);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Unknown("provider reports no such build");
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                throw new UnityBuildAuthException(
                    $"Unity rejected status credentials ({(int)response.StatusCode}).");
            if ((int)response.StatusCode == 429)
                throw RateLimited(await ReadErrorAsync(response, ct).ConfigureAwait(false), response);
            if (!response.IsSuccessStatusCode)
                throw new UnityBuildUnavailableException(
                    $"Unity status failed ({(int)response.StatusCode}): "
                    + await ReadErrorAsync(response, ct).ConfigureAwait(false));
            var details = await ReadJsonAsync<UnityBuildDetails>(response, ct).ConfigureAwait(false);
            if (details is null)
                return Unknown("provider returned an unreadable build payload");
            return UnityBuildStatusMapper.Map(
                details, target, commit, sourceDigest, providerRunId,
                Math.Min(options.MaxDiagnosticsChars, framework.MaxDiagnosticsChars),
                framework.MaxArtifactBytes, options.AllowedArtifactHosts,
                _clock.GetUtcNow());
        }
    }

    public async Task<ExternalBuildCancelResult> CancelAsync(string providerRunId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerRunId);
        if (!TryParseRunId(providerRunId, out var coords, out _, out _))
            return new ExternalBuildCancelResult(false, "unrecognized Unity run identity");
        var approval = _approvalLookup(coords.BuildTargetId)
            ?? throw new UnityBuildTargetRejectedException(
                $"Unity build target '{coords.BuildTargetId}' is not operator-approved.");
        var target = UnityBuildTarget.Resolve(coords.BuildTargetId, approval, _options());
        var token = await _credentials.GetApiTokenAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, BuildPath(target, $"builds/{coords.BuildNumber}/cancel"));
        ApplyAuth(request, token);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not UnityBuildException)
        {
            throw new UnityBuildUnavailableException("Unity cancel transport failed.", ex);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone)
                return new ExternalBuildCancelResult(false, "provider reports no cancellable build (already terminal or unknown)");
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                throw new UnityBuildAuthException(
                    $"Unity rejected cancel credentials ({(int)response.StatusCode}).");
            if ((int)response.StatusCode == 429)
                throw RateLimited(await ReadErrorAsync(response, ct).ConfigureAwait(false), response);
            if (!response.IsSuccessStatusCode)
                throw new UnityBuildUnavailableException(
                    $"Unity cancel failed ({(int)response.StatusCode}): "
                    + await ReadErrorAsync(response, ct).ConfigureAwait(false));
            var payload = (await ReadTextAsync(response, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (payload.Contains("cancelpending") || payload.Contains("cancel_pending")
                || payload.Contains("cancelrequested") || payload.Contains("cancel_requested"))
                return new ExternalBuildCancelResult(false, "cancellation pending: provider has not stopped the build; keep polling");
            return new ExternalBuildCancelResult(true, "provider confirmed cancellation");
        }
    }

    public async Task<IReadOnlyList<ExternalBuildArtifactRef>> ListArtifactsAsync(
        string providerRunId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerRunId);
        var framework = _frameworkOptions();
        var options = _options();
        if (!TryParseRunId(providerRunId, out var coords, out _, out _))
            throw new UnityBuildArtifactException("Unrecognized Unity run identity.");
        var approval = _approvalLookup(coords.BuildTargetId)
            ?? throw new UnityBuildTargetRejectedException(
                $"Unity build target '{coords.BuildTargetId}' is not operator-approved.");
        var target = UnityBuildTarget.Resolve(coords.BuildTargetId, approval, options);
        var token = await _credentials.GetApiTokenAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, BuildPath(target, $"builds/{coords.BuildNumber}/artifacts"));
        ApplyAuth(request, token);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not UnityBuildException)
        {
            throw new UnityBuildArtifactException("Artifact listing transport failed.", ex);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new UnityBuildArtifactException("Provider reports no such build or the listing expired.");
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                throw new UnityBuildAuthException(
                    $"Unity rejected artifact credentials ({(int)response.StatusCode}).");
            if ((int)response.StatusCode == 429)
                throw RateLimited(await ReadErrorAsync(response, ct).ConfigureAwait(false), response);
            if (!response.IsSuccessStatusCode)
                throw new UnityBuildArtifactException(
                    $"Artifact listing failed ({(int)response.StatusCode}): "
                    + await ReadErrorAsync(response, ct).ConfigureAwait(false));
            var entries = await ReadJsonAsync<List<UnityBuildArtifactEntry>>(response, ct).ConfigureAwait(false)
                ?? [];
            var refs = new List<ExternalBuildArtifactRef>();
            foreach (var entry in entries.Take(framework.MaxArtifactsPerBuild + 1))
            {
                if (refs.Count >= framework.MaxArtifactsPerBuild)
                    break;
                var candidate = new ExternalBuildArtifactRef(
                    entry.Name ?? string.Empty,
                    Math.Max(0, entry.Size),
                    (entry.Sha256 ?? string.Empty).Trim().ToLowerInvariant(),
                    string.IsNullOrWhiteSpace(entry.MediaType) ? "application/octet-stream" : entry.MediaType);
                if (ExternalBuildArtifactGuard.ValidateRef(candidate, framework) is not null)
                    continue;
                if (ExternalBuildArtifactGuard.ValidateArtifactUrl(entry.Url, options.AllowedArtifactHosts) is not null)
                    continue;
                refs.Add(candidate);
            }
            return refs;
        }
    }

    public async Task<ExternalBuildArtifactPayload> ReadArtifactAsync(
        string providerRunId, string artifactName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactName);
        var framework = _frameworkOptions();
        if (artifactName.Length > ExternalBuildArtifactGuard.MaxArtifactNameChars
            || artifactName.Replace('\\', '/').StartsWith('/')
            || artifactName.Contains("..", StringComparison.Ordinal))
            throw new UnityBuildArtifactException("Artifact name escapes its directory.");
        var listed = await ListArtifactsAsync(providerRunId, ct).ConfigureAwait(false);
        var match = listed.FirstOrDefault(a => string.Equals(a.Name, artifactName, StringComparison.Ordinal));
        if (match is null)
            throw new UnityBuildArtifactException(
                $"Artifact '{artifactName}' is not in the run's authorized listing.");
        if (match.SizeBytes > framework.MaxArtifactBytes)
            throw new UnityBuildArtifactException("Artifact exceeds the size cap; refusing to buffer.");
        if (!TryParseRunId(providerRunId, out var coords, out _, out _))
            throw new UnityBuildArtifactException("Unrecognized Unity run identity.");
        var approval = _approvalLookup(coords.BuildTargetId)
            ?? throw new UnityBuildTargetRejectedException(
                $"Unity build target '{coords.BuildTargetId}' is not operator-approved.");
        var target = UnityBuildTarget.Resolve(coords.BuildTargetId, approval, _options());
        var token = await _credentials.GetApiTokenAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, BuildPath(target, $"builds/{coords.BuildNumber}/artifacts/{Uri.EscapeDataString(artifactName)}/content"));
        ApplyAuth(request, token);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not UnityBuildException)
        {
            throw new UnityBuildArtifactException("Artifact download transport failed.", ex);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone)
                throw new UnityBuildArtifactException("Artifact expired or is no longer downloadable.");
            if ((int)response.StatusCode == 429)
                throw RateLimited("artifact download throttled", response);
            if (!response.IsSuccessStatusCode)
                throw new UnityBuildArtifactException($"Artifact download failed ({(int)response.StatusCode}).");
            if (response.Content.Headers.ContentLength is { } length
                && length > framework.MaxArtifactBytes)
                throw new UnityBuildArtifactException("Artifact exceeds the size cap; refusing to buffer.");
            var bytes = await ReadCappedAsync(response, framework.MaxArtifactBytes, ct).ConfigureAwait(false);
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(digest, match.ContentDigestSha256, StringComparison.OrdinalIgnoreCase))
                throw new UnityBuildArtifactException("Artifact digest mismatch; refusing substituted bytes.");
            return new ExternalBuildArtifactPayload(match.Name, bytes, match.MediaType, digest);
        }
    }

    private async Task<UnityBuildSummary?> FindBuildByLabelAsync(
        UnityBuildTarget target, string requestId, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return null;
        using var request = new HttpRequestMessage(
            HttpMethod.Get, BuildPath(target, "builds?label=" + Uri.EscapeDataString(requestId.Trim())));
        ApplyAuth(request, token);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not UnityBuildException)
        {
            throw new UnityBuildUnavailableException("Unity reconcile-by-label transport failed.", ex);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                throw new UnityBuildAuthException(
                    $"Unity rejected reconcile credentials ({(int)response.StatusCode}).");
            if ((int)response.StatusCode == 429)
                throw RateLimited(await ReadErrorAsync(response, ct).ConfigureAwait(false), response);
            if (!response.IsSuccessStatusCode)
                throw new UnityBuildUnavailableException(
                    $"Unity reconcile failed ({(int)response.StatusCode}): "
                    + await ReadErrorAsync(response, ct).ConfigureAwait(false));
            var builds = await ReadJsonAsync<List<UnityBuildSummary>>(response, ct).ConfigureAwait(false)
                ?? [];
            foreach (var build in builds.Take(MaxListedBuildsScanned))
                if (build.Build > 0 && string.Equals(build.Label?.Trim(), requestId.Trim(), StringComparison.Ordinal))
                    return build;
            return null;
        }
    }

    private ExternalBuildProviderStatus Unknown(string detail) =>
        new(ExternalBuildExecutionPhase.Unknown, null, detail, _clock.GetUtcNow());

    private void Remember(string requestId, string runId, UnityBuildAutomationOptions options)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return;
        lock (_dedupGate)
        {
            if (_requestRuns.Count >= Math.Max(16, options.MaxDedupEntries))
            {
                var victim = _requestRuns.Keys.FirstOrDefault();
                if (victim is not null)
                    _requestRuns.Remove(victim);
            }
            _requestRuns[requestId] = runId;
        }
    }

    public sealed record UnityRunCoords(string OrganizationId, string ProjectId, string BuildTargetId, long BuildNumber);

    internal static string RunId(UnityBuildTarget target, long buildNumber, string commit, string sourceDigest) =>
        $"unity:{target.OrganizationId}/{target.ProjectId}/{target.BuildTargetId}/{buildNumber}/{commit.ToLowerInvariant()}/{sourceDigest.ToLowerInvariant()}";

    internal static bool TryParseRunId(
        string providerRunId, out UnityRunCoords coords, out string commit, out string sourceDigest)
    {
        coords = null!;
        commit = string.Empty;
        sourceDigest = string.Empty;
        if (!providerRunId.StartsWith("unity:", StringComparison.Ordinal))
            return false;
        var parts = providerRunId["unity:".Length..].Split('/');
        if (parts.Length != 6)
            return false;
        foreach (var part in parts[..3])
            if (string.IsNullOrWhiteSpace(part) || part.Length > 128)
                return false;
        if (!long.TryParse(parts[3], out var buildNumber) || buildNumber <= 0)
            return false;
        if (parts[4].Length != 40 || !parts[4].All(static c => Uri.IsHexDigit(c)))
            return false;
        if (parts[5].Length != 64 || !parts[5].All(static c => Uri.IsHexDigit(c)))
            return false;
        coords = new UnityRunCoords(parts[0], parts[1], parts[2], buildNumber);
        commit = parts[4].ToLowerInvariant();
        sourceDigest = parts[5].ToLowerInvariant();
        return true;
    }

    private static string BuildPath(UnityBuildTarget target, string suffix) =>
        $"api/v1/orgs/{Uri.EscapeDataString(target.OrganizationId)}/projects/{Uri.EscapeDataString(target.ProjectId)}/buildtargets/{Uri.EscapeDataString(target.BuildTargetId)}/{suffix}";

    private void ApplyAuth(HttpRequestMessage request, string token)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(token + ":"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }

    private static void ApplyIdempotency(HttpRequestMessage request, string requestId)
    {
        if (!string.IsNullOrWhiteSpace(requestId))
            request.Headers.TryAddWithoutValidation("X-Idempotency-Key", requestId.Trim());
    }

    private static ExternalBuildRateLimitedException RateLimited(string detail, HttpResponseMessage response)
    {
        TimeSpan? retryAfter = null;
        if (response.Headers.RetryAfter?.Delta is { } delta)
            retryAfter = delta;
        return new ExternalBuildRateLimitedException(
            "Unity throttled the call (HTTP 429): " + detail, retryAfter);
    }

    private async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await ReadTextAsync(response, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? response.StatusCode.ToString() : text;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return response.StatusCode.ToString();
        }
    }

    private async Task<string> ReadTextAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var bytes = await ReadCappedAsync(response, MaxResponseBytes, ct).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(bytes);
        return ExternalBuildArtifactGuard.Redact(ExternalBuildArtifactGuard.TruncateBounded(text, 4096));
    }

    private async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        T? value;
        try
        {
            var bytes = await ReadCappedAsync(response, MaxResponseBytes, ct).ConfigureAwait(false);
            value = JsonSerializer.Deserialize<T>(bytes, JsonOptions);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new UnityBuildUnavailableException("Unity returned an unreadable payload.", ex);
        }
        return value;
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, long capBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            total = checked(total + read);
            if (total > capBytes)
                throw new UnityBuildUnavailableException("Unity response exceeds the pre-buffer cap; refusing to buffer.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
