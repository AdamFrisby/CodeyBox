using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="AgentSmokeCache"/> and <see cref="SmokeCredentialFingerprint"/>.
/// </summary>
public sealed class SmokeCacheTests
{
    private static AgentCredential MakeCred(string token) =>
        new(AgentKind.Claude,
            new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = token },
            new Dictionary<string, string>());

    private static AgentSmokeResult OkResult => new(true, null, TimeSpan.FromMilliseconds(100));
    private static AgentSmokeResult FailResult => new(false, "auth", TimeSpan.FromMilliseconds(50));

    // ── Fingerprint ───────────────────────────────────────────────────────────

    [Fact]
    public void SameToken_ProducesSameFingerprint()
    {
        var c1 = MakeCred("tok-abc");
        var c2 = MakeCred("tok-abc");
        Assert.Equal(
            SmokeCredentialFingerprint.Compute(c1),
            SmokeCredentialFingerprint.Compute(c2));
    }

    [Fact]
    public void DifferentToken_ProducesDifferentFingerprint()
    {
        Assert.NotEqual(
            SmokeCredentialFingerprint.Compute(MakeCred("token-A")),
            SmokeCredentialFingerprint.Compute(MakeCred("token-B")));
    }

    [Fact]
    public void EmptyCred_ReturnsStableFingerprint()
    {
        var cred = new AgentCredential(AgentKind.Claude,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());
        var fp1 = SmokeCredentialFingerprint.Compute(cred);
        var fp2 = SmokeCredentialFingerprint.Compute(cred);
        Assert.Equal(fp1, fp2);
    }

    // ── Cache hit within TTL ──────────────────────────────────────────────────

    [Fact]
    public void SameFingerprint_WithinTtl_ReturnsCachedResult()
    {
        var cache = new AgentSmokeCache(TimeSpan.FromMinutes(15));
        var fp = SmokeCredentialFingerprint.Compute(MakeCred("tok"));
        cache.Set(AgentKind.Claude, fp, OkResult);

        var result = cache.TryGet(AgentKind.Claude, fp);
        Assert.NotNull(result);
        Assert.True(result!.Ok);
    }

    [Fact]
    public void DifferentFingerprint_DoesNotHitCache()
    {
        var cache = new AgentSmokeCache(TimeSpan.FromMinutes(15));
        var fp1 = SmokeCredentialFingerprint.Compute(MakeCred("tok-A"));
        var fp2 = SmokeCredentialFingerprint.Compute(MakeCred("tok-B"));
        cache.Set(AgentKind.Claude, fp1, OkResult);

        var result = cache.TryGet(AgentKind.Claude, fp2);
        Assert.Null(result);
    }

    [Fact]
    public void DifferentAgentKind_DoesNotHitCache()
    {
        var cache = new AgentSmokeCache(TimeSpan.FromMinutes(15));
        var fp = SmokeCredentialFingerprint.Compute(MakeCred("tok"));
        cache.Set(AgentKind.Claude, fp, OkResult);

        var result = cache.TryGet(AgentKind.Codex, fp);
        Assert.Null(result);
    }

    // ── TTL expiry ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AfterTtlExpiry_CacheMiss()
    {
        var cache = new AgentSmokeCache(TimeSpan.Zero);
        var fp = SmokeCredentialFingerprint.Compute(MakeCred("tok"));
        cache.Set(AgentKind.Claude, fp, OkResult);

        // Zero TTL → already expired on the next read.
        await Task.Delay(1);
        var result = cache.TryGet(AgentKind.Claude, fp);
        Assert.Null(result);
    }

    // ── Cache stores failure results ──────────────────────────────────────────

    [Fact]
    public void FailedResult_IsAlsoCached()
    {
        var cache = new AgentSmokeCache(TimeSpan.FromMinutes(15));
        var fp = SmokeCredentialFingerprint.Compute(MakeCred("bad-tok"));
        cache.Set(AgentKind.Claude, fp, FailResult);

        var result = cache.TryGet(AgentKind.Claude, fp);
        Assert.NotNull(result);
        Assert.False(result!.Ok);
        Assert.Equal("auth", result.FailureReason);
    }

    // ── Overwrite ────────────────────────────────────────────────────────────

    [Fact]
    public void Set_OverwritesExistingEntry()
    {
        var cache = new AgentSmokeCache(TimeSpan.FromMinutes(15));
        var fp = SmokeCredentialFingerprint.Compute(MakeCred("tok"));
        cache.Set(AgentKind.Claude, fp, FailResult);
        cache.Set(AgentKind.Claude, fp, OkResult);

        var result = cache.TryGet(AgentKind.Claude, fp);
        Assert.NotNull(result);
        Assert.True(result!.Ok);
    }

    // ── Absent key ────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyCache_ReturnsNull()
    {
        var cache = new AgentSmokeCache(TimeSpan.FromMinutes(15));
        Assert.Null(cache.TryGet(AgentKind.Claude, "any-fingerprint"));
    }
}
