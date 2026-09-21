using System.Text.Json;
using CodeyBox.Admin.Model;
using CodeyBox.Admin.Web.Components.Shared;

namespace CodeyBox.Admin.Web.Services;

/// <summary>One item's fetched history as the page holds it: which item, and how much arrived.</summary>
public sealed record OpenItemPayload
{
    public required string Id { get; init; }

    /// <summary>"loading" until the per-item surfaces answer; "ready"; "unavailable" when they failed outright.</summary>
    public required string Status { get; init; }

    public ItemStagePipeline? Pipeline { get; init; }

    /// <summary>For items that need a person: the evidence and the actions, so the bubble carries the decision.</summary>
    public DecisionBrief? Decision { get; init; }
}

/// <summary>
/// Builds the canonical JSON frame the canvas renderer draws. The payload is
/// the idle-cost gate: the page re-renders only when the payload string
/// changes, so a quiet fleet costs one string comparison per poll and zero JS
/// interop calls. Pure and deterministic — the same inputs always produce the
/// same string, byte for byte.
/// </summary>
public static class FleetMapFrameBuilder
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Builds the frame payload. Never throws on odd input.</summary>
    public static string BuildPayload(
        FleetSnapshot snapshot,
        FleetProjection projection,
        FleetMapLayout layout,
        IReadOnlyDictionary<string, MapNodeBadge> badges,
        CameraState camera,
        IReadOnlyList<MapTransition> transitions,
        FleetMapOptions? options = null,
        IReadOnlyDictionary<string, OpenItemPayload>? details = null,
        IReadOnlyList<RailCard>? rail = null,
        IReadOnlyList<GhostGroup>? ghosts = null,
        IReadOnlyList<ReleaseFrame>? releases = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(badges);
        ArgumentNullException.ThrowIfNull(camera);
        options ??= new FleetMapOptions();
        transitions ??= [];

        var items = new Dictionary<string, AdminWorkItem>(StringComparer.Ordinal);
        foreach (var item in snapshot.Items ?? [])
        {
            if (item is not null && !string.IsNullOrEmpty(item.Id))
            {
                items.TryAdd(item.Id, item);
            }
        }
        var itemList = items.Values.ToList();

        var edges = new SortedSet<(string From, string To)>();
        var dependents = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items.Values)
        {
            foreach (var dep in item.DependsOn ?? [])
            {
                if (!string.IsNullOrEmpty(dep) && items.ContainsKey(dep) && edges.Add((dep, item.Id)))
                {
                    dependents[dep] = dependents.GetValueOrDefault(dep) + 1;
                }
                if (edges.Count >= FleetSnapshot.MaxItems)
                {
                    break;
                }
            }
        }

        var nodes = new List<object>();
        foreach (var id in items.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!layout.Nodes.TryGetValue(id, out var position)
                || !badges.TryGetValue(id, out var badge))
            {
                continue;
            }
            var item = items[id];
            var vocab = StatusVocabulary.ForWorkItem(item.State);
            projection.Activities.TryGetValue(id, out var activity);
            var actions = MapItemActions.For(item.State);
            nodes.Add(new
            {
                id,
                x = Round(position.X),
                y = Round(position.Y),
                lane = position.LaneId,
                chain = position.ChainId,
                depth = position.Depth,
                zone = position.Zone.ToString().ToLowerInvariant(),
                batch = position.Batch,
                pushed = position.PushedByDependency,
                spaced = position.Spaced,
                shape = badge.Shape.ToString().ToLowerInvariant(),
                tone = badge.Tone,
                glyph = vocab.Glyph,
                stateText = vocab.Text,
                stage = (ItemStagePipelineBuilder.StageOf(item.State) ?? PipelineStage.Work).ToString().ToLowerInvariant(),
                title = string.IsNullOrWhiteSpace(item.Title) ? ShortId(id) : item.Title.Trim(),
                label = badge.Label,
                sub = badge.SubLabel,
                urgency = badge.HasUrgencyRing,
                settled = TerminalVisibility.IsSettled(item.State),
                needsYou = TerminalVisibility.NeedsYou(item.State),
                titleTextPx = badge.TitleTextPx,
                subTextPx = badge.SubTextPx,
                activity = activity?.Kind.ToString() ?? "Unknown",
                activityText = activity?.Summary ?? string.Empty,
                waiting = dependents.GetValueOrDefault(id),
                blockers = (activity?.Blockers ?? []).Select(b => new
                {
                    id = b.Id,
                    shortId = ShortId(b.Id),
                    title = items.TryGetValue(b.Id, out var blocker) && !string.IsNullOrWhiteSpace(blocker.Title) ? blocker.Title.Trim() : ShortId(b.Id),
                    state = b.State,
                    onMap = items.ContainsKey(b.Id),
                }).ToList(),
                actions = actions.Select(a => a.Key).ToList(),
            });
        }

        var lanes = BuildLanes(layout, projection, items, options);

        var urgent = (projection.Attention ?? [])
            .Where(a => a is not null)
            .Select(a => new { id = a!.ItemId, score = Math.Round(a.Score, 1) })
            .Take(8)
            .ToList();

        var frame = new
        {
            opts = new
            {
                nodeW = options.NodeWidth,
                nodeH = options.NodeHeight,
                colGap = options.ColumnGap,
                rowGap = options.RowGap,
                laneGap = options.ChainLaneGap,
                minFitZoom = options.MinFitZoom,
                panMargin = options.PanMarginFraction,
                futureNear = options.FutureNearBatches,
                futureCompression = options.FutureCompression,
                pastMinutesPerColumn = options.PastMinutesPerColumn,
                fullDetailZoom = options.FullDetailZoom,
                compactDetailZoom = options.CompactDetailZoom,
                openZoomStart = options.OpenZoomStart,
                openZoomEnd = options.OpenZoomEnd,
                minTextPx = options.MinReadableTextPx,
            },
            axis = new
            {
                nowX = 0,
                nowBucket = layout.NowBucket.ToString("u"),
                futureBatches = layout.FutureBatches,
                ticks = layout.Ticks.Select(t => new
                {
                    x = Round(t.X),
                    label = t.Label,
                    zone = t.Zone.ToString().ToLowerInvariant(),
                    at = t.At?.ToUniversalTime().ToString("o"),
                }).ToList(),
                // The faithful runs of the past: what a position between landings means.
                stretches = (layout.Stretches ?? []).Select(r => new
                {
                    x0 = Round(r.OldestX),
                    x1 = Round(r.NewestX),
                    t0 = r.Oldest.ToUniversalTime().ToString("o"),
                    t1 = r.Newest.ToUniversalTime().ToString("o"),
                }).ToList(),
                // Quiet stretches cut from the past: each says what it skipped.
                breaks = (layout.Breaks ?? []).Select(b => new
                {
                    x = Round(b.X),
                    w = Round(b.Width),
                    label = b.Label,
                    exact = b.Exact,
                    minutes = Math.Round(b.Skipped.TotalMinutes),
                }).ToList(),
                basis = "past: when it landed — quiet is cut and labelled, landings within minutes of a lane-mate are spaced a card apart · future: predicted order from dependencies and capacity, not a schedule",
            },
            lanes,
            nodes,
            // Routed: an edge that would run through a node between its endpoints arcs over it instead.
            edges = EdgeRouter.Route(layout, edges, options).Select(e => new { from = e.From, to = e.To, bend = e.Bend }).ToList(),
            camera = new
            {
                cx = Round(camera.Viewport.CenterX),
                cy = Round(camera.Viewport.CenterY),
                zoom = Round(camera.Viewport.Zoom),
                focus = camera.FocusKind.ToString().ToLowerInvariant(),
                focusId = camera.FocusId,
                manual = camera.Manual,
                travelMs = camera.TravelMs,
                open = camera.OpenItemId,
                hold = camera.HoldReason,
                dwell = Round(camera.DwellSeconds),
            },
            details = BuildDetails(details, items),
            ghosts = (ghosts ?? []).Where(g => g is not null).SelectMany(g => g.Shown.Select((p, i) => new
            {
                id = p.Suggestion.Id,
                parent = g.ParentId,
                x = Round(p.X),
                y = Round(p.Y),
                rank = p.Rank,
                title = p.Suggestion.Title,
                severity = p.Suggestion.Severity,
                category = p.Suggestion.Category,
                effort = p.Suggestion.Effort,
                rationale = p.Suggestion.Rationale,
                files = p.Suggestion.Files.Take(4).ToList(),
                folded = i == g.Shown.Count - 1 ? g.Folded : 0,
            })).OrderBy(g => g.id, StringComparer.Ordinal).ToList(),
            releases = (releases ?? []).Where(r => r is not null).Select(r => new
            {
                id = r.Id,
                name = r.Name,
                state = r.State,
                total = r.Total,
                done = r.Done,
                blocking = r.Blocking,
                x = Round(r.X),
                y = Round(r.Y),
                w = Round(r.W),
                h = Round(r.H),
                remediation = r.RemediationOnMap,
            }).ToList(),
            rail = (rail ?? []).Select(c => new
            {
                id = c.ItemId,
                order = c.Order,
                channel = c.Channel,
                top = c.IsTop,
                x = Round(c.NodeX),
                y = Round(c.NodeY),
            }).ToList(),
            transitions = transitions
                .Where(t => t is not null)
                .Select(t => new { kind = t!.Kind.ToString(), item = t.ItemId, detail = t.Detail })
                .ToList(),
            stats = new
            {
                // Note: no wall-clock timestamp here by design. The payload is
                // the idle gate — identical inputs must serialize identically
                // so a quiet fleet skips the JS bridge entirely.
                items = nodes.Count,
                chains = projection.Chains?.Count ?? 0,
                urgent = urgent,
            },
        };
        return JsonSerializer.Serialize(frame, Json);
    }

    private static List<object> BuildLanes(
        FleetMapLayout layout,
        FleetProjection projection,
        Dictionary<string, AdminWorkItem> items,
        FleetMapOptions options)
    {
        var prefixByChain = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var chain in projection.Chains ?? [])
        {
            if (chain?.Id is not null)
            {
                prefixByChain[chain.Id] = chain.SeriesPrefix;
            }
        }
        var membersByLane = layout.Nodes.Values
            .GroupBy(n => n.LaneId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var lanes = new List<object>();
        foreach (var lane in layout.Lanes.OrderBy(l => l.ChainId, StringComparer.Ordinal))
        {
            if (!membersByLane.TryGetValue(lane.ChainId, out var members) || members.Count == 0)
            {
                continue;
            }
            var blocked = 0;
            var running = 0;
            var parkedOrFailed = 0;
            var settled = 0;
            foreach (var member in members)
            {
                if (items.TryGetValue(member.ItemId, out var memberItem) && TerminalVisibility.IsSettled(memberItem.State))
                {
                    settled++;
                }
                if (!projection.Activities.TryGetValue(member.ItemId, out var activity))
                {
                    continue;
                }
                switch (activity.Kind)
                {
                    case ActivityKind.BlockedByDependency: blocked++; break;
                    case ActivityKind.Running: running++; break;
                    case ActivityKind.Parked or ActivityKind.Failed: parkedOrFailed++; break;
                }
            }
            var chainIds = lane.ChainIds.Count > 0 ? lane.ChainIds : [lane.ChainId];
            string label;
            if (string.Equals(lane.ChainId, FleetMapBuilder.LooseLaneId, StringComparison.Ordinal))
            {
                label = "No dependencies";
            }
            else if (FleetMapBuilder.IsHistoryLane(lane.ChainId))
            {
                label = "Landed work";
            }
            else if (chainIds.Count == 1 && prefixByChain.TryGetValue(chainIds[0], out var prefix) && !string.IsNullOrWhiteSpace(prefix))
            {
                label = prefix;
            }
            else if (members.Count == 1)
            {
                label = items.TryGetValue(members[0].ItemId, out var only) && !string.IsNullOrWhiteSpace(only.Title)
                    ? Truncate(only.Title.Trim(), options.MaxLabelChars)
                    : ShortId(members[0].ItemId);
            }
            else
            {
                // A multi-item lane without a series name: name it by its root (leftmost, topmost).
                var root = members.OrderBy(m => m.Depth).ThenBy(m => m.Y).ThenBy(m => m.ItemId, StringComparer.Ordinal).First();
                label = items.TryGetValue(root.ItemId, out var rootItem) && !string.IsNullOrWhiteSpace(rootItem.Title)
                    ? Truncate(rootItem.Title.Trim(), options.MaxLabelChars)
                    : ShortId(root.ItemId);
            }
            // Frame the nodes actually present, with a little air — not the row allowance.
            var pad = options.NodeHeight / 4;
            lanes.Add(new
            {
                id = lane.ChainId,
                x = Round(members.Min(m => m.X) - options.NodeWidth / 2 - pad),
                y = Round(members.Min(m => m.Y) - options.NodeHeight / 2 - pad),
                w = Round(members.Max(m => m.X) - members.Min(m => m.X) + options.NodeWidth + pad * 2),
                h = Round(members.Max(m => m.Y) - members.Min(m => m.Y) + options.NodeHeight + pad * 2),
                label,
                n = members.Count,
                blocked,
                running,
                settled,
                attention = parkedOrFailed,
            });
        }
        return lanes;
    }

    /// <summary>
    /// Per-item history for every node the page has fetched (the set visible
    /// at pipeline zoom). Keyed by id, sorted, so the payload is stable.
    /// </summary>
    private static Dictionary<string, object> BuildDetails(
        IReadOnlyDictionary<string, OpenItemPayload>? details,
        Dictionary<string, AdminWorkItem> items)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (details is null)
        {
            return result;
        }
        foreach (var id in details.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var detail = details[id];
            if (detail is null || string.IsNullOrEmpty(id) || !items.ContainsKey(id))
            {
                continue;
            }
            var pipeline = detail.Pipeline;
            var routed = StageLoopRouter.Route(pipeline?.Loops ?? []);
            var decision = detail.Decision;
            result[id] = new
            {
                history = pipeline is null ? detail.Status : pipeline.History == StageHistory.Full ? "full" : "stateOnly",
                note = pipeline?.HistoryNote ?? (detail.Status == "loading" ? "Loading history…" : "History unavailable — the timeline could not be fetched."),
                summary = pipeline?.Summary ?? string.Empty,
                current = (pipeline?.Current ?? ItemStagePipelineBuilder.StageOf(items[id].State) ?? PipelineStage.Work).ToString().ToLowerInvariant(),
                stages = (pipeline?.Stages ?? []).Select(s => new
                {
                    key = s.Stage.ToString().ToLowerInvariant(),
                    label = s.Label,
                    status = LowerFirst(s.Status.ToString()),
                    visits = s.Visits,
                    detail = s.Detail,
                }).ToList(),
                loops = routed.Select(r => new
                {
                    from = r.FromIndex,
                    to = r.ToIndex,
                    kind = r.Loop.Kind.ToString().ToLowerInvariant(),
                    count = r.Loop.Count,
                    label = r.Loop.Label,
                    tier = r.Tier,
                    labelCenter = Round(r.LabelCenter),
                    labelHalfWidth = Round(r.LabelHalfWidth),
                }).ToList(),
                decision = decision is null ? null : new
                {
                    headline = decision.Headline,
                    findings = decision.Findings.Select(f => new { auditor = f.Auditor, severity = f.Severity, title = f.Title }).ToList(),
                    findingsNote = decision.FindingsNote,
                    lastError = decision.LastError,
                    openQuestion = decision.OpenQuestion,
                    waitingOn = decision.WaitingOn,
                    actions = decision.Actions.Select(a => new { key = a.Key, label = a.Label, danger = a.Danger, confirm = a.Confirm }).ToList(),
                },
            };
        }
        return result;
    }

    private static string LowerFirst(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static string ShortId(string id) => id.Length >= 8 ? id[..8] : id;

    private static string Truncate(string value, int maxChars) =>
        maxChars <= 0 ? string.Empty : value.Length <= maxChars ? value : value[..Math.Max(0, maxChars - 1)] + "…";

    private static double Round(double value) => Math.Round(value, 2);
}
