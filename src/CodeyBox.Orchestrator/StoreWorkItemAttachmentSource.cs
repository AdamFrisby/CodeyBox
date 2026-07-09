using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Adapts <see cref="IWorkItemAttachmentStore"/> to
/// <see cref="IWorkItemAttachmentSource"/> for the future in-VM attachment
/// delivery task.
/// </summary>
/// <remarks>
/// The current attachment foundation does not wire this adapter into the
/// production agent prompt path. It only centralizes planned delivery names so
/// a future staging service can share the same duplicate-name policy.
/// </remarks>
public sealed class StoreWorkItemAttachmentSource : IWorkItemAttachmentSource
{
    /// <summary>
    /// Per-item staging directory inside the sandbox. The eventual delivery
    /// step (out of scope here) writes blobs under this path keyed by the safe
    /// delivery filename.
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
                Caption: row.Caption));
        }
        return result;
    }
}
