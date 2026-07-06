using CodeyBox.Core;
using CodeyBox.Sandbox.Graphical;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Translates a recorded <see cref="TraceAction"/> plus the re-located target
/// into the concrete <see cref="ComputerUseRequest"/> the bridge dispatches.
/// This is the request-shape / input-event-geometry concern, kept out of
/// <see cref="ReplayEngine"/> so step-orchestration and input-shape semantics
/// evolve independently — a change to how a scroll or drag is encoded lives
/// here, not in the same file as the replay loop.
/// </summary>
internal static class ReplayRequestBuilder
{
    public static ComputerUseRequest BuildRequestForReplay(TraceAction action, LocatedTarget? located)
    {
        return action.Kind switch
        {
            "click" => new ComputerUseRequest
            {
                Action = "click",
                X = located!.CenterX,
                Y = located.CenterY,
            },
            "double_click" => new ComputerUseRequest
            {
                Action = "double_click",
                X = located!.CenterX,
                Y = located.CenterY,
            },
            "move" => new ComputerUseRequest
            {
                Action = "move",
                X = located!.CenterX,
                Y = located.CenterY,
            },
            "scroll" => BuildScrollRequest(action, located),
            "key" => BuildTargetedKeyRequest(action, located),
            "type" => BuildTargetedTypeRequest(action, located),
            "events" => new ComputerUseRequest
            {
                Action = "events",
                Events = RelocateEvents(action.InputEvents, located, action),
            },
            _ => throw new NotSupportedException($"Unsupported replay action kind '{action.Kind}'."),
        };
    }

    private static ComputerUseRequest BuildTargetedKeyRequest(TraceAction action, LocatedTarget? located)
    {
        var key = FirstKey(action.InputEvents);
        if (located is not null)
        {
            return new ComputerUseRequest
            {
                Action = "events",
                Events =
                [
                    new SandboxInputEvent
                    {
                        Type = SandboxInputEventType.Click,
                        X = located.CenterX,
                        Y = located.CenterY,
                    },
                    new SandboxInputEvent
                    {
                        Type = SandboxInputEventType.Key,
                        Key = key,
                    },
                ],
            };
        }

        return new ComputerUseRequest
        {
            Action = "key",
            Key = key,
        };
    }

    private static ComputerUseRequest BuildTargetedTypeRequest(TraceAction action, LocatedTarget? located)
    {
        var text = FirstText(action.InputEvents);
        if (located is not null)
        {
            return new ComputerUseRequest
            {
                Action = "events",
                Events =
                [
                    new SandboxInputEvent
                    {
                        Type = SandboxInputEventType.Click,
                        X = located.CenterX,
                        Y = located.CenterY,
                    },
                    new SandboxInputEvent
                    {
                        Type = SandboxInputEventType.Type,
                        Text = text,
                    },
                ],
            };
        }

        return new ComputerUseRequest
        {
            Action = "type",
            Text = text,
        };
    }

