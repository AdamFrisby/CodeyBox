using System;
using System.Threading;
using System.Threading.Tasks;

namespace CodeyBox.Core;

/// <summary>
/// Import payload sent to JobTrack's test-case import/upsert API for a single
/// CodeyBox <see cref="TestCase"/>. This is the wire contract between CodeyBox
/// and JobTrack: it projects CodeyBox's lean, execution-focused model onto the
/// subset JobTrack accepts, and carries the cross-system provenance keys JobTrack
/// needs to place and de-duplicate the case.
///
/// <para><b>Idempotency.</b> <see cref="ExternalSourceId"/> is the stable
/// per-case key (the CodeyBox <see cref="TestCase.Id"/>); JobTrack upserts on it,
/// so re-exporting the same case updates the existing JobTrack row rather than
/// creating a duplicate. <see cref="SourceTaskId"/> is the owning JobTrack task
/// (the analogue of CodeyBox's <see cref="TestCase.SourceWorkItemId"/>).</para>
///
/// <para><b>Placement.</b> CodeyBox does not model JobTrack's SurfaceArea /
/// hierarchy. <see cref="SurfaceArea"/> carries an optional operator-configured
/// default placement; when null, JobTrack applies its own default. The
/// hierarchy fields (parent / path / level / sort order) are never sent — they
/// are JobTrack-owned and assigned on its side.</para>
/// </summary>
public sealed record JobTrackTestCaseImport
{
    /// <summary>
    /// Stable per-case idempotency key JobTrack upserts on: the CodeyBox
    /// <see cref="TestCase.Id"/>. Re-export with the same value updates in place.
    /// </summary>
    public required string ExternalSourceId { get; init; }

    /// <summary>
    /// The owning JobTrack task id (from the work item's external-id namespace).
    /// Analogue of CodeyBox's <see cref="TestCase.SourceWorkItemId"/>.
    /// </summary>
    public required string SourceTaskId { get; init; }

    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>
    /// JobTrack automation-kind token mapped from CodeyBox's
    /// <see cref="AutomationKind"/> enum by <see cref="JobTrackTestCaseMapper"/>.
    /// Null when the CodeyBox case declares no automation kind (JobTrack defaults).
    /// </summary>
    public string? AutomationKind { get; init; }

    /// <summary>Opaque executable-artifact JSON (e2e-replay steps/selectors/assertions), carried verbatim.</summary>
    public string? ExecutableArtifactJson { get; init; }

    /// <summary>Opaque conformance JSON (the mutation-gate "must fail when broken" rule), carried verbatim.</summary>
    public string? ConformanceJson { get; init; }

    /// <summary>Optional flat capability/area label carried from the CodeyBox case.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// Optional default SurfaceArea placement. Null lets JobTrack apply its own
    /// default; CodeyBox never models the SurfaceArea entity itself.
    /// </summary>
    public string? SurfaceArea { get; init; }

    public bool IsArchived { get; init; }
}

/// <summary>
/// Pure projection of a CodeyBox <see cref="TestCase"/> onto the JobTrack import
/// contract. Kept free of I/O so the mapping is directly unit-testable.
/// </summary>
public static class JobTrackTestCaseMapper
{
    /// <summary>
    /// Maps a CodeyBox <paramref name="testCase"/> to its JobTrack import payload.
    /// </summary>
    /// <param name="testCase">The source case. Not mutated.</param>
    /// <param name="sourceTaskId">
    /// The owning JobTrack task id — must be non-empty (the caller resolves it
    /// from the work item's external-id namespace).
    /// </param>
    /// <param name="defaultSurfaceArea">
    /// Optional operator-configured default SurfaceArea placement. Whitespace or
    /// null maps to a null <see cref="JobTrackTestCaseImport.SurfaceArea"/> so
    /// JobTrack applies its own default.
    /// </param>
    public static JobTrackTestCaseImport ToImport(
        TestCase testCase,
        string sourceTaskId,
        string? defaultSurfaceArea = null)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTaskId);

        return new JobTrackTestCaseImport
        {
            ExternalSourceId = testCase.Id,
            SourceTaskId = sourceTaskId,
            Name = testCase.Name,
            Description = testCase.Description,
            AutomationKind = MapAutomationKind(testCase.AutomationKind),
            ExecutableArtifactJson = testCase.ExecutableArtifactJson,
            ConformanceJson = testCase.ConformanceJson,
            Label = testCase.Label,
            SurfaceArea = string.IsNullOrWhiteSpace(defaultSurfaceArea) ? null : defaultSurfaceArea.Trim(),
            IsArchived = testCase.IsArchived,
        };
    }

    /// <summary>
    /// Maps CodeyBox's <see cref="AutomationKind"/> enum to JobTrack's stable
    /// automation-kind token. Null in → null out (JobTrack defaults).
    /// </summary>
    public static string? MapAutomationKind(AutomationKind? kind) => kind switch
    {
        Core.AutomationKind.Manual => "manual",
        Core.AutomationKind.Unit => "unit",
        Core.AutomationKind.Integration => "integration",
        Core.AutomationKind.E2eReplay => "e2e-replay",
        null => null,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "Unknown automation kind cannot be mapped to a JobTrack token"),
    };
}

