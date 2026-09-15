using System.Text.Json;
using CodeyBox.Admin.Model;
using CodeyBox.Admin.Web.Components.Shared;

namespace CodeyBox.Admin.Web.Services;

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
        IReadOnlyList<MapTransition> transitions)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(badges);
        ArgumentNullException.ThrowIfNull(camera);
        transitions ??= [];

        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in snapshot.Items ?? [])
        {
            if (item is not null && !string.IsNullOrEmpty(item.Id))
            {
                states.TryAdd(item.Id, item.State ?? string.Empty);
            }
        }

        var nodes = new List<object>();
        foreach (var id in states.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!layout.Nodes.TryGetValue(id, out var position)
                || !badges.TryGetValue(id, out var badge))
            {
                continue;
            }
            var vocab = StatusVocabulary.ForWorkItem(states[id]);
            nodes.Add(new
            {
                id,
                x = Round(position.X),
                y = Round(position.Y),
                shape = badge.Shape.ToString().ToLowerInvariant(),
                tone = badge.Tone,
                glyph = vocab.Glyph,
                label = badge.Label,
                sub = badge.SubLabel,
                shortLabel = badge.ShortLabel,
                urgency = badge.HasUrgencyRing,
                titleTextPx = badge.TitleTextPx,
                subTextPx = badge.SubTextPx,
            });
        }

        var edges = new SortedSet<(string From, string To)>();
        foreach (var item in snapshot.Items ?? [])
        {
            if (item?.DependsOn is null || string.IsNullOrEmpty(item.Id))
            {
                continue;
            }
            foreach (var dep in item.DependsOn)
            {
                if (!string.IsNullOrEmpty(dep) && states.ContainsKey(dep) && states.ContainsKey(item.Id))
                {
                    edges.Add((dep, item.Id));
                }
                if (edges.Count >= FleetSnapshot.MaxItems)
                {
                    break;
                }
            }
        }

        var urgent = (projection.Attention ?? [])
            .Where(a => a is not null)
            .Select(a => new { id = a!.ItemId, score = Math.Round(a.Score, 1) })
            .Take(8)
            .ToList();

        var frame = new
        {
            nodes,
            edges = edges.Select(e => new { from = e.From, to = e.To }).ToList(),
            camera = new
            {
                cx = Round(camera.Viewport.CenterX),
                cy = Round(camera.Viewport.CenterY),
                zoom = Round(camera.Viewport.Zoom),
                focus = camera.FocusKind.ToString().ToLowerInvariant(),
                focusId = camera.FocusId,
                manual = camera.Manual,
            },
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

    private static double Round(double value) => Math.Round(value, 2);
}
