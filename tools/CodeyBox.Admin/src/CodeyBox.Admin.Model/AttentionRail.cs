namespace CodeyBox.Admin.Model;

/// <summary>One card on the rail: an item that needs a human, and where it lives on the map.</summary>
public sealed record RailCard
{
    public required string ItemId { get; init; }

    public required string Title { get; init; }

    public required string State { get; init; }

    /// <summary>Top attention reason, in operator terms.</summary>
    public required string Reason { get; init; }

    public required double Score { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public required double NodeX { get; init; }

    public required double NodeY { get; init; }

    /// <summary>Position in the stack, 0 at the top. Cards are stacked in the order their nodes appear top-to-bottom.</summary>
    public required int Order { get; init; }

    /// <summary>
    /// Leader channel: the vertical run of card <c>i</c>'s leader sits
    /// <c>Channel</c> steps out from the rail. Lower cards get channels nearer
    /// the rail, so with nodes ordered top-to-bottom no two leaders cross.
    /// </summary>
    public required int Channel { get; init; }

    /// <summary>True on the single most urgent card.</summary>
    public bool IsTop { get; init; }
}

/// <summary>
/// The inbox docked to the map: every item that needs a person, each with a
/// leader to its node. Ordering is by node position (top-to-bottom), which
/// is what makes the leaders legible; urgency is carried on the card itself
/// and the camera already goes to the most urgent. Pure and deterministic.
/// </summary>
public static class AttentionRail
{
    public static IReadOnlyList<RailCard> Build(
        FleetSnapshot snapshot,
        IReadOnlyList<AttentionScore> attention,
        FleetMapLayout layout,
        int maxCards = 12)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(layout);
        var scoreById = new Dictionary<string, AttentionScore>(StringComparer.Ordinal);
        foreach (var score in attention ?? [])
        {
            if (score is not null)
            {
                scoreById.TryAdd(score.ItemId, score);
            }
        }

        var candidates = new List<(AdminWorkItem Item, MapNodeLayout Node, double Score, string Reason)>();
        foreach (var item in snapshot.Items ?? [])
        {
            if (item is null || string.IsNullOrEmpty(item.Id) || !TerminalVisibility.NeedsYou(item.State))
            {
                continue;
            }
            if (!layout.Nodes.TryGetValue(item.Id, out var node))
            {
                continue;
            }
            var score = scoreById.TryGetValue(item.Id, out var s) ? s.Score : 0;
            var reason = scoreById.TryGetValue(item.Id, out var r) && r.Reasons.Count > 0 ? r.Reasons[0] : $"Needs you ({item.State}).";
            candidates.Add((item, node, score, reason));
        }

