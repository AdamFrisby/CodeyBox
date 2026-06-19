using System.Net;
using CodeyBox.Api;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the OAuth-refresh behaviour added to the subscription quota probes
/// in #109. The probes used to forward an expired access_token verbatim; the
/// provider would 401, the snapshot would become "unknown", and the router's
/// default UnknownPolicy=UseObservedFailures would fall open onto an agent
/// that immediately 429s. These tests assert the refresher detects expiry,
/// posts to the OAuth refresh endpoint, persists the new token to disk, and
/// dedupes concurrent refresh attempts.
/// </summary>
public sealed class OauthCredentialFileRefresherTests : IDisposable
{
    private readonly string _dir;

    public OauthCredentialFileRefresherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cb-refresher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string WriteCreds(string fileName, string json)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    // ── Gemini ───────────────────────────────────────────────────────────────

    private static string GeminiCreds(string access, string refresh, long expiryMs)
        => $$"""
        {
          "access_token": "{{access}}",
          "refresh_token": "{{refresh}}",
          "client_id": "test-client",
          "client_secret": "test-secret",
          "expiry_date": {{expiryMs}}
        }
        """;

    [Fact]
    public async Task Gemini_Refresh_WhenExpired_PostsRefreshAndReturnsNewToken()
    {
        var path = WriteCreds("oauth_creds.json",
            GeminiCreds("old-access", "rt-1", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()));
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK,
            """{"access_token":"new-access","expires_in":3600}""");
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<GeminiOauthCredentialFileRefresher>.Instance);

        var token = await refresher.GetAccessTokenAsync();

        Assert.Equal("new-access", token);
        Assert.Single(handler.Requests);
        var form = handler.Requests[0];
        Assert.Contains("grant_type=refresh_token", form);
        Assert.Contains("refresh_token=rt-1", form);
        Assert.Contains("client_id=test-client", form);

        // File on disk now carries the new access_token and a future expiry.
        var updated = File.ReadAllText(path);
        Assert.Contains("\"new-access\"", updated);
        Assert.Contains("\"refresh_token\": \"rt-1\"", updated);
    }

    [Fact]
    public async Task Gemini_Refresh_WhenFresh_DoesNotCallEndpoint()
    {
        var path = WriteCreds("oauth_creds.json",
            GeminiCreds("good-token", "rt-1", DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds()));
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK, "{}");
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<GeminiOauthCredentialFileRefresher>.Instance);

        var token = await refresher.GetAccessTokenAsync();

        Assert.Equal("good-token", token);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Gemini_Refresh_Failure_LogsOnceWithinSuppressionWindow()
    {
        var path = WriteCreds("oauth_creds.json",
            GeminiCreds("old", "rt-1", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()));
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        var log = new CountingLogger<GeminiOauthCredentialFileRefresher>();
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            log);

        for (int i = 0; i < 5; i++)
            Assert.Null(await refresher.GetAccessTokenAsync());

        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal(1, log.WarningCount);
    }

    [Fact]
    public async Task Gemini_Refresh_ConcurrentCalls_PerformSingleRoundTrip()
    {
        var path = WriteCreds("oauth_creds.json",
            GeminiCreds("old", "rt-1", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()));
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK,
            """{"access_token":"new-access","expires_in":3600}""");
        // Slow the handler so all callers queue at the refresh gate before the
        // first one's response returns.
        handler.ResponseDelay = TimeSpan.FromMilliseconds(150);
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<GeminiOauthCredentialFileRefresher>.Instance);

        var calls = Enumerable.Range(0, 10).Select(_ => refresher.GetAccessTokenAsync()).ToArray();
        await Task.WhenAll(calls);

        Assert.All(calls, t => Assert.Equal("new-access", t.Result));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Gemini_Refresh_ConfigProvidedClientCredentials_FallbackWhenFileLacksThem()
    {
        // Real gemini CLI output omits client_id and client_secret.
        var path = WriteCreds("oauth_creds.json",
            $$"""{"access_token":"old-access","refresh_token":"rt-1","expiry_date":{{DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()}}}""");
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK,
            """{"access_token":"new-access","expires_in":3600}""");
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<GeminiOauthCredentialFileRefresher>.Instance,
            geminiOauthClientId: "config-client",
            geminiOauthClientSecret: "config-secret");

        var token = await refresher.GetAccessTokenAsync();

        Assert.Equal("new-access", token);
        Assert.Single(handler.Requests);
        var form = handler.Requests[0];
        Assert.Contains("client_id=config-client", form);
        Assert.Contains("client_secret=config-secret", form);
    }

    [Fact]
    public async Task Gemini_CliRefresh_InvokedWhenNoClientCredentialsAvailable()
    {
        var path = WriteCreds("oauth_creds.json",
            $$"""{"access_token":"old-access","refresh_token":"rt-1","expiry_date":{{DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()}}}""");
        var freshJson = $$"""{"access_token":"cli-refreshed-token","refresh_token":"rt-2","expiry_date":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()}}}""";
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK, """{"access_token":"unused","expires_in":3600}""");
        var cliCalled = 0;
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<GeminiOauthCredentialFileRefresher>.Instance,
            cliTokenRefresher: _ => { cliCalled++; File.WriteAllText(path, freshJson); return Task.FromResult(true); });

        var token = await refresher.GetAccessTokenAsync();

        Assert.Equal("cli-refreshed-token", token);
        Assert.Equal(1, cliCalled);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Gemini_CliRefresh_ExtractsTokenFromRefreshedFile()
    {
        // The file on disk is stale (no client_id/client_secret). The CLI
        // refresh rewrites the file with a fresh token. The refresher must
        // re-read and return it.
        var staleJson = $$"""{"access_token":"old-access","refresh_token":"rt-1","expiry_date":{{DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()}}}""";
        var path = WriteCreds("oauth_creds.json", staleJson);
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK, "{}");

        var freshMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
        var freshJson = $$"""{"access_token":"cli-fresh-token","refresh_token":"rt-2","expiry_date":{{freshMs}}}""";

        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<GeminiOauthCredentialFileRefresher>.Instance,
            cliTokenRefresher: _ =>
            {
                // Simulate the CLI rewriting the file.
                File.WriteAllText(path, freshJson);
                return Task.FromResult(true);
            });

        var token = await refresher.GetAccessTokenAsync();

        Assert.Equal("cli-fresh-token", token);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Gemini_CliRefresh_Failure_ReturnsNullAndLogsOnce()
    {
        var path = WriteCreds("oauth_creds.json",
            $$"""{"access_token":"old-access","refresh_token":"rt-1","expiry_date":{{DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()}}}""");
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK, "{}");
        var log = new CountingLogger<GeminiOauthCredentialFileRefresher>();
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            log,
            cliTokenRefresher: _ => Task.FromResult(false));

        for (int i = 0; i < 3; i++)
            Assert.Null(await refresher.GetAccessTokenAsync());

        Assert.Empty(handler.Requests);
        Assert.Equal(1, log.WarningCount);
    }

    [Fact]
    public async Task Gemini_CliRefresh_ConfigCredentialsBeatCliFallback()
    {
        // When both config client creds and CLI refresh are available, config
        // creds are preferred (HTTP refresh is faster and more reliable).
        var path = WriteCreds("oauth_creds.json",
            $$"""{"access_token":"old-access","refresh_token":"rt-1","expiry_date":{{DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()}}}""");
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK,
            """{"access_token":"http-new-access","expires_in":3600}""");
        var cliCalled = 0;
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<GeminiOauthCredentialFileRefresher>.Instance,
            geminiOauthClientId: "config-client",
            geminiOauthClientSecret: "config-secret",
            cliTokenRefresher: _ => { cliCalled++; return Task.FromResult(true); });

        var token = await refresher.GetAccessTokenAsync();

        Assert.Equal("http-new-access", token);
        Assert.Single(handler.Requests);
        Assert.Equal(0, cliCalled);
    }

    [Fact]
    public async Task Gemini_CliRefresh_ReturnsNullWhenRefreshedFileLacksAccessToken()
    {
        var staleJson = $$"""{"access_token":"old-access","refresh_token":"rt-1","expiry_date":{{DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()}}}""";
        var path = WriteCreds("oauth_creds.json", staleJson);
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK, "{}");
        var log = new CountingLogger<GeminiOauthCredentialFileRefresher>();
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            log,
            cliTokenRefresher: _ =>
            {
                File.WriteAllText(path, """{"access_token":"","refresh_token":"rt-1","expiry_date":0}""");
                return Task.FromResult(true);
            });

        Assert.Null(await refresher.GetAccessTokenAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Gemini_CliRefresh_UsesFallbackLifetimeWhenExpiryMissing()
    {
        var staleJson = $$"""{"access_token":"old-access","refresh_token":"rt-1","expiry_date":{{DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()}}}""";
        var path = WriteCreds("oauth_creds.json", staleJson);
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK, "{}");
        var cliCalled = 0;
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<GeminiOauthCredentialFileRefresher>.Instance,
            cliTokenRefresher: _ =>
            {
                cliCalled++;
                File.WriteAllText(path, """{"access_token":"cli-fresh-token","refresh_token":"rt-2"}""");
                return Task.FromResult(true);
            });

        var token = await refresher.GetAccessTokenAsync();
        Assert.Equal("cli-fresh-token", token);
        Assert.Equal(1, cliCalled);
        Assert.Empty(handler.Requests);

        // Second call must serve from the in-process cache (FallbackTokenLifetime)
        // without re-invoking the CLI.
        var token2 = await refresher.GetAccessTokenAsync();
        Assert.Equal("cli-fresh-token", token2);
        Assert.Equal(1, cliCalled);
    }

    [Fact]
    public async Task Gemini_Refresh_OnlyClientIdProvided_WithoutFileClientId_FallsBackToCli()
    {
        var path = WriteCreds("oauth_creds.json",
            $$"""{"access_token":"old-access","refresh_token":"rt-1","expiry_date":{{DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()}}}""");
        var freshJson = $$"""{"access_token":"cli-refreshed","refresh_token":"rt-2","expiry_date":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()}}}""";
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK, """{"access_token":"http-new","expires_in":3600}""");
        var cliCalled = 0;
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<GeminiOauthCredentialFileRefresher>.Instance,
            geminiOauthClientId: "config-client",
            geminiOauthClientSecret: null,
            cliTokenRefresher: _ => { cliCalled++; File.WriteAllText(path, freshJson); return Task.FromResult(true); });

        var token = await refresher.GetAccessTokenAsync();

        Assert.Equal("cli-refreshed", token);
        Assert.Equal(1, cliCalled);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Gemini_Refresh_OnlyClientSecretProvided_WithoutFileClientSecret_FallsBackToCli()
    {
        var path = WriteCreds("oauth_creds.json",
            $$"""{"access_token":"old-access","refresh_token":"rt-1","expiry_date":{{DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()}}}""");
        var freshJson = $$"""{"access_token":"cli-refreshed","refresh_token":"rt-2","expiry_date":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()}}}""";
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK, """{"access_token":"http-new","expires_in":3600}""");
        var cliCalled = 0;
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<GeminiOauthCredentialFileRefresher>.Instance,
            geminiOauthClientId: null,
            geminiOauthClientSecret: "config-secret",
            cliTokenRefresher: _ => { cliCalled++; File.WriteAllText(path, freshJson); return Task.FromResult(true); });

        var token = await refresher.GetAccessTokenAsync();

        Assert.Equal("cli-refreshed", token);
        Assert.Equal(1, cliCalled);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Gemini_Refresh_WhenHttpFails_DoesNotFallBackToCli()
    {
        var path = WriteCreds("oauth_creds.json",
            GeminiCreds("old-access", "rt-1", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()));
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        // HTTP returns 401 — credentials were revoked or misconfigured.
        var handler = new RefresherCapturingHandler(HttpStatusCode.Unauthorized, "{}");
        var cliCalled = 0;
        var log = new CountingLogger<GeminiOauthCredentialFileRefresher>();
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            log,
            cliTokenRefresher: _ =>
            {
                cliCalled++;
                File.WriteAllText(path, """{"access_token":"cli-fresh","expiry_date":9999999999999}""");
                return Task.FromResult(true);
            });

        var token = await refresher.GetAccessTokenAsync();

        Assert.Null(token);
        Assert.Equal(0, cliCalled);
        Assert.True(log.WarningCount >= 1);
    }

    [Fact]
    public async Task Gemini_Refresh_WhenResponseBodyExceedsMax_ReturnsNull()
    {
        var path = WriteCreds("oauth_creds.json",
            GeminiCreds("old-access", "rt-1", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()));
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var oversizedBody = "{\"access_token\":\"tok\",\"expires_in\":3600" + new string(' ', 8192) + "}";
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK, oversizedBody);
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<GeminiOauthCredentialFileRefresher>.Instance);

        var token = await refresher.GetAccessTokenAsync();

        Assert.Null(token);
        Assert.Single(handler.Requests);
    }

    // ── CLI refresh handler (TryCreateCliRefreshHandler / ResolveGeminiCliPath) ─

    [Fact]
    public async Task TryCreateCliRefreshHandler_WhenScriptExitsZero_ReturnsTrue()
    {
        var scriptPath = WriteExecutableScript("echo done", exitCode: 0);
        var handler = GeminiOauthCredentialFileRefresher.TryCreateCliRefreshHandler(
            resolvePath: () => scriptPath);
        Assert.NotNull(handler);
        Assert.True(await handler(CancellationToken.None));
    }

    [Fact]
    public async Task TryCreateCliRefreshHandler_WhenScriptExitsNonZero_ReturnsFalse()
    {
        var scriptPath = WriteExecutableScript("echo fail", exitCode: 1);
        var handler = GeminiOauthCredentialFileRefresher.TryCreateCliRefreshHandler(
            resolvePath: () => scriptPath);
        Assert.NotNull(handler);
        Assert.False(await handler(CancellationToken.None));
    }

    [Fact]
    public async Task TryCreateCliRefreshHandler_WhenPathIsNull_ReturnsNull()
    {
        var handler = GeminiOauthCredentialFileRefresher.TryCreateCliRefreshHandler(
            resolvePath: () => null!);
        Assert.Null(handler);
    }

    [Fact]
    public async Task TryCreateCliRefreshHandler_WhenCancellationTokenCanceled_ReturnsFalse()
    {
        // Use a script that sleeps so the CT fires before exit.
        var scriptPath = WriteExecutableScript("sleep 60", exitCode: 0);
        var handler = GeminiOauthCredentialFileRefresher.TryCreateCliRefreshHandler(
            resolvePath: () => scriptPath);
        Assert.NotNull(handler);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        // The external CT fires after 100 ms, well before the 30 s linked CTS timeout.
        var result = await handler(cts.Token);
        Assert.False(result);
    }

    [Fact]
    public async Task TryCreateCliRefreshHandler_ForwardsHomeEnvVarToSubprocess()
    {
        var sentinel = Path.Combine(_dir, $"sentinel-{Guid.NewGuid():N}.txt");
        var isWin = OperatingSystem.IsWindows();
        var body = isWin
            ? $"echo %HOME% > \"{sentinel}\""
            : $"echo \"$HOME\" > \"{sentinel}\"";
        var scriptPath = WriteExecutableScript(body, exitCode: 0);
        var handler = GeminiOauthCredentialFileRefresher.TryCreateCliRefreshHandler(
            resolvePath: () => scriptPath);
        Assert.NotNull(handler);
        Assert.True(await handler(CancellationToken.None));
        Assert.True(File.Exists(sentinel));
        var captured = File.ReadAllText(sentinel).Trim();
        Assert.Equal(Environment.GetEnvironmentVariable("HOME") ?? "", captured);
    }

    [Fact]
    public void ResolveExecutablePath_WhenResolverFails_ReturnsNull()
    {
        var result = GeminiOauthCredentialFileRefresher.ResolveExecutablePath(
            "no-such-resolver-command-hopefully-not-on-path", "gemini");
        Assert.Null(result);
    }

    [Fact]
    public void ResolveCliRefreshPathValue_WhenParentPathMissing_UsesOsDefault()
    {
        var result = GeminiOauthCredentialFileRefresher.ResolveCliRefreshPathValue(null);

        if (OperatingSystem.IsWindows())
            Assert.Contains("Windows", result, StringComparison.OrdinalIgnoreCase);
        else
            Assert.Contains("/usr/bin", result);
    }

    [Fact]
    public void ResolveCliRefreshPathValue_WhenParentPathPresent_PreservesIt()
    {
        Assert.Equal("custom-path",
            GeminiOauthCredentialFileRefresher.ResolveCliRefreshPathValue("custom-path"));
    }

    [Fact]
    public void ResolveExecutablePath_WhenResolverTimesOut_ReturnsNull()
    {
        var scriptPath = WriteExecutableScript("sleep 10", exitCode: 0);
        var result = GeminiOauthCredentialFileRefresher.ResolveExecutablePath(scriptPath, "gemini");
        Assert.Null(result);
    }

    private string WriteExecutableScript(string body, int exitCode)
    {
        var ext = OperatingSystem.IsWindows() ? ".cmd" : ".sh";
        var scriptPath = Path.Combine(_dir, $"fake-gemini-{Guid.NewGuid():N}{ext}");
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(scriptPath, $"@echo off\r\n{body}\r\nexit /b {exitCode}\r\n");
        }
        else
        {
            File.WriteAllText(scriptPath, $"#!/bin/sh\n{body}\nexit {exitCode}\n");
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }
        return scriptPath;
    }

    // ── Claude ───────────────────────────────────────────────────────────────

    private static string ClaudeCreds(string access, string refresh, long expiresAtMs)
        => $$"""
        { "claudeAiOauth": { "accessToken": "{{access}}", "refreshToken": "{{refresh}}", "expiresAt": {{expiresAtMs}} } }
        """;

    [Fact]
    public async Task Claude_Refresh_WhenExpired_PostsRefreshAndReturnsNewToken()
    {
        var path = WriteCreds(".credentials.json",
            ClaudeCreds("old", "rt-claude", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()));
        using var source = new ClaudeCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK,
            """{"access_token":"sk-ant-new","expires_in":28800}""");
        using var refresher = new ClaudeOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<ClaudeOauthCredentialFileRefresher>.Instance);

        var token = await refresher.GetAccessTokenAsync();

        Assert.Equal("sk-ant-new", token);
        Assert.Single(handler.Requests);
        var body = handler.Requests[0];
        Assert.Contains("\"grant_type\":\"refresh_token\"", body);
        Assert.Contains("\"refresh_token\":\"rt-claude\"", body);

        var updated = File.ReadAllText(path);
        Assert.Contains("\"sk-ant-new\"", updated);
        Assert.Contains("\"refreshToken\": \"rt-claude\"", updated);
    }

    [Fact]
    public async Task Claude_Refresh_WhenFresh_DoesNotCallEndpoint()
    {
        var path = WriteCreds(".credentials.json",
            ClaudeCreds("good", "rt-claude", DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds()));
        using var source = new ClaudeCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK, "{}");
        using var refresher = new ClaudeOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<ClaudeOauthCredentialFileRefresher>.Instance);

        Assert.Equal("good", await refresher.GetAccessTokenAsync());
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// Anthropic emits expiresAt in ms-since-epoch, but older / hand-edited
    /// snapshots have been observed using seconds. The ParseCreds branch at
    /// OauthCredentialFileRefresher.cs:699 disambiguates by magnitude. A seconds
    /// value far in the future must still be treated as fresh — without this
    /// coverage a bug that swaps `<` and `>` (or uses the wrong factory) would
    /// silently mis-classify every seconds-format snapshot.
    /// </summary>
    [Fact]
    public async Task Claude_Refresh_ExpiresAtAsSeconds_TreatedAsFresh()
    {
        var path = WriteCreds(".credentials.json",
            ClaudeCreds("good-seconds", "rt-claude", DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()));
        using var source = new ClaudeCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK, "{}");
        using var refresher = new ClaudeOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<ClaudeOauthCredentialFileRefresher>.Instance);

        Assert.Equal("good-seconds", await refresher.GetAccessTokenAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Claude_Refresh_Failure_LogsOnceWithinSuppressionWindow()
    {
        var path = WriteCreds(".credentials.json",
            ClaudeCreds("old", "rt-claude", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()));
        using var source = new ClaudeCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        var log = new CountingLogger<ClaudeOauthCredentialFileRefresher>();
        using var refresher = new ClaudeOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            log);

        for (int i = 0; i < 5; i++)
            Assert.Null(await refresher.GetAccessTokenAsync());

        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal(1, log.WarningCount);
    }

    [Fact]
    public async Task Claude_Refresh_ConcurrentCalls_PerformSingleRoundTrip()
    {
        var path = WriteCreds(".credentials.json",
            ClaudeCreds("old", "rt-claude", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()));
        using var source = new ClaudeCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK,
            """{"access_token":"sk-ant-new","expires_in":28800}""");
        handler.ResponseDelay = TimeSpan.FromMilliseconds(150);
        using var refresher = new ClaudeOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<ClaudeOauthCredentialFileRefresher>.Instance);

        var calls = Enumerable.Range(0, 10).Select(_ => refresher.GetAccessTokenAsync()).ToArray();
        await Task.WhenAll(calls);

        Assert.All(calls, t => Assert.Equal("sk-ant-new", t.Result));
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// File on disk has an expired access_token but no refresh_token (a real
    /// state for a partially-rotated file or a hand-edited one). The refresher
    /// must return null without throwing, and emit exactly one warning even
    /// when called repeatedly — otherwise the probe would NRE on the
    /// PerformRefreshAsync path or spam logs.
    /// </summary>
    [Fact]
    public async Task Claude_Refresh_WhenExpiredAndNoRefreshToken_ReturnsNullAndLogsOnce()
    {
        var json = $$"""
        { "claudeAiOauth": { "accessToken": "old", "expiresAt": {{DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()}} } }
        """;
        var path = WriteCreds(".credentials.json", json);
        using var source = new ClaudeCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK, "{}");
        var log = new CountingLogger<ClaudeOauthCredentialFileRefresher>();
        using var refresher = new ClaudeOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            log);

        for (int i = 0; i < 3; i++)
            Assert.Null(await refresher.GetAccessTokenAsync());

        Assert.Empty(handler.Requests);
        Assert.Equal(1, log.WarningCount);
    }

    // ── Codex ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a JWT-shaped access_token with the given expiry claim (signature
    /// payload is irrelevant — the refresher only reads <c>exp</c>).
    /// </summary>
    private static string CodexAccessJwt(long expSeconds)
    {
        static string Encode(string json)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        var header = Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Encode($$"""{"exp":{{expSeconds}},"sub":"u","account_id":"acct-1"}""");
        return $"{header}.{payload}.";
    }

    private static string CodexCreds(string accessJwt, string refresh, string accountId)
        => $$"""
        {
          "tokens": {
            "id_token": "id-1",
            "access_token": "{{accessJwt}}",
            "refresh_token": "{{refresh}}",
            "account_id": "{{accountId}}"
          },
          "last_refresh": "2024-01-01T00:00:00Z"
        }
        """;

    [Fact]
    public async Task Codex_Refresh_WhenExpired_PostsRefreshAndReturnsNewToken()
    {
        var expiredExp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var freshExp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var path = WriteCreds("auth.json", CodexCreds(CodexAccessJwt(expiredExp), "rt-codex", "acct-1"));
        using var source = new CodexCredentialFileSource(path, watch: false);
        var newJwt = CodexAccessJwt(freshExp);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK,
            $$"""{"access_token":"{{newJwt}}","refresh_token":"rt-codex-2","expires_in":3600}""");
        using var refresher = new CodexOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<CodexOauthCredentialFileRefresher>.Instance);

        var (token, accountId) = await refresher.GetTokensAsync();

        Assert.Equal(newJwt, token);
        Assert.Equal("acct-1", accountId);
        Assert.Single(handler.Requests);

        var updated = File.ReadAllText(path);
        Assert.Contains(newJwt, updated);
        Assert.Contains("rt-codex-2", updated);
    }

    [Fact]
    public async Task Codex_Refresh_WhenFresh_DoesNotCallEndpoint()
    {
        var freshExp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var jwt = CodexAccessJwt(freshExp);
        var path = WriteCreds("auth.json", CodexCreds(jwt, "rt-codex", "acct-1"));
        using var source = new CodexCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK, "{}");
        using var refresher = new CodexOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<CodexOauthCredentialFileRefresher>.Instance);

        var (token, accountId) = await refresher.GetTokensAsync();

        Assert.Equal(jwt, token);
        Assert.Equal("acct-1", accountId);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Codex_Refresh_Failure_LogsOnceWithinSuppressionWindow()
    {
        var expiredExp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var path = WriteCreds("auth.json", CodexCreds(CodexAccessJwt(expiredExp), "rt-codex", "acct-1"));
        using var source = new CodexCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        var log = new CountingLogger<CodexOauthCredentialFileRefresher>();
        using var refresher = new CodexOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            log);

        for (int i = 0; i < 5; i++)
        {
            var (token, accountId) = await refresher.GetTokensAsync();
            Assert.Null(token);
            // Refresh fails, but the account_id is still observable through the
            // fall-back parse path in CodexOauthCredentialFileRefresher.GetTokensAsync.
            Assert.Equal("acct-1", accountId);
        }

        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal(1, log.WarningCount);
    }

    [Fact]
    public async Task Codex_Refresh_ConcurrentCalls_PerformSingleRoundTrip()
    {
        var expiredExp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var freshExp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var path = WriteCreds("auth.json", CodexCreds(CodexAccessJwt(expiredExp), "rt-codex", "acct-1"));
        using var source = new CodexCredentialFileSource(path, watch: false);
        var newJwt = CodexAccessJwt(freshExp);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK,
            $$"""{"access_token":"{{newJwt}}","refresh_token":"rt-codex-2","expires_in":3600}""");
        handler.ResponseDelay = TimeSpan.FromMilliseconds(150);
        using var refresher = new CodexOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<CodexOauthCredentialFileRefresher>.Instance);

        var calls = Enumerable.Range(0, 10)
            .Select(_ => refresher.GetTokensAsync())
            .ToArray();
        await Task.WhenAll(calls);

        Assert.All(calls, t =>
        {
            Assert.Equal(newJwt, t.Result.AccessToken);
            Assert.Equal("acct-1", t.Result.AccountId);
        });
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// File on disk has an expired access_token but no refresh_token in the
    /// tokens object. Refresher must return a null access_token (still surface
    /// the account_id via fallback) without throwing, and emit exactly one
    /// warning across multiple calls.
    /// </summary>
    [Fact]
    public async Task Codex_Refresh_WhenExpiredAndNoRefreshToken_ReturnsNullAndLogsOnce()
    {
        var expiredExp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var jwt = CodexAccessJwt(expiredExp);
        var json = $$"""
        {
          "tokens": {
            "id_token": "id-1",
            "access_token": "{{jwt}}",
            "account_id": "acct-1"
          }
        }
        """;
        var path = WriteCreds("auth.json", json);
        using var source = new CodexCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK, "{}");
        var log = new CountingLogger<CodexOauthCredentialFileRefresher>();
        using var refresher = new CodexOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            log);

        for (int i = 0; i < 3; i++)
        {
            var (token, accountId) = await refresher.GetTokensAsync();
            Assert.Null(token);
            Assert.Equal("acct-1", accountId);
        }

        Assert.Empty(handler.Requests);
        Assert.Equal(1, log.WarningCount);
    }

    // ── BuildPersistedJson field preservation ────────────────────────────────
    //
    // Each refresher's BuildPersistedJson is deliberately structured to copy
    // through every property that isn't the rotated access_token / refresh_token
    // / expiry. If a future change dropped one of those unmodified fields
    // (client_id/client_secret for Gemini, id_token for Codex, a custom field
    // like an organisation hint for Claude), the refresh would succeed but the
    // very next refresh round-trip would fail because PerformRefreshAsync
    // requires the original client credentials. The "happy path" tests above
    // only assert that the rotated fields are present, so they would not catch
    // a regression that silently drops the unrotated ones. These tests close
    // that gap.

    [Fact]
    public async Task Gemini_Refresh_PreservesClientIdAndSecretOnDisk()
    {
        var path = WriteCreds("oauth_creds.json",
            GeminiCreds("old-access", "rt-1", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()));
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK,
            """{"access_token":"new-access","expires_in":3600}""");
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<GeminiOauthCredentialFileRefresher>.Instance);

        Assert.Equal("new-access", await refresher.GetAccessTokenAsync());

        var updated = File.ReadAllText(path);
        using var doc = System.Text.Json.JsonDocument.Parse(updated);
        // Rotated fields are present.
        Assert.Equal("new-access", doc.RootElement.GetProperty("access_token").GetString());
        Assert.Equal("rt-1", doc.RootElement.GetProperty("refresh_token").GetString());
        // Unrotated fields survive — without these the next refresh would fail
        // because PerformRefreshAsync returns null when ClientId/ClientSecret
        // are missing.
        Assert.Equal("test-client", doc.RootElement.GetProperty("client_id").GetString());
        Assert.Equal("test-secret", doc.RootElement.GetProperty("client_secret").GetString());
    }

    [Fact]
    public async Task Codex_Refresh_PreservesIdTokenAndLastRefreshOnDisk()
    {
        var expiredExp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var freshExp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var path = WriteCreds("auth.json", CodexCreds(CodexAccessJwt(expiredExp), "rt-codex", "acct-1"));
        using var source = new CodexCredentialFileSource(path, watch: false);
        var newJwt = CodexAccessJwt(freshExp);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK,
            $$"""{"access_token":"{{newJwt}}","refresh_token":"rt-codex-2","expires_in":3600}""");
        using var refresher = new CodexOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<CodexOauthCredentialFileRefresher>.Instance);

        var (token, _) = await refresher.GetTokensAsync();
        Assert.Equal(newJwt, token);

        var updated = File.ReadAllText(path);
        using var doc = System.Text.Json.JsonDocument.Parse(updated);
        var tokens = doc.RootElement.GetProperty("tokens");
        // Rotated.
        Assert.Equal(newJwt, tokens.GetProperty("access_token").GetString());
        Assert.Equal("rt-codex-2", tokens.GetProperty("refresh_token").GetString());
        // Unrotated — id_token and account_id are preserved inside "tokens";
        // last_refresh is preserved at the top level (its value is rewritten
        // via TimeProvider, but the property must still exist).
        Assert.Equal("id-1", tokens.GetProperty("id_token").GetString());
        Assert.Equal("acct-1", tokens.GetProperty("account_id").GetString());
        Assert.True(doc.RootElement.TryGetProperty("last_refresh", out _));
    }

    [Fact]
    public async Task Claude_Refresh_PreservesUnrotatedClaudeAiOauthFieldsOnDisk()
    {
        // Include a non-standard "scopes" field inside claudeAiOauth and a
        // sibling "userInfo" object at the root — both must survive the
        // rewrite. Real .credentials.json files emitted by the claude CLI
        // include extras like these.
        var path = Path.Combine(_dir, ".credentials.json");
        var pastMs = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
        File.WriteAllText(path, $$"""
        {
          "claudeAiOauth": {
            "accessToken": "old",
            "refreshToken": "rt-claude",
            "expiresAt": {{pastMs}},
            "scopes": ["read", "write"]
          },
          "userInfo": { "email": "u@example.com" }
        }
        """);
        using var source = new ClaudeCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK,
            """{"access_token":"sk-ant-new","expires_in":28800}""");
        using var refresher = new ClaudeOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<ClaudeOauthCredentialFileRefresher>.Instance);

        Assert.Equal("sk-ant-new", await refresher.GetAccessTokenAsync());

        var updated = File.ReadAllText(path);
        using var doc = System.Text.Json.JsonDocument.Parse(updated);
        var oauth = doc.RootElement.GetProperty("claudeAiOauth");
        Assert.Equal("sk-ant-new", oauth.GetProperty("accessToken").GetString());
        Assert.Equal("rt-claude", oauth.GetProperty("refreshToken").GetString());
        // Unrotated fields survive both inside claudeAiOauth and at the root.
        var scopes = oauth.GetProperty("scopes");
        Assert.Equal(2, scopes.GetArrayLength());
        Assert.Equal("read", scopes[0].GetString());
        Assert.Equal("u@example.com",
            doc.RootElement.GetProperty("userInfo").GetProperty("email").GetString());
    }

    // ── Atomic write file-permission preservation ────────────────────────────
    //
    // The atomic-write codepath sets the tempfile to UnixFileMode 0600 before
    // the rename so the rotated access_token never lands on a shared host with
    // world-readable perms. This branch has no other coverage — a regression
    // that removed the SetUnixFileMode / UnixCreateMode wiring would silently
    // leave the rewritten creds file at the process umask (typically 0644).
    [Fact]
    public async Task Gemini_Refresh_PersistedFileIs0600OnPosix()
    {
        if (OperatingSystem.IsWindows())
        {
            // UnixFileMode is not meaningful on Windows; ACLs are validated by
            // a separate test in CredentialFileSourceTests.
            return;
        }

        var path = WriteCreds("oauth_creds.json",
            GeminiCreds("old", "rt-1", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()));
        // Pre-set the existing file to 0644 so we can prove the rewrite tightens
        // perms to 0600 (rather than relying on whatever umask the test runner
        // happens to inherit).
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK,
            """{"access_token":"new-access","expires_in":3600}""");
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<GeminiOauthCredentialFileRefresher>.Instance);

        Assert.Equal("new-access", await refresher.GetAccessTokenAsync());

        var mode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    // ── Shared: in-process cache ─────────────────────────────────────────────

    /// <summary>
    /// Two sequential calls against an expired file should only post once to
    /// the refresh endpoint: after the first refresh succeeds, the second call
    /// must observe the in-process cache rather than hitting OAuth again. We
    /// exercise that path by rewriting the file back to expired state between
    /// calls (simulating an out-of-band rewrite from the host CLI), forcing
    /// the cache branch in GetOrRefreshAsync rather than the file-fresh
    /// short-circuit.
    /// </summary>
    [Fact]
    public async Task Gemini_Refresh_CacheHit_SkipsSecondHttpRoundTrip()
    {
        var expiredJson = GeminiCreds("old", "rt-1",
            DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());
        var path = WriteCreds("oauth_creds.json", expiredJson);
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK,
            """{"access_token":"new-access","expires_in":3600}""");
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<GeminiOauthCredentialFileRefresher>.Instance);

        var first = await refresher.GetAccessTokenAsync();
        Assert.Equal("new-access", first);
        Assert.Single(handler.Requests);

        // Rewrite the file back to the expired snapshot. Delay so the mtime
        // observably advances on coarse-resolution filesystems; the source's
        // stat-based reload then re-parses and sees an expired token.
        await Task.Delay(50);
        File.WriteAllText(path, expiredJson);

        var second = await refresher.GetAccessTokenAsync();

        Assert.Equal("new-access", second);
        Assert.Single(handler.Requests);
    }
}

