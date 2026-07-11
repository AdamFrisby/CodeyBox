using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.ExploratoryTesting;
using CodeyBox.Sandbox.Graphical;
using CodeyBox.Tests.E2eAuthoring;

namespace CodeyBox.Tests;

public sealed class E2eReplayArtifactEmitterTests
{
    [Theory]
    [InlineData("scroll")]
    [InlineData("move")]
    [InlineData("screenshot")]
    public void EmitFromTrace_skips_non_replay_action_kinds(string kind)
    {
        var trace = BuildTraceWithAction(kind);
        var artifact = E2eReplayArtifactEmitter.EmitFromTrace(
            trace,
            [new E2eReplayAssertion { Kind = "titleContains", Value = "ok" }]);
        Assert.Empty(artifact.Steps);
    }

    [Fact]
    public void EmitFromTrace_emits_double_click_as_replay_step()
    {
        var trace = BuildTraceWithAction("double_click", selector: "#menu");
        var artifact = E2eReplayArtifactEmitter.EmitFromTrace(
            trace,
            [new E2eReplayAssertion { Kind = "titleContains", Value = "ok" }]);
        var step = Assert.Single(artifact.Steps);
        Assert.Equal("doubleClick", step.Action);
        Assert.Equal("#menu", step.Selector);
    }

