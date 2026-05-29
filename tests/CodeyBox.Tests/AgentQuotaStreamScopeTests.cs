using CodeyBox.Agents;

namespace CodeyBox.Tests;

public sealed class AgentQuotaStreamScopeTests
{
    [Fact]
    public void IsNonQuotaAgentApiCrash_Claude400ThinkingBlock_ReturnsTrue()
    {
        var stdout = """
            {"type":"result","subtype":"error","is_error":true,"api_error_status":400,"result":"messages: `thinking` blocks in the latest assistant message cannot be modified"}
            """;

        Assert.True(AgentQuotaStreamScope.IsNonQuotaAgentApiCrash(stderr: null, stdout));
    }

    [Fact]
    public void IsNonQuotaAgentApiCrash_Claude429_ReturnsFalse()
    {
        var stdout = """
            {"type":"result","subtype":"error","is_error":true,"api_error_status":429,"result":"Error: rate_limit_exceeded"}
            """;

        Assert.False(AgentQuotaStreamScope.IsNonQuotaAgentApiCrash(stderr: null, stdout));
    }

    [Fact]
    public void ScopeStdoutForQuotaDetection_UsesTerminalLineOnly()
    {
        var stdout = """
            {"type":"result","subtype":"error","is_error":true,"result":"Error: rate_limit_exceeded earlier"}
            {"type":"result","subtype":"error","is_error":true,"api_error_status":400,"result":"messages: `thinking` blocks in the latest assistant message cannot be modified"}
            """;

        var scoped = AgentQuotaStreamScope.ScopeStdoutForQuotaDetection(stdout);

        Assert.NotNull(scoped);
        Assert.Contains("api_error_status\":400", scoped);
        Assert.DoesNotContain("rate_limit_exceeded earlier", scoped);
    }
}
