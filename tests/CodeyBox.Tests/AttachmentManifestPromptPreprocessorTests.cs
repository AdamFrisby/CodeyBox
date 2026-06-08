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
