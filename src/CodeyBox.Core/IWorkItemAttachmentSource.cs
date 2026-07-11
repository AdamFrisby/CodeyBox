namespace CodeyBox.Core;

/// <summary>
/// One work-item attachment projected for in-VM delivery: the sandbox path its
/// bytes are staged to, plus the metadata the agent prompt announces. The
/// orchestrator's <c>AttachmentManifestPromptPreprocessor</c> stages the blob
/// addressed by <see cref="Sha256"/> to <see cref="InVmPath"/> and lists it for
/// the agent.
/// </summary>
/// <param name="InVmPath">Absolute path inside the sandbox where the blob is staged.</param>
/// <param name="FileName">Human-friendly filename (matches <see cref="InVmPath"/>'s basename).</param>
/// <param name="ContentType">MIME type (e.g. <c>image/png</c>, <c>text/markdown</c>). Empty when unknown.</param>
/// <param name="Caption">Operator-supplied note explaining what this attachment is for. May be empty.</param>
/// <param name="SizeBytes">Blob size in bytes, surfaced in the manifest and used to bound the staged read.</param>
/// <param name="Sha256">Lowercase-hex SHA-256 of the blob bytes; the content-addressed key the blob store reads from.</param>
public sealed record WorkItemAttachment(
    string InVmPath,
    string FileName,
    string ContentType,
    string Caption,
    long SizeBytes,
    string Sha256);

/// <summary>
/// Projects a work item's stored attachments into <see cref="WorkItemAttachment"/>
/// delivery records (in-VM path + metadata). Consumed by the orchestrator's
/// <c>AttachmentManifestPromptPreprocessor</c> to stage the bytes into the
/// sandbox and inject the attachment manifest into the agent prompt.
/// </summary>
public interface IWorkItemAttachmentSource
{
    Task<IReadOnlyList<WorkItemAttachment>> ListAsync(WorkItemId itemId, CancellationToken ct = default);
}
