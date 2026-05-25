using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Claude;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Covers <see cref="CredentialFileSource"/> behaviour and its plumbing into
/// quota probes (cache invalidation on <c>TokenUpdated</c>). These are the
/// guarantees the CB-quota-oauth-cache-invalidation fix relies on so an
/// out-of-band OAuth refresh (operator running the CLI on the host, scripted
/// rotation, child-VM writeback) is observed by every consumer within ~1 s
/// without restarting CodeyBox.
/// </summary>
public sealed class CredentialFileSourceTests : IDisposable
{
    private readonly string _tempDir;

    public CredentialFileSourceTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("codeybox-credential-file-source-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void GetRaw_ReturnsCachedContent_OnFirstCall()
    {
        var path = WriteFile("creds.json", """{"access_token":"first"}""");
        using var source = new CredentialFileSource(path);

        var raw = source.GetRaw();

        Assert.Equal("""{"access_token":"first"}""", raw);
    }

    [Fact]
    public void GetRaw_ReturnsNull_WhenFileMissing()
    {
        using var source = new CredentialFileSource(Path.Combine(_tempDir, "missing.json"));
        Assert.Null(source.GetRaw());
    }

    [Fact]
    public void WatchFalse_DisablesWatcherButKeepsStatBasedReload()
    {
        var path = WriteFile("polling-creds.json", """{"access_token":"old"}""");
        using var source = new CredentialFileSource(path, watch: false);

        Assert.False(TestFileSystemWatcherLeakTracker.IsTrackingPath(path));
        Assert.Equal("""{"access_token":"old"}""", source.GetRaw());

        File.WriteAllText(path, """{"access_token":"new-longer"}""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMilliseconds(100));

        Assert.Equal("""{"access_token":"new-longer"}""", source.GetRaw());
        Assert.False(TestFileSystemWatcherLeakTracker.IsTrackingPath(path));
    }

    [Fact]
    public async Task FileWatch_PicksUpFreshTokenWithinOneSecond()
    {
        // The "host-side autonomous OAuth refresh" code path rewrites the file
        // outside the CodeyBox process. Within ~1 s the source must observe
        // the new contents and raise TokenUpdated so downstream caches drop.
        var path = WriteFile("creds.json", """{"access_token":"stale"}""");
        using var source = new CredentialFileSource(path);
        Assert.Equal("""{"access_token":"stale"}""", source.GetRaw());

        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.TokenUpdated += () => observed.TrySetResult(true);

        File.WriteAllText(path, """{"access_token":"fresh"}""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMilliseconds(50));

        // Allow either FileSystemWatcher or the stat-based backstop on GetRaw()
        // to deliver the update within the one-second budget.
        var fresh = await PollAsync(() =>
        {
            var raw = source.GetRaw();
            return raw is not null && raw.Contains("fresh") ? raw : null;
        }, TimeSpan.FromSeconds(2));

        Assert.NotNull(fresh);
        Assert.Contains("fresh", fresh!);
        // Wait for the event itself rather than asserting IsCompleted: under
        // load the watcher thread can update the cache (which lets the next
        // GetRaw short-circuit on matching mtime/length) a beat before it
        // reaches RaiseTokenUpdated, so the poll above can return "fresh"
        // while the event is still in flight.
        var fired = await Task.WhenAny(observed.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(observed.Task, fired);
    }

    [Fact]
    public void LeakTracker_PrintsWarning_WhenCredentialWatcherIsNotDisposed()
    {
        var path = WriteFile("leaky-creds.json", """{"access_token":"leaky"}""");
        var source = new CredentialFileSource(path);
        try
        {
            using var writer = new StringWriter();
            Assert.True(TestFileSystemWatcherLeakTracker.ReportLeaks(writer));
            var output = writer.ToString();
            Assert.Contains("FileSystemWatcher", output);
            Assert.Contains(path, output);
        }
        finally
        {
            source.Dispose();
        }
    }

    [Fact]
    public async Task QuotaProbe_RefetchesAfterTokenUpdated_WhenFileChanges()
    {
        // The cache-invalidation guarantee: a stale snapshot must be dropped
        // when the host file is rewritten. Without this, /quota stays
        // "HTTP 401" for the entire cache TTL.
        //
        // Critical: the two file versions parse to the SAME access_token.
        // ClaudeQuotaProbe already cache-keys on the token string, so a
        // differing token would trigger a refetch even if InvalidateCache
        // were a no-op. By keeping the token identical and only changing
        // sibling fields, the only mechanism that can cause the second
        // GetAvailabilityAsync to refetch is the TokenUpdated→InvalidateCache
        // wiring this test is meant to exercise.
        var path = WriteFile("creds.json", """{"access_token":"same","refresh_token":"r1"}""");
        using var source = new CredentialFileSource(path);

        int callCount = 0;
        var capturedAuths = new List<string?>();
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK,
            "{\"rate_limit\":{\"primary_window\":{\"used_percent\":0,\"reset_at\":0}}}",
            req =>
            {
                callCount++;
                capturedAuths.Add(req.Headers.Authorization?.ToString());
            });
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        // Long TTL: without invalidation the second call would return cached.
        var probe = new ClaudeQuotaProbe(
            factory,
            () =>
            {
                var raw = source.GetRaw();
                var match = raw is null ? null : System.Text.RegularExpressions.Regex.Match(
                    raw, "\"access_token\":\"([^\"]+)\"").Groups[1].Value;
                return new AgentQuotaCredentials(string.IsNullOrEmpty(match) ? null : match);
            },
            TimeSpan.FromHours(1),
            NullLogger<ClaudeQuotaProbe>.Instance);
        source.TokenUpdated += probe.InvalidateCache;

        var member = new AgentMembership
        {
            Agent = AgentKind.Claude,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        };

        await probe.GetAvailabilityAsync(member, CancellationToken.None);
        Assert.Equal(1, callCount);

        // Rewrite the file with the SAME access_token but a different
        // refresh_token. The source must observe the change and the
        // subscribed probe.InvalidateCache must drop the snapshot so the next
        // probe call hits the network. Without InvalidateCache being wired
        // up, the cache would survive (same token = same key) and callCount
        // would stay at 1.
        File.WriteAllText(path, """{"access_token":"same","refresh_token":"r2"}""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMilliseconds(50));

        await PollAsync(() => source.GetRaw()?.Contains("r2") == true ? (object?)true : null,
            TimeSpan.FromSeconds(2));

        await probe.GetAvailabilityAsync(member, CancellationToken.None);
        Assert.Equal(2, callCount);
        Assert.Equal(new[] { "Bearer same", "Bearer same" }, capturedAuths);
    }

    [Fact]
    public async Task ClaudeQuotaProbe_InvalidateCache_ForcesRefetchOnNextCall()
    {
        // Direct unit test: with no file watcher involved, InvalidateCache()
        // on its own must cause the next GetAvailabilityAsync to hit the
        // network even though the supplied token is unchanged. Catches the
        // failure modes the wiring test cannot — InvalidateCache as a no-op,
        // deadlock from a missing lock release, or clearing the wrong field.
        int callCount = 0;
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK,
            "{\"rate_limit\":{\"primary_window\":{\"used_percent\":0,\"reset_at\":0}}}",
            _ => callCount++);
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        var probe = new ClaudeQuotaProbe(
            factory,
            "stable-token",
            TimeSpan.FromHours(1),
            NullLogger<ClaudeQuotaProbe>.Instance);

        var member = new AgentMembership
        {
            Agent = AgentKind.Claude,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        };

        await probe.GetAvailabilityAsync(member, CancellationToken.None);
        await probe.GetAvailabilityAsync(member, CancellationToken.None);
        Assert.Equal(1, callCount); // long TTL: second call cached.

        probe.InvalidateCache();

        await probe.GetAvailabilityAsync(member, CancellationToken.None);
        Assert.Equal(2, callCount); // invalidation forced a refetch.

        // InvalidateCache must release its lock — a missing release would
        // deadlock the next GetAvailabilityAsync. A 2 s budget catches that.
        probe.InvalidateCache();
        var refetch = probe.GetAvailabilityAsync(member, CancellationToken.None);
        var winner = await Task.WhenAny(refetch, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(refetch, winner);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task TornWrite_EventuallySettles_WithoutPropagatingException()
    {
        // Simulate the CLI mid-write: an attacker scenario is a partial JSON
        // that doesn't parse. The source must keep its previous cached snapshot
        // and recover once the writer finishes — never expose a half-written
        // value or throw out of GetRaw().
        var path = WriteFile("creds.json", """{"access_token":"good-old"}""");
        using var source = new CredentialFileSource(path);
        Assert.Equal("""{"access_token":"good-old"}""", source.GetRaw());

        // Truncated JSON — invalid.
        File.WriteAllText(path, """{"access_token":""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMilliseconds(10));

        // The source must not throw and must keep the prior content — never
        // surface the half-written torn JSON to callers.
        var rawDuringTear = source.GetRaw();
        Assert.Equal("""{"access_token":"good-old"}""", rawDuringTear);

        await Task.Delay(50);

        // Writer finishes — full valid JSON lands.
        File.WriteAllText(path, """{"access_token":"good-new"}""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMilliseconds(100));

        var settled = await PollAsync(() =>
        {
            var raw = source.GetRaw();
            return raw is not null && raw.Contains("good-new") ? raw : null;
        }, TimeSpan.FromSeconds(2));

        Assert.NotNull(settled);
        Assert.Contains("good-new", settled!);
    }

    [Fact]
    public async Task ConcurrentReads_DuringWrite_AllReturnValidValueOrNull()
    {
        // 10 parallel GetRaw calls while a separate writer rewrites the file.
        // No reader should observe a parse error; each must return either the
        // previous or the new contents.
        var path = WriteFile("creds.json", """{"access_token":"v1"}""");
        using var source = new CredentialFileSource(path);
        // Prime the cache.
        Assert.Equal("""{"access_token":"v1"}""", source.GetRaw());

        using var writerStop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            int i = 0;
            while (!writerStop.Token.IsCancellationRequested)
            {
                try
                {
                    File.WriteAllText(path, $$"""{"access_token":"v{{i++}}"}""");
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                }
                catch (IOException) { }
                Thread.Sleep(5);
            }
        });

        var readers = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                var raw = source.GetRaw();
                if (raw is null) continue;
                // Must contain a valid token field with closing quote — never a
                // half-written torn read like `{"access_token":"v17`.
                Assert.Matches(@"""access_token"":""v\d+""", raw);
            }
        })).ToArray();

        await Task.WhenAll(readers);
        writerStop.Cancel();
        await writer;
    }

    [Fact]
    public void TokenUpdated_NotRaised_WhenContentUnchanged()
    {
        var path = WriteFile("creds.json", """{"access_token":"same"}""");
        using var source = new CredentialFileSource(path);
        // Force the initial read.
        Assert.Equal("""{"access_token":"same"}""", source.GetRaw());

        int notifications = 0;
        source.TokenUpdated += () => Interlocked.Increment(ref notifications);

        // Rewrite the file with the same content but bumped mtime.
        File.WriteAllText(path, """{"access_token":"same"}""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMilliseconds(50));

        // GetRaw checks mtime and reloads, but the content matches the cache.
        // No subscriber should fire.
        Thread.Sleep(150);
        var raw = source.GetRaw();
        Assert.Equal("""{"access_token":"same"}""", raw);
        Assert.Equal(0, notifications);
    }

    private static async Task<T?> PollAsync<T>(Func<T?> probe, TimeSpan budget) where T : class
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            var v = probe();
            if (v is not null) return v;
            await Task.Delay(25);
        }
        return null;
    }
}
