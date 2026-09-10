using System.Net;
using CodeyBox.Agents.Antigravity;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the platform keyring seam for the Antigravity OAuth refresher: the
/// Secret Service reader must not be constructed off Linux, and a host with no
/// usable backend must retain the on-disk token while saying so loudly at
/// startup (the silent variant of this gap is the bug #400 fixed on Linux —
/// an expired probe token 401s, reads UNKNOWN, and the router fails open).
/// </summary>
public sealed class AgentKeyringReaderTests : IDisposable
{
    private readonly string _dir;

    public AgentKeyringReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cb-keyring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void SecretServiceKeyringReader_TryCreate_WhenPlatformUnsupported_ReturnsNull()
    {
        Assert.Null(SecretServiceKeyringReader.TryCreate(isPlatformSupported: () => false));
    }

    [Fact]
    public void SecretServiceKeyringReader_TryCreate_WhenPlatformSupported_ReturnsReaderWithoutTouchingDbus()
    {
        // Construction alone must not open a bus connection: there is no
        // session bus in this sandbox, so any D-Bus I/O here would throw.
        var reader = SecretServiceKeyringReader.TryCreate(isPlatformSupported: () => true);

        Assert.NotNull(reader);
    }

    [Fact]
    public async Task Antigravity_NoKeyringReaderAvailable_RetainsStaleTokenAndStartupWarningNamesConsequence()
    {
        var expiredIso = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("o");
        var path = Path.Combine(_dir, "antigravity-oauth-token");
        File.WriteAllText(path, $$"""
        {
          "auth_method": "consumer",
          "token": {
            "access_token": "stale-access-token",
            "refresh_token": "rt-1",
            "token_type": "Bearer",
            "expiry": "{{expiredIso}}"
          }
        }
        """);
        using var source = new AntigravityCredentialFileSource(path, watch: false);
        using var refresher = new AntigravityOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", new RefresherCapturingHandler(HttpStatusCode.OK, "")),
            NullLogger<AntigravityOauthCredentialFileRefresher>.Instance,
            cliRunner: _ => Task.FromResult(true),
            // No keyring backend for this platform: the injected platform
            // check forces the null-returning default delegate.
            keyringReader: AntigravityOauthCredentialFileRefresher.CreateDefaultKeyringReader(
                isPlatformSupported: () => false,
                log: NullLogger<AntigravityOauthCredentialFileRefresher>.Instance));

        // The expired on-disk token is retained rather than dropped or thrown.
        Assert.Equal("stale-access-token", await refresher.GetAccessTokenAsync());

        // And startup says so explicitly, naming the consequence — asserted on
        // the emitted text, not on a mock invocation.
        var log = new MessageCapturingLogger();
        AntigravityKeyringStartup.LogKeyringStatus(log, reader: null);

        var warning = Assert.Single(log.Messages);
        Assert.Contains("expire", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNKNOWN", warning, StringComparison.Ordinal);

        // A usable reader stays silent.
        var quiet = new MessageCapturingLogger();
        AntigravityKeyringStartup.LogKeyringStatus(
            quiet,
            SecretServiceKeyringReader.TryCreate(isPlatformSupported: () => true));
        Assert.Empty(quiet.Messages);
    }
}

internal sealed class MessageCapturingLogger : ILogger
{
    public List<string> Messages { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
