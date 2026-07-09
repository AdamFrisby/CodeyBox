namespace CodeyBox.Core;

/// <summary>
/// Planned in-VM attachment location for a future delivery task. The current
/// attachment foundation stores and serves blobs through the REST API only;
/// no production code injects this metadata into agent prompts.
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
/// Returns planned attachment delivery records for a future in-VM staging
/// service. This interface is not wired into the production agent prompt path
/// by the foundation-only attachment implementation.
/// </summary>
public interface IWorkItemAttachmentSource
{
    Task<IReadOnlyList<WorkItemAttachment>> ListAsync(WorkItemId itemId, CancellationToken ct = default);
}
