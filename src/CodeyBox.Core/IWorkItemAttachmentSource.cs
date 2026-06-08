namespace CodeyBox.Core;

/// <summary>
/// One attachment that travels with a work item and is staged into the sandbox
/// before the agent runs. The orchestrator delivers blobs to <see cref="InVmPath"/>;
/// the <see cref="AttachmentManifestPromptPreprocessor"/> emits a manifest
/// section so the agent knows the file exists, where to find it, what it is,
/// and why the operator attached it.
/// </summary>
/// <param name="InVmPath">Absolute path inside the sandbox where the blob has been staged.</param>
/// <param name="FileName">Human-friendly filename (independent of <see cref="InVmPath"/>'s on-disk name).</param>
/// <param name="ContentType">MIME type (e.g. <c>image/png</c>, <c>text/markdown</c>). Empty when unknown.</param>
/// <param name="Caption">Operator-supplied note explaining what this attachment is for. May be empty.</param>
public sealed record WorkItemAttachment(
    string InVmPath,
    string FileName,
    string ContentType,
    string Caption);

/// <summary>
/// Returns the attachments registered for a work item. Implementations are
/// optional: when no source is wired the
/// <see cref="AttachmentManifestPromptPreprocessor"/> is a no-op.
/// <para>
/// The in-VM staging of blobs (writing each attachment to its
/// <see cref="WorkItemAttachment.InVmPath"/>) is the responsibility of the
/// attachments foundation that owns the source, NOT of the preprocessor —
/// the preprocessor only describes what has already been staged so the agent
/// can find it.
/// </para>
/// </summary>
public interface IWorkItemAttachmentSource
{
    Task<IReadOnlyList<WorkItemAttachment>> ListAsync(WorkItemId itemId, CancellationToken ct = default);
}
