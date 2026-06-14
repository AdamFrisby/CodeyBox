using CodeyBox.Core;

namespace CodeyBox.StatisticsPlugin;

/// <summary>
/// Subscription capacity analyser. Joins the captured quota-snapshot
/// time-series (per-agent, per-window % remaining) against actual token
/// consumption recorded in <c>agent_usage_events</c> to estimate how many
/// tokens / requests a full subscription window holds.
///
/// <para><b>Algorithm.</b> For every (agent, window[, model]) tuple seen in
/// the time-series:</para>
/// <list type="number">
///   <item>Sort quota samples by <c>sampled_at</c> ascending.</item>
///   <item>For each pair of consecutive samples <c>s_i, s_{i+1}</c>:</item>
///   <item>If <c>WindowPct</c> drops by at least <see cref="CapacityFilter.MinDeltaPct"/>,
///     query the usage store for events with <c>time_utc</c> in
///     <c>[s_i.SampledAt, s_{i+1}.SampledAt)</c> and record a
///     <see cref="CapacityInterval"/>.</item>
///   <item>If <c>WindowPct</c> went UP between samples, treat the boundary as
///     a window-reset event: mark the interval <c>IsWindowReset</c> and do
///     not count its tokens toward the burn-rate average (the percent jump
///     is a calendar reset, not a real consumption signal).</item>
///   <item>The burn rate is the weighted mean of <c>tokens / deltaPct</c>
///     across counted intervals, weighted by <c>deltaPct</c>. Equivalently,
///     it is <c>sum(tokens) / sum(deltaPct)</c> across counted intervals.
///     That formula is robust to occasional tiny intervals with absurd
///     ratios (e.g. one big request at near-zero percent change).</item>
/// </list>
///
/// <para><b>Confidence.</b> The estimate stabilises with more samples; the
/// dashboard buckets confidence by interval count (Low &lt; 3, Medium 3-9,
/// High 10+). When zero intervals survive filtering the entry still
/// reports <see cref="CurrentPct"/> + <see cref="ResetAt"/> but every
/// estimate column is null with <see cref="CapacityConfidence.None"/>.</para>
///
/// <para><b>Caveats surfaced as <see cref="CapacityEntry.Notes"/>:</b>
/// cached input tokens are billed at a different rate than fresh input, so
/// they are reported separately; rolling provider windows (Codex 5h-rolling)
/// never "reset" in the percent sense — the burn-rate is amortised across
/// the rolling horizon.</para>
/// </summary>
public sealed class CapacityCalculator : ICapacityCalculator
{
    private const int MaxIntervalsReturned = 5000;
    private const int MaxHorizonHours = 24 * 60; // 60 days — beyond this, query plan loses value.

    private readonly IQuotaTimeSeriesStore _timeSeries;
    private readonly IAgentUsageStore _usage;
    private readonly TimeProvider _clock;

