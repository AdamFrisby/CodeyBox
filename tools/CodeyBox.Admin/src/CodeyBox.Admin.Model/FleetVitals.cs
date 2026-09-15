namespace CodeyBox.Admin.Model;

/// <summary>
/// Vital band. A vital only leaves <see cref="Neutral"/> for a reason a
/// person could act on; bands are judged against this fleet's own recent
/// history, never an absolute target.
/// </summary>
public enum VitalBand
{
    Neutral,
    Good,
    Bad,
}

/// <summary>One fleet number with the history that makes it readable in context.</summary>
public sealed record FleetVital
{
    public required string Name { get; init; }

    public required double Value { get; init; }

    public required string Unit { get; init; }

    /// <summary>History echoed back (capped, oldest first) for the screen to chart.</summary>
    public IReadOnlyList<double> History { get; init; } = [];

    public required VitalBand Band { get; init; }

    /// <summary>Why the band was assigned; always set when not Neutral.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Derives the fleet vitals from a snapshot. Current values come from the
/// snapshot's items; bands come from the caller-supplied history series.
/// Throughput never reports <see cref="VitalBand.Bad"/> by construction: a
/// quiet period is quiet, not broken, and colouring it red teaches the
/// operator to ignore red.
/// </summary>
public static class VitalsEvaluator
{
    /// <summary>Evaluates all vitals. Pure over snapshot and options.</summary>
    public static IReadOnlyList<FleetVital> Evaluate(
        FleetSnapshot snapshot,
        AdminModelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= new AdminModelOptions();

        var items = BoundedItems(snapshot);
        var now = snapshot.Now;

        var queued = 0;
        var inFlight = 0;
        var parked = 0;
        var windowTerminal = 0;
        var windowFailed = 0;
        var hourlyCompletions = 0;
        var windowStart = now - options.FailureWindow;
        var hourStart = now - options.ThroughputWindow;

        foreach (var item in items)
        {
            var state = item.State ?? string.Empty;
            if (ItemStates.IsQueued(state))
            {
                queued++;
            }
            else if (ItemStates.Parked.Contains(state))
            {
                parked++;
            }
            else if (!ItemStates.IsTerminal(state))
            {
                inFlight++;
            }
            if (ItemStates.IsTerminal(state) && item.UpdatedAt >= windowStart)
            {
                windowTerminal++;
                if (ItemStates.IsFailedTerminal(state))
                {
                    windowFailed++;
                }
            }
            if (ItemStates.IsTerminal(state) && item.UpdatedAt >= hourStart)
            {
                hourlyCompletions++;
            }
        }

        var throughputPerHour = options.ThroughputWindow.TotalHours > 0
            ? hourlyCompletions / options.ThroughputWindow.TotalHours
            : hourlyCompletions;
        var failureRate = windowTerminal > 0 ? (double)windowFailed / windowTerminal : 0.0;

        return
        [
            QueueDepthVital(queued, snapshot.History?.QueueDepth, options),
            InFlightVital(inFlight, queued, snapshot.History?.InFlight, options),
            FailureRateVital(failureRate, windowTerminal, snapshot.History?.FailureRate, options),
            ThroughputVital(throughputPerHour, snapshot.History?.ThroughputPerHour, options),
            ParkedVital(parked, snapshot.History?.Parked, options),
        ];
    }

    private static FleetVital QueueDepthVital(
        int queued, IReadOnlyList<double>? history, AdminModelOptions options)
    {
        var capped = CapHistory(history, options);
        if (capped.Count < options.MinimumHistorySamples)
        {
            return Vital("Queue depth", queued, "items", capped, VitalBand.Neutral,
                "Not enough history to judge this fleet's queue yet.");
        }
        var median = Median(capped);
        if (median > 0 && queued >= median * options.QueueDepthBadMultiple && queued > median)
        {
            return Vital("Queue depth", queued, "items", capped, VitalBand.Bad,
                $"Queued items ({queued}) are {queued / median:0.#}× this fleet's usual {median:0.#} — intake or dispatch needs attention.");
        }
        if (median > 0 && queued <= median * options.QueueDepthGoodFraction)
        {
            return Vital("Queue depth", queued, "items", capped, VitalBand.Good,
                $"Queue is drained against this fleet's usual {median:0.#}.");
        }
        return Vital("Queue depth", queued, "items", capped, VitalBand.Neutral,
            "Queue depth is within this fleet's usual range.");
    }

