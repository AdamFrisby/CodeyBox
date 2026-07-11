using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Adapts <see cref="IWorkItemAttachmentStore"/> to
/// <see cref="IWorkItemAttachmentSource"/>: maps stored metadata rows to
/// in-VM delivery records, assigning each a collision-free delivery filename
/// under <see cref="SandboxStagingDirectory"/>.
/// </summary>
/// <remarks>
/// This is the single source of truth for the duplicate-name policy so the
/// staged on-disk name, the manifest's bold filename, and the manifest's path
/// can never disagree.
/// </remarks>
public sealed class StoreWorkItemAttachmentSource : IWorkItemAttachmentSource
{
    /// <summary>
    /// Per-item staging directory inside the sandbox. The
    /// <c>AttachmentManifestPromptPreprocessor</c> writes each blob under this
    /// path keyed by the safe delivery filename before announcing it to the agent.
    /// </summary>
    public const string SandboxStagingDirectory = "/work/.codeybox/attachments";

    private readonly IWorkItemAttachmentStore _store;

    public StoreWorkItemAttachmentSource(IWorkItemAttachmentStore store)
    {
        _store = store;
    }

    public async Task<IReadOnlyList<WorkItemAttachment>> ListAsync(WorkItemId itemId, CancellationToken ct = default)
    {
        var rows = await _store.ListForWorkItemAsync(itemId, ct).ConfigureAwait(false);
        if (rows.Count == 0)
            return Array.Empty<WorkItemAttachment>();

        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<WorkItemAttachment>(rows.Count);
        foreach (var row in rows)
        {
            var fileName = row.FileName;
            var deliveryName = fileName;
            if (!seenNames.Add(deliveryName))
            {
                deliveryName = $"{row.Id}-{fileName}";
                seenNames.Add(deliveryName);
                // Reflect the disambiguated name in both the FileName field
                // and the InVmPath basename so the manifest does not show a
                // bold FileName that disagrees with the trailing code-span
                // path (which would tempt the agent to open the bare name).
                fileName = deliveryName;
            }
            result.Add(new WorkItemAttachment(
                InVmPath: $"{SandboxStagingDirectory}/{deliveryName}",
                FileName: fileName,
                ContentType: row.ContentType,
                Caption: row.Caption,
                SizeBytes: row.SizeBytes,
                Sha256: row.Sha256));
        }
        return result;
    }
}