/// <summary>
/// A fully-resolved JobTrack import endpoint: the absolute POST target plus the
/// optional bearer token. Produced by <see cref="JobTrackExportEndpointResolver"/>
/// from a project's <see cref="ProjectJobTrackExport"/> config; the token is read
/// from an env var and never persisted.
/// </summary>
public sealed record JobTrackExportEndpoint
{
    /// <summary>Absolute http(s) URI the import payload is POSTed to.</summary>
    public required Uri ImportUri { get; init; }

    /// <summary>Bearer token, or null for an unauthenticated JobTrack deployment.</summary>
    public string? Token { get; init; }
}

/// <summary>
/// Resolves a project's <see cref="ProjectJobTrackExport"/> config into a
/// concrete <see cref="JobTrackExportEndpoint"/>. Pure given the environment
/// reader, so token-resolution and URL-composition are unit-testable without
/// touching the process environment.
/// </summary>
public static class JobTrackExportEndpointResolver
{
    /// <summary>
    /// Attempts to resolve an import endpoint. Returns false with a human-readable
    /// <paramref name="error"/> when the base URL is not an absolute http(s) URL,
    /// or a configured token env var resolves to empty. Callers treat a false
    /// result as a skip (misconfiguration), not a hard failure.
    /// </summary>
    public static bool TryResolve(
        ProjectJobTrackExport config,
        Func<string, string?> environment,
        out JobTrackExportEndpoint? endpoint,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(environment);

        endpoint = null;
        error = null;

        if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            error = $"BaseUrl '{config.BaseUrl}' is not an absolute http(s) URL";
            return false;
        }

        var combined = $"{config.BaseUrl.TrimEnd('/')}/{config.ImportPath.TrimStart('/')}";
        if (!Uri.TryCreate(combined, UriKind.Absolute, out var importUri))
        {
            error = $"Import URL '{combined}' is not a valid absolute URL";
            return false;
        }

        string? token = null;
        if (!string.IsNullOrWhiteSpace(config.TokenEnvVar))
        {
            token = environment(config.TokenEnvVar);
            if (string.IsNullOrWhiteSpace(token))
            {
                error = $"token env var '{config.TokenEnvVar}' is empty";
                return false;
            }
        }

        endpoint = new JobTrackExportEndpoint { ImportUri = importUri, Token = token };
        return true;
    }
}

/// <summary>
/// Outbound client that upserts a single test case into JobTrack's import API.
/// The concrete HTTP implementation lives in the orchestrator; this seam keeps
/// the exporter testable with an in-memory fake.
/// </summary>
public interface IJobTrackTestCaseClient
{
    /// <summary>
    /// Upserts <paramref name="import"/> into JobTrack at <paramref name="endpoint"/>.
    /// Implementations MUST be idempotent from the caller's perspective (JobTrack
    /// keys on <see cref="JobTrackTestCaseImport.ExternalSourceId"/>). Throws on a
    /// transport or non-success HTTP status so the exporter can apply its retry
    /// policy; the exporter, not this client, decides best-effort swallowing.
    /// </summary>
    Task UpsertAsync(JobTrackExportEndpoint endpoint, JobTrackTestCaseImport import, CancellationToken ct = default);
}

/// <summary>
/// Terminal disposition of a per-work-item JobTrack export attempt.
/// </summary>
public enum JobTrackExportStatus
{
    /// <summary>The project has not opted into JobTrack export.</summary>
    Disabled,

    /// <summary>The work item carries no JobTrack task id in the configured namespace.</summary>
    NoJobTrackId,

    /// <summary>The project opted in but its export config could not be resolved (bad URL / empty token env var).</summary>
    Misconfigured,

    /// <summary>The export ran; see <see cref="JobTrackExportSummary.Exported"/> / <see cref="JobTrackExportSummary.Failed"/>.</summary>
    Completed,
}

/// <summary>
/// Result of a best-effort per-work-item export. Never carries an exception:
/// export failure is surfaced as counts/status, never thrown into the pipeline.
/// </summary>
public sealed record JobTrackExportSummary
{
    public required JobTrackExportStatus Status { get; init; }

    /// <summary>Number of cases successfully upserted into JobTrack.</summary>
    public int Exported { get; init; }

    /// <summary>Number of cases that failed to upsert after retries (best-effort; item unaffected).</summary>
    public int Failed { get; init; }

    /// <summary>Optional human-readable detail (skip reason, error summary).</summary>
    public string? Detail { get; init; }

    public static JobTrackExportSummary Skipped(JobTrackExportStatus status, string? detail = null)
        => new() { Status = status, Detail = detail };
}

/// <summary>
/// Exports a work item's CodeyBox test cases to JobTrack. Config-gated and
/// opt-in per project; propagation is best-effort and idempotent, and never
/// fails the owning work item. See <c>docs/quality/test-cases.md</c>.
/// </summary>
public interface IJobTrackTestCaseExporter
{
    /// <summary>
    /// Exports every test case linked to <paramref name="item"/> to JobTrack when
    /// <paramref name="project"/> has opted in and the item carries a JobTrack
    /// task id. Best-effort: returns a summary and never throws (except on
    /// cancellation) so a propagation failure cannot fail the work item.
    /// </summary>
    Task<JobTrackExportSummary> ExportForWorkItemAsync(WorkItem item, Project project, CancellationToken ct = default);
}
