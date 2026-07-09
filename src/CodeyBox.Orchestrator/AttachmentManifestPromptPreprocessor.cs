using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Reserved built-in preprocessor for the future attachment delivery task.
/// The current attachment foundation stores and serves attachments through
/// the API only; it deliberately does not inject attachment filenames,
/// content types, captions, paths, or bytes into tool-bearing agent prompts.
/// </summary>
public sealed class AttachmentManifestPromptPreprocessor : IAgentPromptPreprocessor
{
    public AttachmentManifestPromptPreprocessor(
        ILogger<AttachmentManifestPromptPreprocessor> log,
        IWorkItemAttachmentSource? source = null)
    {
        _ = log;
        _ = source;
    }

    public int Order => AgentPromptPreprocessorOrder.BuiltInFirst + 100;

    public Task<string> ProcessAsync(PromptContext ctx, string prompt, CancellationToken ct = default)
    {
        _ = ctx;
        _ = ct;
        return Task.FromResult(prompt);
    }
}
