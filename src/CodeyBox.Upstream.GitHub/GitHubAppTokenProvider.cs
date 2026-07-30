using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeyBox.Upstream.GitHub;

public interface IGitHubTokenProvider
{
    ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default);
}

public sealed class GitHubAppTokenProvider : IGitHubTokenProvider, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _clients;
    private readonly GitHubAppTokenOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _refreshAt;

    public GitHubAppTokenProvider(
        IHttpClientFactory clients,
        GitHubAppTokenOptions options,
        TimeProvider? timeProvider = null)
    {
        _clients = clients;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        if (_options.AppId <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "GitHub AppId must be positive.");
        if (_options.InstallationId <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "GitHub InstallationId must be positive.");
        ValidatePrivateKeyFile(_options.PrivateKeyPath);
    }

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        if (_cachedToken is not null && now < _refreshAt)
            return _cachedToken;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            now = _time.GetUtcNow();
            if (_cachedToken is not null && now < _refreshAt)
                return _cachedToken;

            var jwt = CreateAppJwt(now);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://api.github.com/app/installations/{_options.InstallationId}/access_tokens");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd("CodeyBox");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await _clients.CreateClient("github-upstream")
                .SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<InstallationTokenResponse>(
                JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException("GitHub returned an empty installation-token response.");
            if (string.IsNullOrWhiteSpace(payload.Token))
                throw new InvalidOperationException("GitHub returned an empty installation token.");
            if (payload.ExpiresAt <= now.AddMinutes(1))
                throw new InvalidOperationException("GitHub returned an installation token with an invalid expiry.");

            _cachedToken = payload.Token;
            _refreshAt = payload.ExpiresAt.AddMinutes(-1);
            return payload.Token;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private string CreateAppJwt(DateTimeOffset now)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(
            new { alg = "RS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = now.AddSeconds(-30).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = _options.AppId,
        }));
        var signingInput = $"{header}.{payload}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(_options.PrivateKeyPath));
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static void ValidatePrivateKeyFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("GitHub App private-key path must be absolute.", nameof(path));
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null)
            throw new InvalidOperationException("GitHub App private-key file must exist and must not be a symbolic link.");
        if (!OperatingSystem.IsWindows()
            && File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
            throw new InvalidOperationException("GitHub App private-key file permissions must be 0600.");
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => _refreshLock.Dispose();

    private sealed record InstallationTokenResponse(
        string Token,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
}

public sealed record GitHubAppTokenOptions(
    long AppId,
    long InstallationId,
    string PrivateKeyPath);
