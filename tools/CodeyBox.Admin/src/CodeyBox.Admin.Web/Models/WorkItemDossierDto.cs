namespace CodeyBox.Admin.Web.Models;

/// <summary>Client shape for GET /workitems/{id}/dossier. REST + JSON coupling only.</summary>
public sealed class WorkItemDossierDto
{
    public string WorkItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string State { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string DossierLink { get; set; } = "";
    public int PromptRevision { get; set; }
    public string OverallStatus { get; set; } = "";
    public DossierChangeDto Change { get; set; } = new();
    public DossierPublicationDto Publication { get; set; } = new();
    public List<DossierGateDto> Gates { get; set; } = [];
    public List<DossierArtifactDto> Artifacts { get; set; } = [];
    public DossierCostDto Costs { get; set; } = new();
    public long DurationMs { get; set; }
}

public sealed class DossierChangeDto
{
    public string? BaseBranch { get; set; }
    public string? WorkBranch { get; set; }
    public string? BaseCommitSha { get; set; }
    public string? WorkCommitSha { get; set; }
    public int FilesChanged { get; set; }
    public long LinesAdded { get; set; }
    public long LinesRemoved { get; set; }
    public List<string> ChangedFiles { get; set; } = [];
    public bool Truncated { get; set; }
    public string Outcome { get; set; } = "";
}

public sealed class DossierPublicationDto
{
    public DossierLocalMergeDto LocalMerge { get; set; } = new();
    public DossierOpenPrDto OpenPr { get; set; } = new();
    public DossierMergedPrDto MergedPr { get; set; } = new();
}

public sealed class DossierLocalMergeDto
{
    public string State { get; set; } = "";
    public string? Sha { get; set; }
}

public sealed class DossierOpenPrDto
{
    public string State { get; set; } = "";
    public int? Number { get; set; }
    public string? Url { get; set; }
}

public sealed class DossierMergedPrDto
{
    public string State { get; set; } = "";
    public int? Number { get; set; }
    public string? Url { get; set; }
    public string? MergeSha { get; set; }
}

public sealed class DossierGateDto
{
    public string Name { get; set; } = "";
    public string Outcome { get; set; } = "";
    public bool IsStale { get; set; }
    public string Summary { get; set; } = "";
}

public sealed class DossierArtifactDto
{
    public string Name { get; set; } = "";
    public string Phase { get; set; } = "";
    public string ProducedBy { get; set; } = "";
    public string Outcome { get; set; } = "";
    public bool IsStale { get; set; }
    public string Summary { get; set; } = "";
}

public sealed class DossierCostDto
{
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public double EstimatedUsd { get; set; }
    public long ElapsedMs { get; set; }
    public int InvocationCount { get; set; }
}
