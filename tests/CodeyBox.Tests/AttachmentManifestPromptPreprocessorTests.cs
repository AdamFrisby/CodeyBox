using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class AttachmentManifestPromptPreprocessorTests
{
    [Fact]
    public async Task InjectsManifestSection_WhenAttachmentsPresent()
    {
        var source = new StubAttachmentSource(
        [
            new WorkItemAttachment("/work/.codeybox/attachments/spec.md", "spec.md", "text/markdown", "Original spec"),
            new WorkItemAttachment("/work/.codeybox/attachments/repro.png", "repro.png", "image/png", "Screenshot of the bug"),
        ]);
        var preprocessor = new AttachmentManifestPromptPreprocessor(
            NullLogger<AttachmentManifestPromptPreprocessor>.Instance,
            source);

        var result = await preprocessor.ProcessAsync(NewContext(), "do the work");

        Assert.Contains("## Attachments", result);
        Assert.Contains("**spec.md** (text/markdown) — `/work/.codeybox/attachments/spec.md`", result);
        Assert.Contains("Caption: Original spec", result);
        Assert.Contains("**repro.png** (image/png) — `/work/.codeybox/attachments/repro.png`", result);
        Assert.Contains("Caption: Screenshot of the bug", result);
        Assert.Contains("## Agent prompt\n\ndo the work", result);
    }

    [Fact]
    public async Task NoOp_WhenSourceNotRegistered()
    {
        var preprocessor = new AttachmentManifestPromptPreprocessor(
            NullLogger<AttachmentManifestPromptPreprocessor>.Instance,
            source: null);

        var result = await preprocessor.ProcessAsync(NewContext(), "untouched");

        Assert.Equal("untouched", result);
    }

    [Fact]
    public async Task NoOp_WhenAttachmentListIsEmpty()
    {
        var preprocessor = new AttachmentManifestPromptPreprocessor(
            NullLogger<AttachmentManifestPromptPreprocessor>.Instance,
            new StubAttachmentSource([]));

        var result = await preprocessor.ProcessAsync(NewContext(), "untouched");

        Assert.Equal("untouched", result);
    }

    [Fact]
    public async Task NoOp_WhenSourceThrows()
    {
        var preprocessor = new AttachmentManifestPromptPreprocessor(
            NullLogger<AttachmentManifestPromptPreprocessor>.Instance,
            new ThrowingAttachmentSource());

        var result = await preprocessor.ProcessAsync(NewContext(), "untouched");

        Assert.Equal("untouched", result);
    }

    [Fact]
    public async Task OmitsContentTypeAndCaption_WhenEmpty()
    {
        var source = new StubAttachmentSource(
        [
            new WorkItemAttachment("/work/.codeybox/attachments/blob.bin", "blob.bin", "", ""),
        ]);
        var preprocessor = new AttachmentManifestPromptPreprocessor(
            NullLogger<AttachmentManifestPromptPreprocessor>.Instance,
            source);

        var result = await preprocessor.ProcessAsync(NewContext(), "do the work");

        Assert.Contains("**blob.bin** — `/work/.codeybox/attachments/blob.bin`", result);
        Assert.DoesNotContain("Caption:", result);
    }

    [Fact]
    public async Task CapsManifestAt200Entries_AndAppendsOmittedFooter()
    {
        // The orchestrator caps the manifest at 200 entries so a work item
        // with thousands of attachments can't blow the agent's context window.
        // The remaining count is reported so the agent knows additional files
        // exist on disk even though they are not listed.
        var attachments = new List<WorkItemAttachment>();
        const int total = 250;
        for (var i = 0; i < total; i++)
            attachments.Add(new WorkItemAttachment(
                InVmPath: $"/work/.codeybox/attachments/file{i}.txt",
                FileName: $"file{i}.txt",
                ContentType: "text/plain",
                Caption: ""));

        var preprocessor = new AttachmentManifestPromptPreprocessor(
            NullLogger<AttachmentManifestPromptPreprocessor>.Instance,
            new StubAttachmentSource(attachments));

        var result = await preprocessor.ProcessAsync(NewContext(), "prompt");

        Assert.Contains("file0.txt", result);
        Assert.Contains("file199.txt", result);
        Assert.DoesNotContain("file200.txt", result);
        Assert.DoesNotContain("file249.txt", result);
        Assert.Contains($"[...and {total - 200} more attachment(s) omitted by CodeyBox cap of 200.]", result);
    }

    [Fact]
    public async Task TruncatesCaptionAt500Chars_AndAppendsEllipsis()
    {
        // A 1500-char caption gets cut at 500 chars + an ellipsis so an
        // attachment with an absurdly long caption can't dominate the prompt.
        var caption = new string('a', 1500);
        var preprocessor = new AttachmentManifestPromptPreprocessor(
            NullLogger<AttachmentManifestPromptPreprocessor>.Instance,
            new StubAttachmentSource(
            [
                new WorkItemAttachment("/work/.codeybox/attachments/big.txt", "big.txt", "text/plain", caption),
            ]));

        var result = await preprocessor.ProcessAsync(NewContext(), "prompt");

        var captionLine = result
            .Split('\n')
            .Single(line => line.TrimStart().StartsWith("Caption: ", StringComparison.Ordinal));
        var captionValue = captionLine.TrimStart()["Caption: ".Length..];
        // 500 'a' chars + the ellipsis (one UTF-16 code unit).
        Assert.Equal(501, captionValue.Length);
        Assert.EndsWith("…", captionValue);
        Assert.StartsWith("aaaa", captionValue);
    }

    [Fact]
    public async Task CaptionTruncation_DoesNotSplitUtf16SurrogatePair()
    {
        // A supplementary-plane emoji (e.g. U+1F4A9) is two UTF-16 code units.
        // If the cap lands between the high and low surrogate, a naive slice
        // would produce an invalid string that re-encodes to U+FFFD. We pad
        // a caption so the 500-char boundary falls inside such a pair and
        // verify the result is valid UTF-16.
        var prefix = new string('a', 499); // index 499 will be the high surrogate
        const string emoji = "💩"; // pile of poo
        var caption = prefix + emoji + new string('b', 100);
        var preprocessor = new AttachmentManifestPromptPreprocessor(
            NullLogger<AttachmentManifestPromptPreprocessor>.Instance,
            new StubAttachmentSource(
            [
                new WorkItemAttachment("/work/.codeybox/attachments/big.txt", "big.txt", "text/plain", caption),
            ]));

        var result = await preprocessor.ProcessAsync(NewContext(), "prompt");

        // No invalid surrogate should have been emitted: re-encoding to UTF-8
        // and back must round-trip cleanly without any replacement characters.
        var roundTripped = System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(result));
        Assert.Equal(result, roundTripped);
        Assert.DoesNotContain('�', result);

        var captionLine = result
            .Split('\n')
            .Single(line => line.TrimStart().StartsWith("Caption: ", StringComparison.Ordinal));
        var captionValue = captionLine.TrimStart()["Caption: ".Length..];
        // The truncator stepped back one char to avoid splitting the pair, so
        // we keep 499 'a's plus the ellipsis (500 UTF-16 code units total).
        Assert.Equal(500, captionValue.Length);
        Assert.EndsWith("…", captionValue);
    }

    [Fact]
    public async Task EscapesBacktickInPath_SoSyntheticCodeSpanCannotBreakOut()
    {
        // The in-VM path is rendered inside a single-backtick markdown code
        // span. A path that contains a backtick would otherwise close the span
        // and leak into the surrounding markdown. We substitute U+02CB so the
        // path stays inside its span.
        var attachments = new[]
        {
            new WorkItemAttachment("/work/staging/`weird`.bin", "weird.bin", "application/octet-stream", ""),
        };
        var preprocessor = new AttachmentManifestPromptPreprocessor(
            NullLogger<AttachmentManifestPromptPreprocessor>.Instance,
            new StubAttachmentSource(attachments));

        var result = await preprocessor.ProcessAsync(NewContext(), "prompt");

        // Locate the path's code span and verify it has no embedded raw backtick
        // (any backtick inside would close the span).
        var manifestStart = result.IndexOf("## Attachments", StringComparison.Ordinal);
        var manifestEnd = result.IndexOf("## Agent prompt", manifestStart, StringComparison.Ordinal);
        var manifest = result[manifestStart..manifestEnd];
        // Count of standalone backticks in the manifest should be exactly two
        // (open and close of the single path's code span) — none should leak
        // from the path itself.
        var backtickCount = manifest.Count(c => c == '`');
        Assert.Equal(2, backtickCount);
        Assert.Contains("ˋweirdˋ.bin", manifest);
    }

    [Fact]
    public async Task StripsNewlinesFromFields_SoManifestStaysOneEntryPerLine()
    {
        // The manifest renders one attachment per line. A malicious or sloppy
        // filename/path/caption with an embedded newline would otherwise break
        // the line-per-attachment shape and could be exploited to inject
        // synthetic markdown headings the agent treats as authoritative.
        var source = new StubAttachmentSource(
        [
            new WorkItemAttachment(
                InVmPath: "/work/safe\n/etc/passwd",
                FileName: "spec\nv2.md",
                ContentType: "text/markdown\n",
                Caption: "line1\nline2"),
        ]);
        var preprocessor = new AttachmentManifestPromptPreprocessor(
            NullLogger<AttachmentManifestPromptPreprocessor>.Instance,
            source);

        var result = await preprocessor.ProcessAsync(NewContext(), "prompt");

        var manifestStart = result.IndexOf("## Attachments", StringComparison.Ordinal);
        var manifestEnd = result.IndexOf("## Agent prompt", manifestStart, StringComparison.Ordinal);
        Assert.True(manifestStart >= 0 && manifestEnd > manifestStart);
        var manifest = result[manifestStart..manifestEnd];
        Assert.DoesNotContain("/etc/passwd\n", manifest);
        Assert.Contains("spec v2.md", manifest);
        Assert.Contains("line1 line2", manifest);
    }

    private static PromptContext NewContext() =>
        new(
            WorkItemId.New(),
            AgentKind.Claude,
            AgentPromptPhase.Work,
            1,
            NewProject(),
            new NoopSandbox(),
            "/work");

    private static Project NewProject() => new()
    {
        Id = new ProjectId("test-project"),
        DisplayName = "Test Project",
        RepositoryUrl = "https://example.invalid/repo.git",
    };

    private sealed class StubAttachmentSource(IReadOnlyList<WorkItemAttachment> attachments) : IWorkItemAttachmentSource
    {
        public Task<IReadOnlyList<WorkItemAttachment>> ListAsync(WorkItemId itemId, CancellationToken ct = default)
        {
            _ = itemId;
            _ = ct;
            return Task.FromResult(attachments);
        }
    }

    private sealed class ThrowingAttachmentSource : IWorkItemAttachmentSource
    {
        public Task<IReadOnlyList<WorkItemAttachment>> ListAsync(WorkItemId itemId, CancellationToken ct = default)
        {
            _ = itemId;
            _ = ct;
            throw new InvalidOperationException("attachment store offline");
        }
    }

    private sealed class NoopSandbox : ISandbox
    {
        public string Id => "noop";
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            _ = exec;
            _ = ct;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }
    }
}
