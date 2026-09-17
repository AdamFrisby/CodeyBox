namespace CodeyBox.Admin.Web.Models;

/// <summary>
/// The outcome of filing a composer chain: the created items in submission
/// order, plus — when a create failed midway — where it stopped and why.
/// A partial chain is reported honestly, never as success: earlier items
/// exist on the server and later ones were never sent.
/// </summary>
public sealed class ChainCreateResult
{
    public List<WorkItemDto> Created { get; set; } = [];
    public int? FailedIndex { get; set; }
    public string? Error { get; set; }

    public bool Succeeded => FailedIndex is null && Error is null;
}
