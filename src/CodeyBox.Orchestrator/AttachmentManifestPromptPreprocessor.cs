using System.Text;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Built-in preprocessor that injects an ATTACHMENTS section listing every
/// blob the attachments foundation has staged into the sandbox for this work
/// item. The orchestrator stages the blob bytes; this preprocessor only
/// describes what is already on disk so the agent knows where to look.
/// <para>
/// No-op when <see cref="IWorkItemAttachmentSource"/> is not registered (the
/// attachments foundation has not been wired) or returns an empty list. The
/// preprocessor never reads files, never opens the sandbox — listing is the
/// foundation's job, injection is ours.
/// </para>
/// </summary>
public sealed class AttachmentManifestPromptPreprocessor : IAgentPromptPreprocessor
{
    private const int MaxAttachmentsListed = 200;
    private const int MaxCaptionChars = 500;

    private readonly IWorkItemAttachmentSource? _source;
    private readonly ILogger<AttachmentManifestPromptPreprocessor> _log;

    public AttachmentManifestPromptPreprocessor(
        ILogger<AttachmentManifestPromptPreprocessor> log,
        IWorkItemAttachmentSource? source = null)
    {
        _source = source;
        _log = log;
    }

    public int Order => AgentPromptPreprocessorOrder.BuiltInFirst + 100;

    public async Task<string> ProcessAsync(PromptContext ctx, string prompt, CancellationToken ct = default)
    {
        if (_source is null)
            return prompt;

        IReadOnlyList<WorkItemAttachment> attachments;
        try
        {
            attachments = await _source.ListAsync(ctx.ItemId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(
                ex,
                "Attachment source failed for work item {WorkItemId}; prompt left unchanged",
                ctx.ItemId);
            return prompt;
        }

        if (attachments.Count == 0)
            return prompt;

        var sb = new StringBuilder();
        sb.Append("## Attachments\n\n");
        sb.Append("The operator has staged the following files into the sandbox for this work item. ");
        sb.Append("Use the in-VM path to read them; do not assume any other location.\n\n");

        var listed = 0;
        foreach (var attachment in attachments)
        {
            if (listed >= MaxAttachmentsListed)
            {
                sb.Append($"\n[...and {attachments.Count - listed} more attachment(s) omitted by CodeyBox cap of {MaxAttachmentsListed}.]\n");
                break;
            }

            sb.Append("- **").Append(EscapeInline(attachment.FileName)).Append("**");
            if (!string.IsNullOrWhiteSpace(attachment.ContentType))
                sb.Append(" (").Append(EscapeInline(attachment.ContentType)).Append(')');
            sb.Append(" — `").Append(EscapeInline(attachment.InVmPath)).Append('`');
            if (!string.IsNullOrWhiteSpace(attachment.Caption))
            {
                var caption = attachment.Caption.Length > MaxCaptionChars
                    ? attachment.Caption[..MaxCaptionChars] + "…"
                    : attachment.Caption;
                sb.Append("\n  Caption: ").Append(EscapeInline(caption));
            }
            sb.Append('\n');
            listed++;
        }

        sb.Append("\n## Agent prompt\n\n");
        sb.Append(prompt);
        return sb.ToString();
    }

    private static string EscapeInline(string value) =>
        value.Replace("\r", string.Empty).Replace('\n', ' ');
}
