using CodeyBox.Agents.Cline;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="ClineCostExtractor"/>. Unlike the autohand bare
/// stream, cline's terminal <c>run_result</c> frame carries machine-readable
/// usage (verified against cline 3.0.62 live runs), so a recorded healthy
/// run yields a real snapshot. Output with no usage frame returns null
/// (unknown) rather than a zero that looks like data.
/// </summary>
public sealed class ClineCostExtractorTests
{
    private static ClineCostExtractor Extractor() => new();

    [Fact]
    public void Kind_IsCline()
    {
        Assert.Equal(AgentKind.Cline, Extractor().Kind);
    }

    [Fact]
    public void DefaultPricing_IsNull()
    {
        // No fallback rate: cost is computed from the measured token snapshot
        // against configured per-model rates, and a fallback would book a
        // rate that was never listed. The shipped free-tier member's $0 rate
        // lives in the pricing bucket, not here.
        Assert.Null(Extractor().DefaultPricing);
    }

    [Fact]
    public void TryExtract_RecordedHealthyRun_ReturnsMeasuredSnapshot()
    {
        // Recorded real output (cline 3.0.62, --json tool run that created a
        // file): per-iteration usage slices, a done event, and the terminal
        // run_result with cumulative aggregateUsage and the dispatch model.
        const string stdout =
            "{\"ts\":\"2026-09-16T18:02:02.531Z\",\"type\":\"agent_event\",\"event\":{\"type\":\"usage\",\"inputTokens\":6244,\"outputTokens\":131,\"totalInputTokens\":6244,\"totalOutputTokens\":131,\"totalCost\":0}}\n" +
            "{\"ts\":\"2026-09-16T18:02:04.251Z\",\"type\":\"agent_event\",\"event\":{\"type\":\"usage\",\"inputTokens\":6424,\"outputTokens\":72,\"cacheReadTokens\":4352,\"totalInputTokens\":12668,\"totalOutputTokens\":203,\"totalCacheReadTokens\":4352,\"totalCost\":0}}\n" +
            "{\"ts\":\"2026-09-16T18:02:09.659Z\",\"type\":\"agent_event\",\"event\":{\"type\":\"done\",\"reason\":\"completed\",\"text\":\"The task is complete.\",\"iterations\":3,\"usage\":{\"inputTokens\":19209,\"outputTokens\":294,\"cacheReadTokens\":8704,\"totalCost\":0}}}\n" +
            "{\"ts\":\"2026-09-16T18:02:09.691Z\",\"type\":\"run_result\",\"finishReason\":\"completed\",\"iterations\":3,\"usage\":{\"inputTokens\":19209,\"outputTokens\":294,\"cacheReadTokens\":8704,\"cacheWriteTokens\":0,\"totalCost\":0},\"aggregateUsage\":{\"inputTokens\":19209,\"outputTokens\":294,\"cacheReadTokens\":8704,\"cacheWriteTokens\":0,\"totalCost\":0},\"durationMs\":10075,\"text\":\"The task is complete.\",\"model\":{\"id\":\"nvidia/nemotron-3.5-lightning:free\",\"provider\":\"openrouter\"}}";

        var snapshot = Extractor().TryExtract(stdout, string.Empty);

        Assert.NotNull(snapshot);
        // Cumulative aggregateUsage wins over the per-iteration slices.
        Assert.Equal(19209, snapshot.InputTokens);
        Assert.Equal(8704, snapshot.CachedInputTokens);
        Assert.Equal(294, snapshot.OutputTokens);
        Assert.Equal("nvidia/nemotron-3.5-lightning:free", snapshot.ModelId);
    }

    [Fact]
    public void TryExtract_RecordedQuotaFailure_ReturnsZeroSnapshot()
    {
        // Recorded real output (cline 3.0.62, $0-spend-limit key against a
        // paid model, exit 1): the error run_result measures zero token
        // spend — that IS data (nothing was billed), so a zero snapshot is
        // returned rather than unknown. The workspace key id in the recorded
        // message is redacted — it is secret-derived.
        const string stdout =
            "{\"ts\":\"2026-09-16T18:01:51.679Z\",\"type\":\"agent_event\",\"event\":{\"type\":\"error\",\"error\":{\"name\":\"Error\",\"message\":\"Key limit exceeded (total limit). Manage it using https://openrouter.ai/workspaces/default/keys/REDACTED\"},\"errorClass\":\"auth\",\"recoverable\":false,\"iteration\":1}}\n" +
            "{\"ts\":\"2026-09-16T18:01:51.706Z\",\"type\":\"run_result\",\"finishReason\":\"error\",\"iterations\":1,\"usage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"totalCost\":0},\"aggregateUsage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"totalCost\":0},\"durationMs\":152,\"text\":\"Key limit exceeded (total limit). Manage it using https://openrouter.ai/workspaces/default/keys/REDACTED\",\"model\":{\"id\":\"anthropic/claude-sonnet-4\",\"provider\":\"openrouter\"}}";
        const string stderr =
            "{\"ts\":\"2026-09-16T18:01:51.706Z\",\"type\":\"error\",\"message\":\"Key limit exceeded (total limit). Manage it using https://openrouter.ai/workspaces/default/keys/REDACTED\"}";

        var snapshot = Extractor().TryExtract(stdout, stderr);

        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot.InputTokens);
        Assert.Equal(0, snapshot.CachedInputTokens);
        Assert.Equal(0, snapshot.OutputTokens);
        Assert.Equal("anthropic/claude-sonnet-4", snapshot.ModelId);
    }

    [Fact]
    public void TryExtract_EmptyResponse_ReturnsNull()
    {
        // No machine-readable usage anywhere: unknown, never a zero that
        // looks like measured data.
        Assert.Null(Extractor().TryExtract(null, null));
        Assert.Null(Extractor().TryExtract(string.Empty, string.Empty));
        Assert.Null(Extractor().TryExtract("some plain log line\n", "another one\n"));
    }

    [Fact]
    public void TryExtract_TruncatedStreamWithoutRunResult_FallsBackToDoneUsage()
    {
        // A stream cut before the terminal frame still carries the
        // cumulative done-event usage — the extractor reports that rather
        // than unknown.
        const string stdout =
            "{\"ts\":\"2026-09-16T18:02:09.659Z\",\"type\":\"agent_event\",\"event\":{\"type\":\"done\",\"reason\":\"completed\",\"text\":\"The task is complete.\",\"iterations\":3,\"usage\":{\"inputTokens\":19209,\"outputTokens\":294,\"cacheReadTokens\":8704,\"totalCost\":0}}}";

        var snapshot = Extractor().TryExtract(stdout, null);

        Assert.NotNull(snapshot);
        Assert.Equal(19209, snapshot.InputTokens);
        Assert.Equal(8704, snapshot.CachedInputTokens);
        Assert.Equal(294, snapshot.OutputTokens);
        Assert.Null(snapshot.ModelId);
    }

    [Fact]
    public void TryExtract_NeverThrows_OnHostileInput()
    {
        Assert.Null(Extractor().TryExtract("{not json", "{also not json"));
        Assert.Null(Extractor().TryExtract("{\"type\":\"run_result\",\"usage\":[]}", null));
        Assert.Null(Extractor().TryExtract("{\"type\":\"run_result\",\"usage\":{\"inputTokens\":-5}}", null));
    }
}
