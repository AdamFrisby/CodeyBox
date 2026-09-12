using CodeyBox.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api;

/// <summary>
/// Read-only soundness report over the accumulated <c>csharp:test-pass</c>
/// shadow telemetry: over the last N audits, (1) the unsafe-skip count —
/// times the selector would have deselected a test that actually FAILED —
/// and (2) the wall-clock/test-count the WOULD-BE subset would have saved.
//  Selector-agnostic: the optional <c>selector</c> filter is an exact,
//  case-insensitive match, and the per-selector breakdown always covers every
/// selector present in the window.
/// </summary>
internal static class TestSelectionSoundnessEndpoints
{
    private const int DefaultLimit = 100;

    public static void Map(WebApplication app)
    {
        app.MapGet("/audit/test-selection/soundness", GetSoundnessAsync);
    }

    private static async Task<IResult> GetSoundnessAsync(
        [FromQuery] int? limit,
        [FromQuery] string? selector,
        [FromServices] IAuditReportStore reportStore,
        [FromServices] IOptionsMonitor<TestSelectionSoundnessOptions> soundnessOptions,
        CancellationToken ct)
    {
        var options = soundnessOptions.CurrentValue;
        var maxLimit = options.MaxLimit > 0 ? options.MaxLimit : DefaultLimit;
        var windowSize = Math.Clamp(limit ?? DefaultLimit, 1, maxLimit);

        IReadOnlyList<AuditReport> rows;
        try
        {
            rows = await reportStore.GetRecentTestSelectionAsync(windowSize, ct).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid limit",
                detail: ex.Message);
        }

        var samples = new List<TestSelectionSoundnessSample>(rows.Count);
        foreach (var row in rows)
        {
            if (row.TestSelection is null)
                continue;
            samples.Add(TestSelectionSoundnessSample.FromReport(row));
        }

        var report = TestSelectionSoundnessComputer.Build(
            samples,
            windowSize,
            selector,
            options.CalibrationWindowSize > 0 ? options.CalibrationWindowSize : 100);

        return Results.Ok(new
        {
            windowSize = report.WindowSize,
            evaluatedCount = report.EvaluatedCount,
            selectorFilter = report.SelectorFilter,
            unsafeSkipCount = report.UnsafeSkipCount,
            safeCount = report.SafeCount,
            fullSuiteCount = report.FullSuiteCount,
            unverifiableCount = report.UnverifiableCount,
            totalTestsSaved = report.TotalTestsSaved,
            estimatedSavedMs = report.EstimatedSavedMs,
            bySelector = report.BySelector.Select(s => new
            {
                selector = s.Selector,
                runs = s.Runs,
                unsafeSkipCount = s.UnsafeSkipCount,
                safeCount = s.SafeCount,
                testsSaved = s.TestsSaved,
                estimatedSavedMs = s.EstimatedSavedMs,
            }),
            gate = new
            {
                maxAllowedUnsafeSkips = report.Gate.MaxAllowedUnsafeSkips,
                calibrationWindowSize = report.Gate.CalibrationWindowSize,
                assessableCount = report.Gate.AssessableCount,
                readyForEnforcement = report.Gate.ReadyForEnforcement,
                reason = report.Gate.Reason,
            },
        });
    }
}
