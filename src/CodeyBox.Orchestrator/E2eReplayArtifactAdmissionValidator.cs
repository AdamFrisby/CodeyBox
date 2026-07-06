using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeyBox.Core;
using Microsoft.Extensions.Options;

namespace CodeyBox.Orchestrator;

public sealed class E2eReplayArtifactAdmissionValidator
{
    private static readonly JsonSerializerOptions ArtifactJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly IOptionsMonitor<E2eExecutionOptions>? _options;

    public E2eReplayArtifactAdmissionValidator(IOptionsMonitor<E2eExecutionOptions>? options = null)
    {
        _options = options;
    }

    public bool TryValidateJson(
        string? artifactJson,
        out E2eReplayArtifact? artifact,
        out string failureKind,
        out string detail)
    {
        artifact = null;
        if (string.IsNullOrWhiteSpace(artifactJson))
        {
            failureKind = "MissingArtifact";
            detail = "test case has no executable artifact";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(artifactJson) > E2eReplayArtifactValidation.MaxArtifactJsonBytes)
        {
            failureKind = "ArtifactTooLarge";
            detail = $"artifact JSON exceeds {E2eReplayArtifactValidation.MaxArtifactJsonBytes} bytes";
            return false;
        }

        try
        {
            artifact = JsonSerializer.Deserialize<E2eReplayArtifact>(artifactJson, ArtifactJson);
        }
        catch (JsonException ex)
        {
            failureKind = "ArtifactParseError";
            detail = ex.Message;
            return false;
        }

        if (artifact is null)
        {
            failureKind = "ArtifactParseError";
            detail = "artifact JSON deserialized to null";
            return false;
        }

        if (!E2eReplayArtifactValidation.TryValidate(artifact, out failureKind, out detail))
            return false;

        var allowedOrigins = CurrentAllowedOrigins();
        if (artifact.Readiness is { Url.Length: > 0 } readiness
            && !E2eReplayOriginPolicy.TryValidateReadinessUrl(readiness.Url, allowedOrigins, out _, out detail))
        {
            failureKind = "ReadinessUrlRejected";
            return false;
        }

        if (!E2eReplayOriginPolicy.TryValidateReplayNavigationTargets(artifact, allowedOrigins, out detail))
        {
            failureKind = "NavigationUrlRejected";
            return false;
        }

        failureKind = string.Empty;
        detail = string.Empty;
        return true;
    }

    private IReadOnlyList<string> CurrentAllowedOrigins() =>
        _options?.CurrentValue.AllowedReadinessOrigins
        ?? new E2eExecutionOptions().AllowedReadinessOrigins;
}
