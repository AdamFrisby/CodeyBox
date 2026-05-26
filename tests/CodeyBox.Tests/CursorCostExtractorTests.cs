using CodeyBox.Agents;
using CodeyBox.Agents.Cursor;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the Cursor cost extractor's deliberate "always returns null" contract.
/// The Cursor CLI does not currently emit a documented final-usage line, so the
/// extractor is a no-op and the cost calculator falls back to
/// <c>usageTotal.elapsedMs</c>. These tests fail if a future "helpful" refactor
/// adds speculative parsing — that change should be a deliberate decision, not
/// a silent one (a parser that misreads a sandbox log line as a usage line
/// would yield bogus per-item costs with no regression detector).
/// </summary>
public sealed class CursorCostExtractorTests
{
    private static readonly CursorCostExtractor Extractor = new();

    [Fact]
    public void Kind_IsCursor()
        => Assert.Equal(AgentKind.Cursor, Extractor.Kind);

    [Fact]
    public void DefaultPricing_IsNull()
    {
        // Cost is unknown for Cursor — the model is paid via the operator's
        // flat-rate subscription, so a per-million-token rate would be
        // misleading. Callers treat that as $0.
        Assert.Null(Extractor.DefaultPricing);
    }

    [Fact]
    public void TryExtract_NullInputs_ReturnsNull()
        => Assert.Null(Extractor.TryExtract(null, null));

    [Fact]
    public void TryExtract_EmptyInputs_ReturnsNull()
        => Assert.Null(Extractor.TryExtract("", ""));

    [Fact]
    public void TryExtract_StdoutWithUsageLikeShape_ReturnsNull()
    {
        // Even content that looks like a usage line (mirroring Codex/Claude
        // shapes) must NOT extract — Cursor's CLI does not document such a
        // line, and a speculative parser would be wrong-by-construction.
        var stdout = """{"usage":{"prompt_tokens":12345,"completion_tokens":678},"model":"composer-2.5"}""";

        Assert.Null(Extractor.TryExtract(stdout, null));
    }

    [Fact]
    public void TryExtract_StderrWithUsageLikeShape_ReturnsNull()
    {
        var stderr = "12,345 input tokens, 678 output tokens";

        Assert.Null(Extractor.TryExtract(null, stderr));
    }

    [Fact]
    public void TryExtract_BothStreamsPopulated_ReturnsNull()
    {
        // Arbitrary text in both streams still yields no extraction.
        var stdout = "Working on it...\nDone.";
        var stderr = "some diagnostic output";

        Assert.Null(Extractor.TryExtract(stdout, stderr));
    }
}