        // Keep the most urgent when there are too many; then stack by position.
        var chosen = candidates
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Item.UpdatedAt)
            .ThenBy(c => c.Item.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, maxCards))
            .ToList();
        var topId = chosen.Count > 0 ? chosen[0].Item.Id : null;
        var stacked = chosen
            .OrderBy(c => c.Node.Y)
            .ThenBy(c => c.Node.X)
            .ThenBy(c => c.Item.Id, StringComparer.Ordinal)
            .ToList();

        // Default channels assume a uniform card pitch beside a map at working
        // zoom; the renderer re-runs AssignChannels with real screen positions.
        var defaults = AssignChannels(stacked.Select((c, i) => (CardY: i * 90.0, NodeY: c.Node.Y)).ToList());
        var cards = new List<RailCard>(stacked.Count);
        for (var i = 0; i < stacked.Count; i++)
        {
            var (item, node, score, reason) = stacked[i];
            cards.Add(new RailCard
            {
                ItemId = item.Id,
                Title = item.Title,
                State = item.State,
                Reason = reason,
                Score = score,
                UpdatedAt = item.UpdatedAt,
                NodeX = node.X,
                NodeY = node.Y,
                Order = i,
                Channel = defaults[i],
                IsTop = string.Equals(item.Id, topId, StringComparison.Ordinal),
            });
        }
        return cards;
    }

    /// <summary>
    /// Assigns each leader a channel (0 = nearest the rail) so the elbow
    /// paths cross as little as possible — zero in every stacked layout that
    /// admits it. Whether two leaders cross depends on how their card and
    /// node heights interleave *on screen*, which changes with the camera,
    /// so the renderer calls this with real positions on every draw; the
    /// algorithm is greedy insertion, each leader placed where it adds the
    /// fewest crossings, in card order. Deterministic; n is at most a dozen.
    /// </summary>
    public static IReadOnlyList<int> AssignChannels(IReadOnlyList<(double CardY, double NodeY)> leaders)
    {
        ArgumentNullException.ThrowIfNull(leaders);
        var order = new List<int>(leaders.Count); // index 0 = channel 0 (nearest the rail)
        for (var i = 0; i < leaders.Count; i++)
        {
            var bestPos = order.Count;
            var bestCross = int.MaxValue;
            for (var pos = 0; pos <= order.Count; pos++)
            {
                var trial = new List<int>(order);
                trial.Insert(pos, i);
                var cross = CountCrossings(trial.Select((leader, channel) => (leaders[leader].CardY, leaders[leader].NodeY, channel)).ToList());
                if (cross < bestCross)
                {
                    bestCross = cross;
                    bestPos = pos;
                }
            }
            order.Insert(bestPos, i);
        }
        var channels = new int[leaders.Count];
        for (var channel = 0; channel < order.Count; channel++)
        {
            channels[order[channel]] = channel;
        }
        return channels;
    }

    /// <summary>
    /// Geometry check used by tests and by the renderer's self-check: the
    /// number of proper crossings between elbow leaders. Each leader runs
    /// from the rail (x = 0) at <c>cardY</c> left to its channel
    /// (x = −(channel + 1) · <paramref name="channelGap"/>), vertically to
    /// <c>nodeY</c>, then left to the node's edge at <paramref name="nodeEdgeX"/>.
    /// </summary>
    public static int CountCrossings(
        IReadOnlyList<(double CardY, double NodeY, int Channel)> leaders,
        double channelGap = 8,
        double nodeEdgeX = -1000)
    {
        ArgumentNullException.ThrowIfNull(leaders);
        var segments = new List<(int Leader, double X1, double Y1, double X2, double Y2)>();
        for (var i = 0; i < leaders.Count; i++)
        {
            var (cardY, nodeY, channel) = leaders[i];
            var cx = -(channel + 1) * channelGap;
            segments.Add((i, 0, cardY, cx, cardY));
            segments.Add((i, cx, cardY, cx, nodeY));
            segments.Add((i, cx, nodeY, nodeEdgeX, nodeY));
        }
        var crossings = 0;
        for (var a = 0; a < segments.Count; a++)
        {
            for (var b = a + 1; b < segments.Count; b++)
            {
                if (segments[a].Leader == segments[b].Leader)
                {
                    continue;
                }
                if (ProperlyIntersect(segments[a], segments[b]))
                {
                    crossings++;
                }
            }
        }
        return crossings;
    }

    private static bool ProperlyIntersect(
        (int Leader, double X1, double Y1, double X2, double Y2) s,
        (int Leader, double X1, double Y1, double X2, double Y2) t)
    {
        // Axis-aligned segments only: a horizontal and a vertical cross when
        // each strictly spans the other's coordinate.
        var sHorizontal = s.Y1 == s.Y2;
        var tHorizontal = t.Y1 == t.Y2;
        if (sHorizontal == tHorizontal)
        {
            return false;
        }
        var h = sHorizontal ? s : t;
        var v = sHorizontal ? t : s;
        var hx1 = Math.Min(h.X1, h.X2);
        var hx2 = Math.Max(h.X1, h.X2);
        var vy1 = Math.Min(v.Y1, v.Y2);
        var vy2 = Math.Max(v.Y1, v.Y2);
        return v.X1 > hx1 && v.X1 < hx2 && h.Y1 > vy1 && h.Y1 < vy2;
    }
}