internal sealed class RefresherFakeHttpClientFactory : IHttpClientFactory
{
    private readonly string _clientName;
    private readonly HttpMessageHandler _handler;
    public RefresherFakeHttpClientFactory(string clientName, HttpMessageHandler handler)
    {
        _clientName = clientName;
        _handler = handler;
    }
    public HttpClient CreateClient(string name)
    {
        if (name != _clientName)
            throw new InvalidOperationException($"Unexpected client name '{name}'; expected '{_clientName}'");
        return new HttpClient(_handler, disposeHandler: false);
    }
}

internal sealed class RefresherCapturingHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;
    public List<string> Requests { get; } = new();
    public TimeSpan ResponseDelay { get; set; } = TimeSpan.Zero;
    private readonly object _gate = new();

    public RefresherCapturingHandler(HttpStatusCode status, string body)
    {
        _status = status;
        _body = body;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Capture the request body as a string now — the refresher disposes the
        // HttpRequestMessage as soon as SendAsync returns, taking the content
        // stream with it.
        var bodyText = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        lock (_gate) Requests.Add(bodyText);
        if (ResponseDelay > TimeSpan.Zero)
            await Task.Delay(ResponseDelay, ct).ConfigureAwait(false);
        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body),
        };
    }
}

internal sealed class CountingLogger<T> : ILogger<T>
{
    public int WarningCount;
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning) Interlocked.Increment(ref WarningCount);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