    private static FleetVital InFlightVital(
        int inFlight, int queued, IReadOnlyList<double>? history, AdminModelOptions options)
    {
        var capped = CapHistory(history, options);
        if (capped.Count < options.MinimumHistorySamples)
        {
            return Vital("In flight", inFlight, "items", capped, VitalBand.Neutral,
                "Not enough history to judge this fleet's concurrency yet.");
        }
        var median = Median(capped);
        if (queued > 0 && inFlight == 0 && median > 0)
        {
            return Vital("In flight", inFlight, "items", capped, VitalBand.Bad,
                $"Nothing is running while {queued} {(queued == 1 ? "item waits" : "items wait")} — the fleet is stalled.");
        }
        if (median > 0 && inFlight > 0 && inFlight < median / options.InFlightStallMultiple && queued > 0)
        {
            return Vital("In flight", inFlight, "items", capped, VitalBand.Bad,
                $"Only {inFlight} running against this fleet's usual {median:0.#} with {queued} waiting.");
        }
        if (median > 0 && inFlight >= median)
        {
            return Vital("In flight", inFlight, "items", capped, VitalBand.Good,
                $"Running {inFlight} against this fleet's usual {median:0.#}.");
        }
        return Vital("In flight", inFlight, "items", capped, VitalBand.Neutral,
            "Concurrency is within this fleet's usual range.");
    }

    private static FleetVital FailureRateVital(
        double rate, int windowTerminal, IReadOnlyList<double>? history, AdminModelOptions options)
    {
        var capped = CapHistory(history, options);
        if (capped.Count < options.MinimumHistorySamples || windowTerminal == 0)
        {
            return Vital("Failure rate", rate, "fraction", capped, VitalBand.Neutral,
                windowTerminal == 0
                    ? "No terminal items in the window to judge."
                    : "Not enough history to judge this fleet's failure rate yet.");
        }
        var worst = capped.Max();
        if (rate > worst && rate > 0)
        {
            return Vital("Failure rate", rate, "fraction", capped, VitalBand.Bad,
                $"Failure rate {rate:0%} exceeds this fleet's recent worst {worst:0%} — inspect the failures.");
        }
        if (rate == 0 && capped.Any(h => h > 0))
        {
            return Vital("Failure rate", rate, "fraction", capped, VitalBand.Good,
                "No failures in the window after recent failures — the fleet recovered.");
        }
        return Vital("Failure rate", rate, "fraction", capped, VitalBand.Neutral,
            "Failure rate is within this fleet's recent range.");
    }

    private static FleetVital ThroughputVital(
        double perHour, IReadOnlyList<double>? history, AdminModelOptions options)
    {
        var capped = CapHistory(history, options);
        if (capped.Count < options.MinimumHistorySamples)
        {
            return Vital("Throughput", perHour, "completions/hour", capped, VitalBand.Neutral,
                "Not enough history to judge this fleet's throughput yet.");
        }
        var median = Median(capped);
        if (perHour > median)
        {
            return Vital("Throughput", perHour, "completions/hour", capped, VitalBand.Good,
                $"Completing {perHour:0.#}/h against this fleet's usual {median:0.#}/h.");
        }
        // Quiet is quiet, not broken: never Bad, whatever the numbers say.
        return Vital("Throughput", perHour, "completions/hour", capped, VitalBand.Neutral,
            "Throughput is at or below usual — a quiet period, not a failure.");
    }

    private static FleetVital ParkedVital(
        int parked, IReadOnlyList<double>? history, AdminModelOptions options)
    {
        var capped = CapHistory(history, options);
        if (capped.Count < options.MinimumHistorySamples)
        {
            return Vital("Parked", parked, "items", capped, VitalBand.Neutral,
                "Not enough history to judge this fleet's parked count yet.");
        }
        if (parked == 0)
        {
            return Vital("Parked", parked, "items", capped, VitalBand.Good,
                "Nothing is parked.");
        }
        if (parked > capped.Max())
        {
            return Vital("Parked", parked, "items", capped, VitalBand.Bad,
                $"Parked items ({parked}) exceed this fleet's recent worst {capped.Max():0.#} — operators hold the keys.");
        }
        return Vital("Parked", parked, "items", capped, VitalBand.Neutral,
            "Parked count is within this fleet's recent range.");
    }

    private static FleetVital Vital(
        string name, double value, string unit,
        IReadOnlyList<double> history, VitalBand band, string reason) => new()
        {
            Name = name,
            Value = value,
            Unit = unit,
            History = history,
            Band = band,
            Reason = reason,
        };

    private static IReadOnlyList<AdminWorkItem> BoundedItems(FleetSnapshot snapshot)
    {
        var items = snapshot.Items;
        if (items is null)
        {
            return [];
        }
        return items.Count <= FleetSnapshot.MaxItems ? items : items.Take(FleetSnapshot.MaxItems).ToList();
    }

    private static List<double> CapHistory(IReadOnlyList<double>? history, AdminModelOptions options)
    {
        if (history is null || history.Count == 0)
        {
            return [];
        }
        return history
            .Where(h => !double.IsNaN(h) && !double.IsInfinity(h) && h >= 0)
            .TakeLast(Math.Max(1, options.MaxHistorySamples))
            .ToList();
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