/// <summary>A blocking finding as the decision needs it: who said it, how bad, what.</summary>
public sealed record DecisionFinding(string Auditor, string Severity, string Title);

/// <summary>
/// What a bubble shows so the operator can decide without opening anything:
/// the headline, the evidence, and the actions the item admits. Findings
/// null means "not fetched"; empty means "none recorded" — and that is said
/// out loud, because park text is not evidence.
/// </summary>
public sealed record DecisionBrief
{
    public required string Headline { get; init; }

    public IReadOnlyList<DecisionFinding> Findings { get; init; } = [];

    /// <summary>Set when findings are known to be absent, or not fetched.</summary>
    public string? FindingsNote { get; init; }

    public string? LastError { get; init; }

    public string? OpenQuestion { get; init; }

    /// <summary>"waits on a1 (Auditing)" etc.; empty when nothing.</summary>
    public string WaitingOn { get; init; } = string.Empty;

    public required IReadOnlyList<MapItemAction> Actions { get; init; }
}

public static class DecisionBriefBuilder
{
    public const int MaxFindings = 4;

    public static DecisionBrief Build(
        string? state,
        ItemActivity? activity,
        string? lastError,
        int? auditIterations,
        int? auditMaxIterations,
        IReadOnlyList<DecisionFinding>? blockingFindings,
        string? openQuestion)
    {
        var s = state ?? string.Empty;
        var iterations = auditIterations is > 0
            ? auditMaxIterations is > 0 ? $"after {auditIterations} of {auditMaxIterations} audit iterations" : $"after {auditIterations} audit iteration{(auditIterations == 1 ? "" : "s")}"
            : string.Empty;
        var headline = s switch
        {
            "AuditFailed" => $"Audit failed{(iterations.Length > 0 ? " " + iterations : "")}.",
            "Failed" => "Failed during work.",
            "MergeConflictResolutionFailed" => "Merge conflicts could not be resolved.",
            "AbandonedAfterRecoveryAttempts" => "Abandoned: host recovery retries exhausted.",
            "NeedsOperatorInput" => "The agent asked you a question.",
            _ => $"Needs you ({s}).",
        };

        string? note = null;
        var findings = (blockingFindings ?? []).Where(f => f is not null).Take(MaxFindings).ToList();
        if (blockingFindings is null)
        {
            note = "Findings not fetched yet.";
        }
        else if (findings.Count == 0 && s is "AuditFailed" or "Failed" or "MergeConflictResolutionFailed" or "AbandonedAfterRecoveryAttempts")
        {
            note = s == "AuditFailed"
                ? "No blocking findings recorded — the park text is not evidence. Retrying the audit will not change the verdict; retry from work, or delegate."
                : "No blocking findings recorded; the failure was not an audit verdict — see the last error.";
        }
        else if (blockingFindings.Count > MaxFindings)
        {
            note = $"+{blockingFindings.Count - MaxFindings} more blocking findings in Detail.";
        }

        var waiting = activity is { Kind: ActivityKind.BlockedByDependency, Blockers.Count: > 0 }
            ? "waits on " + string.Join(", ", activity.Blockers.Select(b => $"{Short(b.Id)} ({b.State})"))
            : string.Empty;

        return new DecisionBrief
        {
            Headline = headline,
            Findings = findings,
            FindingsNote = note,
            LastError = string.IsNullOrWhiteSpace(lastError) ? null : lastError.Trim(),
            OpenQuestion = string.IsNullOrWhiteSpace(openQuestion) ? null : openQuestion.Trim(),
            WaitingOn = waiting,
            Actions = MapItemActions.For(s),
        };
    }

    private static string Short(string id) => id.Length >= 8 ? id[..8] : id;
}
