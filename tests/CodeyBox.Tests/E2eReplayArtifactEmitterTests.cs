using CodeyBox.Core;
using CodeyBox.ExploratoryTesting;

namespace CodeyBox.Tests;

public sealed class E2eReplayArtifactEmitterTests
{
    [Fact]
    public void EmitFromTrace_skips_non_replay_action_kinds()
    {
        var trace = new SessionTrace
        {
            TraceFormatVersion = SessionTrace.CurrentVersion,
            Modality = "web-graphical",
            StartedAt = DateTimeOffset.UtcNow,
            Entries =
            [
                new TraceEntry
                {
                    Sequence = 1,
                    Timestamp = DateTimeOffset.UtcNow,
                    Action = new TraceAction
                    {
                        Kind = "scroll",
                        InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Scroll, X = 0, Y = 1 }],
                        TargetDescriptor = new TraceTargetDescriptor
                        {
                            Visual = new TraceVisualDescriptor
                            {
                                Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 1, Height = 1 },
                            },
                        },
                    },
                    Observation = new TraceObservation { CapturedAt = DateTimeOffset.UtcNow },
                },
            ],
        };

        var artifact = E2eReplayArtifactEmitter.EmitFromTrace(trace, []);
        Assert.Empty(artifact.Steps);
    }

    [Theory]
    [InlineData("""{"nodes":[{"role":"button","name":"Save","testId":"save-btn"}]}""", "button", "Save", "[data-testid=\"save-btn\"]")]
    [InlineData("""{"nodes":[{"role":"textbox","name":"Email","id":"email"}]}""", "textbox", "Email", "#email")]
    public void SelectorResolver_maps_accessibility_tree_nodes_to_stable_selectors(
        string tree,
        string role,
        string name,
        string expected)
    {
        var selector = E2eSelectorResolver.Resolve(
            new TraceAccessibilityDescriptor { Role = role, Name = name },
            tree);

        Assert.Equal(expected, selector);
    }
}
