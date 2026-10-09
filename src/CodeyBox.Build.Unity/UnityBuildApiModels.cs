using System.Text.Json.Serialization;

namespace CodeyBox.Build.Unity;

/// <summary>
/// Minimal Unity Build Automation REST shapes used by the adapter. Property
/// names follow the published API (https://build-api.cloud.unity3d.com/docs/);
/// every value is untrusted provider output and is re-validated by
/// <see cref="UnityBuildStatusMapper"/> before it reaches any sink.
/// Unknown fields are ignored on read; nothing here is executed.
/// </summary>
public sealed class UnityCreateBuildRequest
{
    [JsonPropertyName("commit")]
    public string Commit { get; set; } = string.Empty;

    [JsonPropertyName("cleanBuild")]
    public bool CleanBuild { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("sourceRef")]
    public string SourceRef { get; set; } = string.Empty;
}

public sealed class UnityCreateBuildResponse
{
    [JsonPropertyName("build")]
    public long Build { get; set; }

    [JsonPropertyName("buildId")]
    public string BuildId { get; set; } = string.Empty;

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class UnityBuildSummary
{
    [JsonPropertyName("build")]
    public long Build { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("commit")]
    public string? Commit { get; set; }

    [JsonPropertyName("buildStatus")]
    public string? BuildStatus { get; set; }
}

public sealed class UnityTestReport
{
    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("passed")]
    public long Passed { get; set; }

    [JsonPropertyName("failed")]
    public long Failed { get; set; }

    [JsonPropertyName("skipped")]
    public long Skipped { get; set; }
}

public sealed class UnityBuildArtifactEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }
}

public sealed class UnityBuildDetails
{
    [JsonPropertyName("build")]
    public long Build { get; set; }

    [JsonPropertyName("buildStatus")]
    public string BuildStatus { get; set; } = string.Empty;

    [JsonPropertyName("failurePhase")]
    public string? FailurePhase { get; set; }

    [JsonPropertyName("failureReason")]
    public string? FailureReason { get; set; }

    [JsonPropertyName("checkoutCommit")]
    public string? CheckoutCommit { get; set; }

    [JsonPropertyName("checkoutMode")]
    public string? CheckoutMode { get; set; }

    [JsonPropertyName("editorVersion")]
    public string? EditorVersion { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("logExcerpt")]
    public string? LogExcerpt { get; set; }

    [JsonPropertyName("testReport")]
    public UnityTestReport? TestReport { get; set; }

    [JsonPropertyName("artifacts")]
    public List<UnityBuildArtifactEntry> Artifacts { get; set; } = [];

    [JsonPropertyName("cancelState")]
    public string? CancelState { get; set; }

    [JsonPropertyName("queuedSeconds")]
    public long? QueuedSeconds { get; set; }
}