    public CapacityCalculator(
        IQuotaTimeSeriesStore timeSeries,
        IAgentUsageStore usage,
        TimeProvider? clock = null)
    {
        _timeSeries = timeSeries ?? throw new ArgumentNullException(nameof(timeSeries));
        _usage = usage ?? throw new ArgumentNullException(nameof(usage));
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task<CapacityReport> ComputeAsync(CapacityFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var now = _clock.GetUtcNow();
        var toUtc = filter.ToUtc?.ToUniversalTime() ?? now;
        var fromUtc = filter.FromUtc?.ToUniversalTime()
            ?? toUtc - TimeSpan.FromHours(CapacityFilter.DefaultHorizonHours);

        if (toUtc - fromUtc > TimeSpan.FromHours(MaxHorizonHours))
            fromUtc = toUtc - TimeSpan.FromHours(MaxHorizonHours);

        if (fromUtc >= toUtc)
            return new CapacityReport(now, fromUtc, toUtc, Array.Empty<CapacityEntry>());

        // Pull every sample in the requested window. The time-series store
        // already supports per-agent / per-window / per-model filters — we
        // forward the same filter so the bulk of slicing is server-side.
        // WindowName is intentionally NOT forwarded as a hard filter here:
        // the operator usually wants every window for the chosen agent
        // (e.g. claude five_hour + seven_day side-by-side). We filter to the
        // requested window after grouping if the operator specified one.
        // ToUtc gets a 1-second slop because the underlying store uses
        // `sampled_at < $to` — without slop a sample written at exactly the
        // default `now` would be silently excluded from the first compute
        // call after that sampler tick lands.
        var tsFilter = new QuotaTimeSeriesFilter
        {
            Agent = filter.Agent,
            ModelId = filter.ModelId,
            FromUtc = fromUtc,
            ToUtc = toUtc + TimeSpan.FromSeconds(1),
            Limit = 50_000,
        };
        var rows = await _timeSeries.QueryAsync(tsFilter, ct);

        // Group into (agent, window, model) buckets. The time-series store
        // emits one row per probe call × per window × per model — so a
        // single ProbeCall fans out into:
        //   - overall row (window_name NULL, model_id NULL)
        //   - per-window rows (window_name = name, model_id NULL)
        //   - per-model overall row (window_name NULL, model_id = id)
        //   - per-model per-window row (window_name = name, model_id = id)
        // The capacity calculator is interested in the per-window rows
        // (with the model filter the caller supplied); overall rows are
        // surfaced under the synthetic window name "overall".
        var grouped = rows
            .Where(r => r.IsKnown && (r.WindowName is null || r.WindowPct.HasValue))
            .GroupBy(r => (
                Agent: r.Agent,
                Window: r.WindowName ?? "overall",
                ModelId: filter.ModelId is null ? null : r.ModelId))
            .Where(g => filter.WindowName is null
                || string.Equals(filter.WindowName, g.Key.Window, StringComparison.OrdinalIgnoreCase));

        var entries = new List<CapacityEntry>();
        foreach (var group in grouped)
        {
            ct.ThrowIfCancellationRequested();
            var (agent, window, modelId) = group.Key;

            // When the caller specified a model filter we narrow to model rows;
            // otherwise we operate on rows with model_id IS NULL (cross-model
            // probe reading). The model-id filter on the SQL side already
            // narrows the recordset, but we still need to pick the correct
            // grouping key on the C# side because we are also grouping by
            // model id.
            var samples = group
                .Where(r => filter.ModelId is null ? r.ModelId is null : r.ModelId is not null)
                .OrderBy(r => r.SampledAt)
                .ToList();

            if (samples.Count < 2)
            {
                // Single sample is not enough to difference. Still emit the
                // entry so the dashboard can show current pct + reset hint.
                var solo = samples.Count == 1 ? samples[0] : null;
                entries.Add(BuildEmptyEntry(agent, window, modelId, solo));
                continue;
            }

            var entry = await ComputeEntryAsync(agent, window, modelId, samples, filter, now, ct);
            entries.Add(entry);
        }

        // Sort: by agent then window so the JSON / table rendering is stable.
        entries.Sort((a, b) =>
        {
            var byAgent = string.CompareOrdinal(a.Agent, b.Agent);
            if (byAgent != 0) return byAgent;
            return string.CompareOrdinal(a.WindowName, b.WindowName);
        });

        return new CapacityReport(now, fromUtc, toUtc, entries);
    }

    private async Task<CapacityEntry> ComputeEntryAsync(
        string agent,
        string window,
        string? modelId,
        IReadOnlyList<QuotaSampleRow> samples,
        CapacityFilter filter,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var intervals = new List<CapacityInterval>(capacity: samples.Count - 1);
        long sumInput = 0, sumCached = 0, sumOutput = 0, sumRequests = 0, sumCost = 0;
        double sumDelta = 0;
        int countedIntervals = 0;

        // We bucket consecutive samples. For each pair compute deltaPct =
        // prevPct - nextPct. Positive means consumption; negative means the
        // window reset (e.g. seven_day rolled over) — those boundaries are
        // recorded for visibility but their tokens are NOT folded into the
        // burn-rate average. Tokens spent in the same interval as a reset
        // are nonetheless attributed to that interval (so dashboards can
        // show a spike); the IsWindowReset flag tells the caller to skip
        // them when computing burn rates.
        for (int i = 0; i < samples.Count - 1; i++)
        {
            ct.ThrowIfCancellationRequested();

            var prev = samples[i];
            var next = samples[i + 1];
            var prevPct = ResolvePct(prev, window);
            var nextPct = ResolvePct(next, window);
            if (prevPct is null || nextPct is null) continue;

            var delta = prevPct.Value - nextPct.Value;
            var isReset = delta < 0;

            var tokens = await _usage.SumTokensWindowAsync(
                agent, modelId, prev.SampledAt, next.SampledAt, ct);

            if (filter.IncludeIntervals && intervals.Count < MaxIntervalsReturned)
            {
                intervals.Add(new CapacityInterval(
                    FromUtc: prev.SampledAt,
                    ToUtc: next.SampledAt,
                    DeltaPct: delta,
                    InputTokens: tokens.InputTokens,
                    CachedInputTokens: tokens.CachedInputTokens,
                    OutputTokens: tokens.OutputTokens,
                    Requests: tokens.Count,
                    CostMicroCents: tokens.SumMicroCents,
                    IsWindowReset: isReset));
            }

            if (isReset || delta < filter.MinDeltaPct)
                continue;

            sumDelta += delta;
            sumInput += tokens.InputTokens;
            sumCached += tokens.CachedInputTokens;
            sumOutput += tokens.OutputTokens;
            sumRequests += tokens.Count;
            sumCost += tokens.SumMicroCents;
            countedIntervals++;
        }

        // Most recent sample drives the current pct + reset + projection.
        var latest = samples[^1];
        var currentPct = ResolvePct(latest, window);
        var resetAt = latest.WindowResetAt;

        double? inputPerPct = null, cachedPerPct = null, outputPerPct = null, requestsPerPct = null;
        double? estFullInput = null, estFullCached = null, estFullOutput = null, estFullRequests = null;
        if (countedIntervals > 0 && sumDelta > 0)
        {
            inputPerPct = sumInput / sumDelta;
            cachedPerPct = sumCached / sumDelta;
            outputPerPct = sumOutput / sumDelta;
            requestsPerPct = sumRequests / sumDelta;
            estFullInput = inputPerPct * 100.0;
            estFullCached = cachedPerPct * 100.0;
            estFullOutput = outputPerPct * 100.0;
            estFullRequests = requestsPerPct * 100.0;
        }

        // Recent burn rate for projection. We use the median over the LAST
        // up-to-3 counted intervals so a single spike doesn't dominate the
        // estimated-exhaustion time. Tokens are billable input — the bucket
        // most directly billed against the subscription's 5h / weekly limit.
        DateTimeOffset? exhaustionAt = null;
        if (currentPct is > 0 && intervals.Count > 0)
        {
            var recent = intervals
                .Where(iv => !iv.IsWindowReset && iv.DeltaPct >= filter.MinDeltaPct)
                .TakeLast(3)
                .ToList();
            if (recent.Count > 0)
            {
                var recentDelta = recent.Sum(iv => iv.DeltaPct);
                var recentSpan = (recent[^1].ToUtc - recent[0].FromUtc).TotalSeconds;
                if (recentDelta > 0 && recentSpan > 0)
                {
                    var pctPerSecond = recentDelta / recentSpan;
                    if (pctPerSecond > 0)
                    {
                        var secondsToZero = currentPct.Value / pctPerSecond;
                        if (double.IsFinite(secondsToZero) && secondsToZero > 0 && secondsToZero < TimeSpan.FromDays(60).TotalSeconds)
                            exhaustionAt = now.AddSeconds(secondsToZero);
                    }
                }
            }
        }

        var confidence = countedIntervals switch
        {
            0 => CapacityConfidence.None,
            >= 1 and <= 2 => CapacityConfidence.Low,
            >= 3 and <= 9 => CapacityConfidence.Medium,
            _ => CapacityConfidence.High,
        };

        var notes = BuildNotes(window, countedIntervals, sumCached);

        return new CapacityEntry
        {
            Agent = agent,
            WindowName = window,
            ModelId = modelId,
            SampleIntervals = countedIntervals,
            TotalDeltaPct = sumDelta,
            TotalInputTokens = sumInput,
            TotalCachedInputTokens = sumCached,
            TotalOutputTokens = sumOutput,
            TotalRequests = sumRequests,
            TotalCostMicroCents = sumCost,
            InputTokensPerPercent = inputPerPct,
            CachedInputTokensPerPercent = cachedPerPct,
            OutputTokensPerPercent = outputPerPct,
            RequestsPerPercent = requestsPerPct,
            EstimatedFullWindowInputTokens = estFullInput,
            EstimatedFullWindowCachedInputTokens = estFullCached,
            EstimatedFullWindowOutputTokens = estFullOutput,
            EstimatedFullWindowRequests = estFullRequests,
            CurrentPct = currentPct,
            ResetAt = resetAt,
            EstimatedExhaustionAt = exhaustionAt,
            Confidence = confidence,
            Notes = notes,
            Intervals = filter.IncludeIntervals ? intervals : Array.Empty<CapacityInterval>(),
        };
    }

    private static CapacityEntry BuildEmptyEntry(
        string agent,
        string window,
        string? modelId,
        QuotaSampleRow? solo)
    {
        return new CapacityEntry
        {
            Agent = agent,
            WindowName = window,
            ModelId = modelId,
            SampleIntervals = 0,
            TotalDeltaPct = 0,
            TotalInputTokens = 0,
            TotalCachedInputTokens = 0,
            TotalOutputTokens = 0,
            TotalRequests = 0,
            TotalCostMicroCents = 0,
            CurrentPct = solo is not null ? ResolvePct(solo, window) : null,
            ResetAt = solo?.WindowResetAt,
            Confidence = CapacityConfidence.None,
            Notes = new[]
            {
                "Insufficient samples for a capacity estimate. Need ≥2 consecutive quota snapshots — wait for more sampler ticks.",
            },
        };
    }

    private static IReadOnlyList<string> BuildNotes(string window, int countedIntervals, long sumCached)
    {
        var notes = new List<string>();
        if (countedIntervals == 0)
        {
            notes.Add("No intervals survived noise-floor filtering. Either no usage was recorded or every interval's pct-delta was below the threshold.");
            return notes;
        }

        if (countedIntervals < 3)
            notes.Add("Estimate is preliminary — confidence improves with more sampler ticks across the window's lifecycle.");

        if (window.Contains("rolling", StringComparison.OrdinalIgnoreCase))
            notes.Add("Rolling window: percent does not reset — the burn-rate represents amortised consumption across the rolling horizon.");

        if (sumCached > 0)
            notes.Add("Cached input tokens are billed at a different rate than fresh input — both buckets are reported separately so totals stay meaningful.");

        return notes;
    }

    private static double? ResolvePct(QuotaSampleRow row, string window)
    {
        if (string.Equals(window, "overall", StringComparison.OrdinalIgnoreCase))
            return row.WindowName is null ? row.OverallPct : (double?)null;
        return row.WindowName is null ? null : row.WindowPct;
    }
}
