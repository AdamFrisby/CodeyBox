using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

internal static class WorkItemTimingsEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/workitems");
        group.MapGet("/{id}/timings", GetTimingsAsync);
        group.MapGet("/timings/aggregate", GetAggregateTimingsAsync);
    }

    private static async Task<IResult> GetTimingsAsync(
        string id,
        ITimingStore timings,
        IWorkItemStore store,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var g)) return Results.BadRequest(new { error = "invalid id" });
        var workItemId = new WorkItemId(g);

        var item = await store.GetAsync(workItemId, ct);
        if (item is null) return Results.NotFound();

        var records = await timings.GetByWorkItemAsync(workItemId, ct);

        var totalMs = records
            .Where(r => r.DurationMs.HasValue && !IsSubStep(r))
            .Sum(r => r.DurationMs!.Value);

        var byPhase = records
            .GroupBy(r => r.Phase)
            .ToDictionary(
                g2 => g2.Key,
                g2 => BuildPhaseDto(g2.ToList()));

        var topSteps = records
            .Where(r => r.DurationMs.HasValue && !IsSubStep(r))
            .GroupBy(r => r.Step)
            .Select(g2 => new
            {
                step = g2.Key,
                totalMs = g2.Sum(r => r.DurationMs!.Value),
                count = g2.Count(),
            })
            .OrderByDescending(x => x.totalMs)
            .Take(10)
            .ToList();

        return Results.Ok(new
        {
            workItemId = g.ToString("D"),
            totalDurationMs = totalMs,
            byPhase,
            topSteps,
        });
    }

    private static object BuildPhaseDto(List<TimingRecord> phaseRecords)
    {
        var phaseDuration = phaseRecords
            .Where(r => r.DurationMs.HasValue && !IsSubStep(r))
            .Sum(r => r.DurationMs!.Value);

        var hasIterations = phaseRecords.Any(r => r.Iteration.HasValue);

        if (hasIterations)
        {
            var iterations = phaseRecords
                .GroupBy(r => r.Iteration)
                .OrderBy(g2 => g2.Key)
                .Select(g2 => new
                {
                    iteration = g2.Key,
                    durationMs = g2.Where(r => r.DurationMs.HasValue && !IsSubStep(r)).Sum(r => r.DurationMs!.Value),
                    steps = BuildStepList(g2.ToList()),
                })
                .ToList();

            return new { durationMs = phaseDuration, iterations };
        }

        return new { durationMs = phaseDuration, steps = BuildStepList(phaseRecords) };
    }

    private static List<object> BuildStepList(List<TimingRecord> records)
    {
        return records
            .OrderBy(r => r.StartedAt)
            .Select(r => (object)new
            {
                step = r.Step,
                startedAt = r.StartedAt,
                endedAt = r.EndedAt,
                durationMs = r.DurationMs,
                metadata = ParseMetadata(r.MetadataJson),
            })
            .ToList();
    }

    private static object ParseMetadata(string json)
    {
        if (string.IsNullOrEmpty(json) || json == "{}") return new { };
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch (JsonException) { return new { }; }
    }

    private static async Task<IResult> GetAggregateTimingsAsync(
        ITimingStore timings,
        int? n,
        CancellationToken ct)
    {
        var limit = Math.Clamp(n ?? 50, 1, 500);

        // Stream completed records and group in memory, bounded by the last N work items.
        // Sub-steps are excluded to match the per-item endpoint's behaviour and avoid
        // double-counting (e.g. agent.thinking_aggregate vs agent.exec).
        var byStep = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var workItemIds = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var record in timings.StreamCompletedAsync(limit, ct))
        {
            if (!record.DurationMs.HasValue) continue;
            if (IsSubStep(record)) continue;
            workItemIds.Add(record.WorkItemId.ToString());

            var key = $"{record.Phase}/{record.Step}";
            if (!byStep.TryGetValue(key, out var list))
            {
                list = [];
                byStep[key] = list;
            }
            list.Add(record.DurationMs.Value);
        }

        var stepStats = byStep
            .Select(kv =>
            {
                var parts = kv.Key.Split('/', 2);
                var sorted = kv.Value.Order().ToArray();
                return new
                {
                    phase = parts[0],
                    step = parts.Length > 1 ? parts[1] : parts[0],
                    count = sorted.Length,
                    medianMs = Percentile(sorted, 0.5),
                    p95Ms = Percentile(sorted, 0.95),
                };
            })
            .OrderByDescending(x => x.medianMs)
            .ToList();

        return Results.Ok(new
        {
            workItemCount = workItemIds.Count,
            stepStats,
        });
    }

    /// <summary>
    /// Sub-steps whose durations are wholly contained within a parent row.
    /// Excluding them from phase and total sums prevents double-counting.
    /// </summary>
    private static bool IsSubStep(TimingRecord r) =>
        r.Step == "vm.exec_first"
        || r.Step == "bwrap.exec_first"
        || r.Step == "agent.thinking_aggregate"
        || r.Step == "upstream.push_branch"
        || r.Step == "upstream.api_create_pr"
        || r.Step == "upstream.api_merge_pr"
        || r.Step.StartsWith("agent.tool_call.", StringComparison.Ordinal)
        || IsLanguageAuditSubStep(r.Step)
        || r.Step.StartsWith("gitleaks.", StringComparison.Ordinal)
        || r.Step.StartsWith("semgrep.", StringComparison.Ordinal);

    private static bool IsLanguageAuditSubStep(string step)
    {
        var dot = step.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0)
            return false;

        var language = step[..dot];
        var subStep = step[(dot + 1)..];
        return language is "csharp" or "python" or "node" or "javascript" or "typescript" or "go" or "rust" or "ruby" or "shell"
            && subStep is "build" or "format" or "test_discovery" or "test_run";
    }

    private static long Percentile(long[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var idx = (int)Math.Floor(p * (sorted.Length - 1));
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }
}