    [Fact]
    public void EmitFromTrace_emits_press_for_key_action()
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
                        Kind = "click",
                        InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Click, X = 1, Y = 1 }],
                        TargetDescriptor = new TraceTargetDescriptor
                        {
                            Accessibility = new TraceAccessibilityDescriptor { Role = "textbox", Name = "Email", ElementType = "css:#email" },
                            Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 1, Height = 1 } },
                        },
                    },
                    Observation = new TraceObservation
                    {
                        AccessibilitySnapshotJson = """{"nodes":[{"role":"textbox","name":"Email","selector":"#email"}]}""",
                        CapturedAt = DateTimeOffset.UtcNow,
                    },
                },
                new TraceEntry
                {
                    Sequence = 2,
                    Timestamp = DateTimeOffset.UtcNow,
                    Action = new TraceAction
                    {
                        Kind = "key",
                        InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = "Enter" }],
                        TargetDescriptor = new TraceTargetDescriptor
                        {
                            Accessibility = new TraceAccessibilityDescriptor { Role = "textbox", Name = "Email", ElementType = "css:#email" },
                            Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 1, Height = 1 } },
                        },
                    },
                    Observation = new TraceObservation { CapturedAt = DateTimeOffset.UtcNow },
                },
            ],
        };

        var artifact = E2eReplayArtifactEmitter.EmitFromTrace(
            trace,
            [new E2eReplayAssertion { Kind = "titleContains", Value = "ok" }]);
        var press = Assert.Single(artifact.Steps, step => step.Action == "press");
        Assert.Equal("#email", press.Selector);
        Assert.Equal("Enter", press.Value);
    }

    [Fact]
    public void BuildDefaultReadiness_uses_entry_origin_for_root_urls()
    {
        var trace = new SessionTrace
        {
            TraceFormatVersion = SessionTrace.CurrentVersion,
            Modality = "web-graphical",
            StartedAt = DateTimeOffset.UtcNow,
            EntryUrl = "http://app.local/",
            Entries = [],
        };

        var artifact = E2eReplayArtifactEmitter.EmitFromTrace(
            trace,
            [new E2eReplayAssertion { Kind = "titleContains", Value = "ok" }]);
        Assert.NotNull(artifact.Readiness);
        Assert.Equal("http://app.local/healthz", artifact.Readiness!.Url);
    }

    [Fact]
    public void EmitFromTrace_throws_when_selector_unresolvable_for_click()
    {
        var trace = BuildTraceWithAction("click", includeSelector: false);
        var ex = Assert.Throws<InvalidOperationException>(() => E2eReplayArtifactEmitter.EmitFromTrace(trace, []));
        Assert.Contains("Could not resolve selector", ex.Message);
    }

    [Fact]
    public void EmitFromTrace_throws_for_unsupported_action()
    {
        var trace = BuildTraceWithAction("drag");
        Assert.Throws<NotSupportedException>(() => E2eReplayArtifactEmitter.EmitFromTrace(trace, []));
    }

    [Fact]
    public void EmitJson_serializes_validated_artifact()
    {
        var trace = new SessionTrace
        {
            TraceFormatVersion = SessionTrace.CurrentVersion,
            Modality = "web-graphical",
            StartedAt = DateTimeOffset.UtcNow,
            EntryUrl = "http://app.local/",
            Entries = [],
        };

        var json = E2eReplayArtifactEmitter.EmitJson(
            trace,
            [new E2eReplayAssertion { Kind = "titleContains", Value = "ok" }]);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("http://app.local/healthz", doc.RootElement.GetProperty("readiness").GetProperty("url").GetString());
    }

    [Fact]
    public void BuildTestCase_links_automation_kind_and_embedded_artifact()
    {
        var trace = new SessionTrace
        {
            TraceFormatVersion = SessionTrace.CurrentVersion,
            Modality = "web-graphical",
            StartedAt = DateTimeOffset.UtcNow,
            EntryUrl = "http://app.local/",
            Entries = [],
        };

        var testCase = E2eReplayArtifactEmitter.BuildTestCase(
            "tc-1",
            "name",
            "description",
            "work-item",
            trace,
            [new E2eReplayAssertion { Kind = "titleContains", Value = "Dashboard" }],
            new E2eReplayEmitOptions { Name = "artifact-name" });

        Assert.Equal("tc-1", testCase.Id);
        Assert.Equal(AutomationKind.E2eReplay, testCase.AutomationKind);
        Assert.Equal("work-item", testCase.SourceWorkItemId);
        Assert.False(string.IsNullOrWhiteSpace(testCase.ExecutableArtifactJson));

        var artifact = JsonSerializer.Deserialize<E2eReplayArtifact>(testCase.ExecutableArtifactJson);
        Assert.NotNull(artifact);
        Assert.Equal("artifact-name", artifact!.Name);
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

    [Fact]
    public void SelectorResolver_returns_null_for_null_descriptor()
    {
        Assert.Null(E2eSelectorResolver.Resolve(null, "{}"));
    }

    [Fact]
    public void SelectorResolver_uses_css_prefix_from_element_type()
    {
        var selector = E2eSelectorResolver.Resolve(
            new TraceAccessibilityDescriptor { Role = "button", Name = "Go", ElementType = "css:#go" },
            "{}");
        Assert.Equal("#go", selector);
    }

    [Fact]
    public void SelectorResolver_emits_playwright_role_locator_for_role_name_fallback()
    {
        var selector = E2eSelectorResolver.Resolve(
            new TraceAccessibilityDescriptor { Role = "button", Name = "Log \"in\"" },
            "{}");
        Assert.Equal("role=button[name=\"Log \\\"in\\\"\"]", selector);
    }

    [Fact]
    public void SelectorResolver_ignores_malformed_accessibility_tree()
    {
        var selector = E2eSelectorResolver.Resolve(
            new TraceAccessibilityDescriptor { Role = "button", Name = "Go", ElementType = "css:#go" },
            "{not-json");
        Assert.Equal("#go", selector);
    }

    [Fact]
    public void SelectorResolver_uses_role_only_match_when_name_missing()
    {
        var tree = """{"nodes":[{"role":"button","name":"Other","selector":"#other"}]}""";
        var selector = E2eSelectorResolver.Resolve(
            new TraceAccessibilityDescriptor { Role = "button", Name = "Save" },
            tree);
        Assert.Equal("#other", selector);
    }

    [Fact]
    public async Task ScriptedE2eCuaExplorer_throws_for_unknown_action()
    {
        var explorer = new ScriptedE2eCuaExplorer();
        var plan = new E2eExplorationPlan
        {
            TargetName = "demo",
            Actions = [new E2eExplorationAction { Kind = "drag" }],
            Assertions = [],
        };

        await Assert.ThrowsAsync<NotSupportedException>(() => explorer.ExploreAsync(
            new E2eAuthoring.DemoLoginCuaSandbox(),
            new RecordingComputerUseBridge(new ComputerUseBridge()),
            plan));
    }

    [Theory]
    [InlineData("claude-opus-4-7")]   // frontier denylist fragment ("opus")
    [InlineData("gpt-5.5")]           // frontier denylist fragment ("gpt-5")
    [InlineData("composer-2.5")]      // frontier denylist fragment ("composer")
    public void CheapModelCuaAuthor_rejects_frontier_model_ids(string modelId)
    {
        Assert.Throws<ArgumentException>(() => new CheapModelCuaAuthor(new CheapModelCuaAuthorOptions
        {
            ModelId = modelId,
        }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CheapModelAllowlist_rejects_missing_model_id(string? modelId)
    {
        // Path 1: null/whitespace model id.
        Assert.Throws<ArgumentException>(() => CheapModelAllowlist.EnsureCheap(modelId));
    }

    [Theory]
    [InlineData("gpt-4o")]                 // unknown, not frontier-denylisted, no haiku/flash
    [InlineData("some-unknown-model")]     // arbitrary id that must not slip through
    [InlineData("gemini-3-pro-preview")]   // capable Gemini id without the "flash" cheap marker
    public void CheapModelAllowlist_rejects_unknown_non_cheap_model_id(string modelId)
    {
        // Path 3: the catch-all reject — neither allow-listed nor haiku/flash.
        Assert.Throws<ArgumentException>(() => CheapModelAllowlist.EnsureCheap(modelId));
    }

    [Theory]
    [InlineData("claude-haiku-4-5-20251001")]  // explicit allowlist entry
    [InlineData("gemini-3-flash-preview")]      // explicit allowlist entry
    [InlineData("some-future-haiku-mini")]      // accepted via the "haiku" cheap marker
    [InlineData("vendor-flash-lite")]           // accepted via the "flash" cheap marker
    public void CheapModelAllowlist_accepts_cheap_model_ids(string modelId)
    {
        CheapModelAllowlist.EnsureCheap(modelId);
    }

    [Fact]
    public void AnthropicComputerUseModelClient_parses_tool_use_actions()
    {
        const string response = """
            {
              "content": [
                {
                  "type": "tool_use",
                  "input": { "action": "left_click", "coordinate": [10, 20] }
                },
                {
                  "type": "tool_use",
                  "input": { "action": "type", "text": "hello" }
                }
              ]
            }
            """;

        var actions = AnthropicComputerUseModelClient.ParseToolUses(response);
        Assert.Equal(2, actions.Count);
        Assert.Equal("click", actions[0].Action);
        Assert.Equal(10, actions[0].X);
        Assert.Equal(20, actions[0].Y);
        Assert.Equal("type", actions[1].Action);
        Assert.Equal("hello", actions[1].Text);
    }

    [Fact]
    public void AnthropicComputerUseModelClient_parses_screenshot_key_and_click_alias_actions()
    {
        const string response = """
            {
              "content": [
                { "type": "tool_use", "input": { "action": "screenshot" } },
                { "type": "tool_use", "input": { "action": "click", "coordinate": [3, 4] } },
                { "type": "tool_use", "input": { "action": "key", "text": "Enter" } },
                { "type": "tool_use", "input": { "action": "left_click" } }
              ]
            }
            """;

        var actions = AnthropicComputerUseModelClient.ParseToolUses(response);
        Assert.Equal(4, actions.Count);
        Assert.Equal("screenshot", actions[0].Action);
        Assert.Equal("click", actions[1].Action);
        Assert.Equal(3, actions[1].X);
        Assert.Equal(4, actions[1].Y);
        Assert.Equal("key", actions[2].Action);
        Assert.Equal("Enter", actions[2].Key);
        Assert.Equal("click", actions[3].Action);
        Assert.Equal(0, actions[3].X);
        Assert.Equal(0, actions[3].Y);
    }

    [Fact]
    public void EmitFromTrace_redacts_password_key_press_values()
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
                        Kind = "click",
                        InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Click, X = 1, Y = 1 }],
                        TargetDescriptor = new TraceTargetDescriptor
                        {
                            Accessibility = new TraceAccessibilityDescriptor { Role = "textbox", Name = "Password", ElementType = "css:#password" },
                            Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 1, Height = 1 } },
                        },
                    },
                    Observation = new TraceObservation
                    {
                        AccessibilitySnapshotJson = """{"nodes":[{"role":"textbox","name":"Password","selector":"#password"}]}""",
                        CapturedAt = DateTimeOffset.UtcNow,
                    },
                },
                new TraceEntry
                {
                    Sequence = 2,
                    Timestamp = DateTimeOffset.UtcNow,
                    Action = new TraceAction
                    {
                        Kind = "key",
                        InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = "secret" }],
                        TargetDescriptor = new TraceTargetDescriptor
                        {
                            Accessibility = new TraceAccessibilityDescriptor { Role = "textbox", Name = "Password", ElementType = "css:#password" },
                            Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 1, Height = 1 } },
                        },
                    },
                    Observation = new TraceObservation { CapturedAt = DateTimeOffset.UtcNow },
                },
            ],
        };

        var artifact = E2eReplayArtifactEmitter.EmitFromTrace(trace, []);
        var press = Assert.Single(artifact.Steps, step => step.Action == "press");
        Assert.Equal(E2eReplaySensitiveValueRedaction.PasswordPlaceholder, press.Value);
    }

    private static SessionTrace BuildTraceWithAction(
        string kind,
        bool includeSelector = true,
        string selector = "#target")
    {
        var descriptor = includeSelector
            ? new TraceAccessibilityDescriptor { Role = "button", Name = "Go", ElementType = $"css:{selector}" }
            : new TraceAccessibilityDescriptor();

        return new SessionTrace
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
                        Kind = kind,
                        InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Click, X = 1, Y = 1 }],
                        TargetDescriptor = new TraceTargetDescriptor
                        {
                            Accessibility = descriptor,
                            Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 1, Height = 1 } },
                        },
                    },
                    Observation = new TraceObservation
                    {
                        AccessibilitySnapshotJson = includeSelector
                            ? $$"""{"nodes":[{"role":"button","name":"Go","selector":"{{selector}}"}]}"""
                            : "{}",
                        CapturedAt = DateTimeOffset.UtcNow,
                    },
                },
            ],
        };
    }

    [Fact]
    public void ResolveStepSecrets_resolves_press_and_fill_placeholders()
    {
        var steps = new List<E2eReplayStep>
        {
            new() { Action = "fill", Selector = "#password", Value = E2eReplaySensitiveValueRedaction.PasswordPlaceholder },
            new() { Action = "press", Selector = "#password", Value = E2eReplaySensitiveValueRedaction.PasswordPlaceholder },
            new() { Action = "click", Selector = "#login-btn" },
        };
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [E2eReplaySensitiveValueRedaction.PasswordPlaceholder] = "secret",
        };

        var resolved = E2eReplaySensitiveValueRedaction.ResolveStepSecrets(steps, secrets);

        Assert.Equal("secret", resolved[0].Value);
        Assert.Equal("secret", resolved[1].Value);
        Assert.Equal("#login-btn", resolved[2].Selector);
    }

    [Fact]
    public void ResolveStepSecrets_keeps_placeholder_when_secret_is_empty()
    {
        var steps = new List<E2eReplayStep>
        {
            new() { Action = "press", Selector = "#password", Value = E2eReplaySensitiveValueRedaction.PasswordPlaceholder },
        };
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [E2eReplaySensitiveValueRedaction.PasswordPlaceholder] = string.Empty,
        };

        var resolved = E2eReplaySensitiveValueRedaction.ResolveStepSecrets(steps, secrets);

        Assert.Equal(E2eReplaySensitiveValueRedaction.PasswordPlaceholder, Assert.Single(resolved).Value);
    }

    [Fact]
    public void ComputerUseAuthoringActionPolicy_rejects_disallowed_actions_and_keys()
    {
        var limits = new ComputerUseAuthoringLimits();

        Assert.Throws<InvalidOperationException>(() =>
            ComputerUseAuthoringActionPolicy.EnsureActionAllowed(
                new ComputerUseRequest { Action = "scroll", ScrollX = 1 },
                limits));

        Assert.Throws<InvalidOperationException>(() =>
            ComputerUseAuthoringActionPolicy.EnsureActionAllowed(
                new ComputerUseRequest { Action = "key", Key = "Control+Alt+t" },
                limits));
    }

    [Fact]
    public void AnthropicComputerUseModelClient_ParseToolUses_enforces_tool_use_cap()
    {
        const string response = """
            {
              "content": [
                { "type": "tool_use", "input": { "action": "screenshot" } },
                { "type": "tool_use", "input": { "action": "screenshot" } }
              ]
            }
            """;

        Assert.Throws<InvalidOperationException>(() => AnthropicComputerUseModelClient.ParseToolUses(response, maxToolUses: 1));
    }
}
