using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// Guards the terminal quota-error path: every agent-sourced string that
/// reaches <c>LastError</c> must pass through <see cref="SanitizedAgentDetail"/>
/// (redaction + truncation). Agent output is untrusted — provider error
/// bodies, repository content, model emissions — so a credential echoed by a
/// provider must never be persisted or API-served, and an unbounded summary
/// must never be persisted whole.
/// </summary>
public sealed class TerminalQuotaErrorRedactionUnitTests
{
    private const string SecretSummary = "exit 1 token ghp_AbCdEfGhIjKlMnOpQrStUvWxYz1234567890 leaked";

    [Fact]
    public void FromRaw_RedactsSecretShapedToken()
    {
        var detail = SanitizedAgentDetail.FromRaw(SecretSummary);

        Assert.DoesNotContain("ghp_AbCdEfGhIjKlMnOpQrStUvWxYz1234567890", detail.Value);
        Assert.Contains("***", detail.Value);
    }

    [Fact]
    public void FromRaw_TruncatesOversizedDetailToCap()
    {
        var detail = SanitizedAgentDetail.FromRaw(new string('x', 6000));

        Assert.Contains("[...truncated]", detail.Value);
        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(detail.Value) <= SanitizedAgentDetail.MaxBytes,
            $"sanitized detail exceeds {SanitizedAgentDetail.MaxBytes} bytes");
    }

    [Fact]
    public void FromRaw_NullBecomesEmpty()
    {
        Assert.Equal(string.Empty, SanitizedAgentDetail.FromRaw(null).Value);
    }

    [Fact]
    public void FromRaw_IsIdempotentForSinkGuard()
    {
        var once = SanitizedAgentDetail.FromRaw(SecretSummary).Value;
        var twice = SanitizedAgentDetail.FromRaw(once).Value;

        Assert.Equal(once, twice);
    }

    /// <summary>
    /// Covers each of the seven terminal-quota construction sites in
    /// <c>PipelineRunner</c> (planning, work, clean-exit/no-diff rework,
    /// audit stderr/stdout, audit terminal diagnostic, session-resume
    /// exhaustion, merge). Every site now builds its message as
    /// <c>QuotaFailureMessage(kind, trustedPrefix, SanitizedAgentDetail)</c>,
    /// so asserting each production prefix shape with a secret-carrying raw
    /// detail proves none can persist unredacted agent text.
    /// </summary>
    [Theory]
    [InlineData("Agent claude reported quota failure during planning")]
    [InlineData("Agent claude reported quota failure")]
    [InlineData("Agent claude reported quota failure on clean-exit/no-diff rework from captured stderr")]
    [InlineData("Audit agent claude reported quota failure while running test-auditor")]
    [InlineData("Audit agent claude reported quota failure on clean exit while running test-auditor")]
    [InlineData("Agent claude reported quota failure after exhausting session resume")]
    [InlineData("Merge agent claude reported quota failure")]
    public void QuotaFailureMessage_RedactsAgentText_AtEveryConstructionSite(string prefix)
    {
        foreach (var kind in new[] { QuotaFailureKind.RateLimitExceeded, QuotaFailureKind.LimitReached })
        {
            var message = PipelineRunner.QuotaFailureMessage(
                kind,
                prefix,
                SanitizedAgentDetail.FromRaw(SecretSummary));

            Assert.DoesNotContain("ghp_AbCdEfGhIjKlMnOpQrStUvWxYz1234567890", message);
            Assert.Contains(prefix switch
            {
                _ when prefix.Contains("reported quota failure") && kind == QuotaFailureKind.RateLimitExceeded
                    => "rate-limited by provider",
                _ => "reported quota failure",
            }, message);
        }
    }

    [Fact]
    public void QuotaFailureMessage_TruncatesOversizedAgentText()
    {
        var message = PipelineRunner.QuotaFailureMessage(
            QuotaFailureKind.LimitReached,
            "Agent claude reported quota failure",
            SanitizedAgentDetail.FromRaw(new string('y', 6000)));

        Assert.Contains("[...truncated]", message);
    }
}

[Collection("Pipeline integration")]
public sealed class TerminalQuotaErrorRedactionPersistenceTests : IDisposable
{
    private const string SecretToken = "ghp_AbCdEfGhIjKlMnOpQrStUvWxYz1234567890";
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-quota-redact-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task WorkPhaseQuotaFailure_WithSecretInSummary_PersistsRedactedLastError()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
        using var tp = TestSupport.BuildPipeline(_workspace, seed, agentOverride: agent);

        agent.WorkResults.Enqueue(new AgentResult(
            false,
            $"agent exited 1 with token {SecretToken} leaked by provider",
            null,
            "API Error: 429 rate_limit_exceeded: quota exhausted"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotNull(final.LastError);
        Assert.DoesNotContain(SecretToken, final.LastError);
        Assert.Contains("***", final.LastError);
    }

    [Fact]
    public async Task WorkPhaseQuotaFailure_WithOversizedSummary_TruncatesLastErrorToCap()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
        using var tp = TestSupport.BuildPipeline(_workspace, seed, agentOverride: agent);

        agent.WorkResults.Enqueue(new AgentResult(
            false,
            new string('z', 6000),
            null,
            "API Error: 429 rate_limit_exceeded: quota exhausted"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotNull(final.LastError);
        Assert.Contains("[...truncated]", final.LastError);
        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(final.LastError) <= SanitizedAgentDetail.MaxBytes + 256,
            $"LastError exceeds cap plus trusted-prefix allowance: {System.Text.Encoding.UTF8.GetByteCount(final.LastError)} bytes");
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/quota-redact",
        Agent = AgentKind.Claude,
        PushUpstream = false,
    };
}
