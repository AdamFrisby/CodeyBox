using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class RawOutputRedactionTests
{
    // ── Redact ────────────────────────────────────────────────────────────────

    [Fact]
    public void Redact_GHO_Token_IsReplaced()
    {
        var result = RawOutputRedactor.Redact("token: gho_ABCdef123456789012345678901234");
        Assert.DoesNotContain("gho_", result);
        Assert.Contains("***", result);
    }

    [Fact]
    public void Redact_GHP_Token_IsReplaced()
    {
        var result = RawOutputRedactor.Redact("Authorization: ghp_XYZabc789012345678901234567890");
        Assert.DoesNotContain("ghp_", result);
        Assert.Contains("***", result);
    }

    [Fact]
    public void Redact_GitHubPat_Token_IsReplaced()
    {
        var result = RawOutputRedactor.Redact("GITHUB_TOKEN=github_pat_AABB11ccDDee22ffGGhh33iiJJkk44llMMnn55ooPP66qqRR");
        Assert.DoesNotContain("github_pat_", result);
        Assert.Contains("***", result);
    }

    [Fact]
    public void Redact_SkAnt_Token_IsReplaced()
    {
        var result = RawOutputRedactor.Redact("key=sk-ant-api03-AABBCCDDEEFFGGHHIIJJKKLLMMNNOOPPQQRRSSTT-0123456");
        Assert.DoesNotContain("sk-ant-", result);
        Assert.Contains("***", result);
    }

    [Fact]
    public void Redact_AIza_Token_IsReplaced()
    {
        var result = RawOutputRedactor.Redact("api_key=AIzaSyBabcdefghijklmnopqrstuvwxyz0123456789a");
        Assert.DoesNotContain("AIzaSy", result);
        Assert.Contains("***", result);
    }

    [Fact]
    public void Redact_MultipleSecrets_AllReplaced()
    {
        var input = "token1=gho_aaa123 token2=ghp_bbb456 done";
        var result = RawOutputRedactor.Redact(input);
        Assert.DoesNotContain("gho_", result);
        Assert.DoesNotContain("ghp_", result);
        Assert.Equal("token1=*** token2=*** done", result);
    }

    [Fact]
    public void Redact_NoSecrets_ReturnsOriginal()
    {
        var input = "Build successful. 0 errors, 2 warnings.";
        var result = RawOutputRedactor.Redact(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Redact_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", RawOutputRedactor.Redact(""));
    }

    // ── TruncateToBytes ───────────────────────────────────────────────────────

    [Fact]
    public void TruncateToBytes_ShortString_Unchanged()
    {
        var text = "Hello, world!";
        var result = RawOutputRedactor.TruncateToBytes(text, 100);
        Assert.Equal(text, result);
    }

    [Fact]
    public void TruncateToBytes_ExactlyAtLimit_Unchanged()
    {
        var text = new string('a', 50);
        var result = RawOutputRedactor.TruncateToBytes(text, 50);
        Assert.Equal(text, result);
    }

    [Fact]
    public void TruncateToBytes_LongString_AppendsMarker()
    {
        var text = new string('x', 1000);
        var result = RawOutputRedactor.TruncateToBytes(text, 100);
        Assert.EndsWith("[...truncated]", result);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result) <= 100);
    }

    [Fact]
    public void TruncateToBytes_256KBLimit_Enforced()
    {
        const int MaxBytes = 256 * 1024;
        var text = new string('z', MaxBytes + 10_000);
        var result = RawOutputRedactor.TruncateToBytes(text, MaxBytes);
        Assert.EndsWith("[...truncated]", result);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result) <= MaxBytes);
    }

    [Fact]
    public void TruncateToBytes_MultiByte_DoesNotSplitCharacter()
    {
        // Each 'é' is 2 UTF-8 bytes. 100 'é' = 200 bytes. Truncate to 50 bytes.
        // Marker is 16 bytes, so budget = 34 bytes = 17 × 'é' before the marker.
        var text = new string('é', 100);
        var result = RawOutputRedactor.TruncateToBytes(text, 50);
        var encoded = System.Text.Encoding.UTF8.GetByteCount(result);
        Assert.True(encoded <= 50);
        Assert.EndsWith("[...truncated]", result);
        // The prefix contains only complete 'é' characters (2 bytes each),
        // so all prefix bytes should pair up evenly.
        var prefixLen = result.IndexOf('\n');
        Assert.True(prefixLen > 0);
        Assert.Equal(0, System.Text.Encoding.UTF8.GetByteCount(result[..prefixLen]) % 2);
    }
}