    private static ComputerUseRequest BuildScrollRequest(TraceAction action, LocatedTarget? located)
    {
        // The bridge resolves the scroll event from (ScrollX ?? X, ScrollY ?? Y)
        // and the validator rejects events with both axes non-zero. We:
        //   - pull the magnitude from the first SandboxInputEvent of Type=Scroll,
        //     not action.InputEvents[0] verbatim (a malformed recording whose
        //     first event is a Click could push pixel coords as scroll units);
        //   - zero the smaller axis when the recording emits a two-axis scroll,
        //     so the validator never rejects a real recording for shape.
        SandboxInputEvent? scrollEvent = null;
        foreach (var e in action.InputEvents)
        {
            if (e.Type == SandboxInputEventType.Scroll)
            {
                scrollEvent = e;
                break;
            }
        }
        if (scrollEvent is null)
        {
            // Recorder bug: a scroll action with no Scroll-typed event in its
            // InputEvents. Surface as a categorical recording-shape failure
            // upfront so operators see "recording has no Scroll event" instead
            // of the bridge validator's generic "Scroll events require a
            // non-zero X or Y amount" once it tries to dispatch null axes.
            throw new MalformedTraceException(
                "scroll action carries no SandboxInputEvent of Type=Scroll (recorder bug)");
        }
        var sx = scrollEvent.X ?? 0;
        var sy = scrollEvent.Y ?? 0;
        if (sx == 0 && sy == 0)
        {
            // Recorder bug: a Scroll event with zero magnitude on both axes
            // would dispatch as a no-op the validator rejects.
            throw new MalformedTraceException(
                "scroll action's Scroll event has zero magnitude on both axes (recorder bug)");
        }
        if (sx != 0 && sy != 0)
        {
            // Drop the smaller-magnitude axis — vertical wins on ties.
            if (Math.Abs(sx) > Math.Abs(sy)) sy = 0;
            else sx = 0;
        }
        if (located is not null)
        {
            return new ComputerUseRequest
            {
                Action = "events",
                Events =
                [
                    new SandboxInputEvent
                    {
                        Type = SandboxInputEventType.Move,
                        X = located.CenterX,
                        Y = located.CenterY,
                    },
                    new SandboxInputEvent
                    {
                        Type = SandboxInputEventType.Scroll,
                        X = sx == 0 ? null : sx,
                        Y = sy == 0 ? null : sy,
                    },
                ],
            };
        }

        return new ComputerUseRequest
        {
            Action = "scroll",
            ScrollX = sx == 0 ? null : sx,
            ScrollY = sy == 0 ? null : sy,
        };
    }

    private static IReadOnlyList<SandboxInputEvent> RelocateEvents(
        IReadOnlyList<SandboxInputEvent> events,
        LocatedTarget? located,
        TraceAction action)
    {
        if (located is null) return events;
        // Anchor relative offsets at the recorded centre so a drag (or any
        // multi-event sequence with internal motion) preserves its geometry
        // when the target moved on screen: each event's recorded (X, Y) is
        // translated by the delta from the recorded anchor to the located
        // anchor. If the action has no recorded coordinates to anchor on, the
        // first Click/Move position acts as the anchor.
        var anchor = FindAnchor(events);
        if (anchor is null)
        {
            return CollapseToCentre(events, located);
        }
        var deltaX = located.CenterX - anchor.Value.X;
        var deltaY = located.CenterY - anchor.Value.Y;
        var result = new List<SandboxInputEvent>(events.Count);
        foreach (var evt in events)
        {
            result.Add(evt.Type switch
            {
                SandboxInputEventType.Click or SandboxInputEventType.Move when evt.X is not null && evt.Y is not null =>
                    evt with { X = evt.X + deltaX, Y = evt.Y + deltaY },
                SandboxInputEventType.Click or SandboxInputEventType.Move =>
                    evt with { X = located.CenterX, Y = located.CenterY },
                _ => evt,
            });
        }
        return result;
    }

    private static (int X, int Y)? FindAnchor(IReadOnlyList<SandboxInputEvent> events)
    {
        foreach (var evt in events)
        {
            if (evt.X is int x && evt.Y is int y &&
                (evt.Type == SandboxInputEventType.Click || evt.Type == SandboxInputEventType.Move))
            {
                return (x, y);
            }
        }
        return null;
    }

    private static IReadOnlyList<SandboxInputEvent> CollapseToCentre(
        IReadOnlyList<SandboxInputEvent> events,
        LocatedTarget located)
    {
        var result = new List<SandboxInputEvent>(events.Count);
        foreach (var evt in events)
        {
            result.Add(evt.Type switch
            {
                SandboxInputEventType.Click or SandboxInputEventType.Move =>
                    evt with { X = located.CenterX, Y = located.CenterY },
                _ => evt,
            });
        }
        return result;
    }

    private static string? FirstKey(IReadOnlyList<SandboxInputEvent> events)
    {
        foreach (var e in events)
            if (e.Type == SandboxInputEventType.Key) return e.Key;
        return null;
    }

    private static string? FirstText(IReadOnlyList<SandboxInputEvent> events)
    {
        foreach (var e in events)
            if (e.Type == SandboxInputEventType.Type) return e.Text;
        return null;
    }
}
