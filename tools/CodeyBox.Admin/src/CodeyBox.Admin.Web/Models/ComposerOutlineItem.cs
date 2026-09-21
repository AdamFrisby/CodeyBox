namespace CodeyBox.Admin.Web.Models;

/// <summary>
/// One row of the composer's chain outline: the parsed title and body,
/// the in-chain edges as the operator last confirmed them, and whether
/// the operator has edited either text — edited text survives a re-parse
/// of the editor while the item count is unchanged; parsed text does not.
/// </summary>
public sealed class ComposerOutlineItem
{
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public List<int> Edges { get; set; } = [];
    public string EdgesText { get; set; } = "";
    public string? EdgeProblem { get; set; }
    public bool TitleEdited { get; set; }
    public bool BodyEdited { get; set; }
    public bool Expanded { get; set; }
}
