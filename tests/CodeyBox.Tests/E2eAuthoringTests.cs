using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.ExploratoryTesting;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Cheap-model CUA authoring: explore a real fixture UI, emit a deterministic
/// e2e-replay artifact, and verify the replay runtime re-runs green with no
/// model in the loop.
/// </summary>
public sealed class E2eAuthoringTests
{
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
  {
    WriteIndented = true,
  };

  [Fact]
  public void SelectorResolver_prefers_explicit_selector_from_accessibility_tree()
  {
    var tree = """
      {"nodes":[{"role":"button","name":"Log in","selector":"#login-btn"}]}
      """;
    var descriptor = new TraceAccessibilityDescriptor { Role = "button", Name = "Log in" };

    var selector = E2eSelectorResolver.Resolve(descriptor, tree);

    Assert.Equal("#login-btn", selector);
  }

  [Fact]
  public void Emitter_converts_session_trace_into_replay_artifact()
  {
    var trace = new SessionTrace
    {
      TraceFormatVersion = SessionTrace.CurrentVersion,
      Modality = "web-graphical",
      StartedAt = DateTimeOffset.Parse("2026-07-06T08:00:00Z"),
      TargetName = "demo-login",
      EntryUrl = "http://app.local/",
      Entries =
      [
        new TraceEntry
        {
          Sequence = 1,
          Timestamp = DateTimeOffset.Parse("2026-07-06T08:00:01Z"),
          Action = new TraceAction
          {
            Kind = "click",
            InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Click, X = 120, Y = 116 }],
            TargetDescriptor = new TraceTargetDescriptor
            {
              Accessibility = new TraceAccessibilityDescriptor { Role = "textbox", Name = "Email", ElementType = "css:#email" },
              Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 1, Height = 1 } },
            },
          },
          Observation = new TraceObservation
          {
            AccessibilitySnapshotJson = """{"nodes":[{"role":"textbox","name":"Email","selector":"#email"}]}""",
            CapturedAt = DateTimeOffset.Parse("2026-07-06T08:00:01Z"),
          },
        },
        new TraceEntry
        {
          Sequence = 2,
          Timestamp = DateTimeOffset.Parse("2026-07-06T08:00:02Z"),
          Action = new TraceAction
          {
            Kind = "type",
            InputEvents = [new SandboxInputEvent { Type = SandboxInputEventType.Type, Text = "alice@example.com" }],
            TargetDescriptor = new TraceTargetDescriptor
            {
              Accessibility = new TraceAccessibilityDescriptor { Role = "textbox", Name = "Email", ElementType = "css:#email" },
              Visual = new TraceVisualDescriptor { Region = new TraceBoundingRegion { X = 0, Y = 0, Width = 1, Height = 1 } },
            },
          },
          Observation = new TraceObservation
          {
            CapturedAt = DateTimeOffset.Parse("2026-07-06T08:00:02Z"),
          },
        },
      ],
    };

    var artifact = E2eReplayArtifactEmitter.EmitFromTrace(
      trace,
      [new E2eReplayAssertion { Kind = "selectorVisible", Selector = "#welcome" }],
      new E2eReplayEmitOptions { Name = "demo-login-happy-path" });

    Assert.Equal("demo-login-happy-path", artifact.Name);
    Assert.Equal(3, artifact.Steps.Count);
    Assert.Equal("navigate", artifact.Steps[0].Action);
    Assert.Equal("http://app.local/", artifact.Steps[0].Target);
    Assert.Equal("click", artifact.Steps[1].Action);
    Assert.Equal("#email", artifact.Steps[1].Selector);
    Assert.Equal("fill", artifact.Steps[2].Action);
    Assert.Equal("#email", artifact.Steps[2].Selector);
    Assert.Equal("alice@example.com", artifact.Steps[2].Value);
  }

  [Fact]
  public async Task CheapModelCua_author_emit_replay_green_for_demo_login()
  {
    await using var cuaSandbox = new E2eAuthoring.DemoLoginCuaSandbox();
    await using var session = E2eAuthoring.DemoLoginExploration.CreateSession(cuaSandbox);

    var author = new CheapModelCuaAuthor(new CheapModelCuaAuthorOptions
    {
      ModelId = "claude-haiku-4-5-20251001",
    });
    var explorer = new ScriptedE2eCuaExplorer();

    var result = await author.ExploreAndEmitAsync(session, explorer, E2eAuthoring.DemoLoginExploration.Plan());

    Assert.Equal("claude-haiku-4-5-20251001", result.AuthorModelId);
    Assert.False(string.IsNullOrWhiteSpace(result.Artifact.Name));
    Assert.Equal(6, result.Artifact.Steps.Count);
    Assert.Contains(result.Artifact.Steps, s => s.Action == "fill" && s.Selector == "#email");
    Assert.Contains(result.Artifact.Steps, s => s.Action == "fill" && s.Selector == "#password");
    Assert.Contains(result.Artifact.Steps, s => s.Action == "click" && s.Selector == "#login-btn");
    Assert.Equal(3, result.Artifact.Assertions.Count);
    Assert.True(E2eReplayArtifactValidation.TryValidate(result.Artifact, out _, out var detail), detail);

    await using var replaySandbox = new E2eAuthoring.DemoLoginReplaySandbox();
    var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);
    var replay = await runtime.ExecuteAsync(result.Artifact, replaySandbox);

    Assert.True(replay.Passed, replay.Summary);
    Assert.Null(replay.FailedStepIndex);
    Assert.Equal(6, replay.StepResults.Count);
    Assert.Equal(3, replay.AssertionResults.Count);
    Assert.All(replay.AssertionResults, assertion => Assert.True(assertion.Passed, assertion.Detail));

    if (string.Equals(Environment.GetEnvironmentVariable("WRITE_E2E_FIXTURES"), "1", StringComparison.Ordinal))
      await WriteCommittedFixturesAsync(result);
  }

  private static async Task WriteCommittedFixturesAsync(E2eAuthoringResult result)
  {
    var fixtureDir = Path.GetFullPath(Path.Combine(
      AppContext.BaseDirectory,
      "..", "..", "..",
      "Fixtures",
      "E2eReplay"));
    Directory.CreateDirectory(fixtureDir);

    var artifactJson = JsonSerializer.Serialize(result.Artifact, JsonOptions);
    await File.WriteAllTextAsync(Path.Combine(fixtureDir, "demo-login-happy-path.artifact.json"), artifactJson);

    var testCase = E2eReplayArtifactEmitter.BuildTestCase(
      E2eAuthoring.DemoLoginExploration.TestCaseId,
      "Demo login happy path",
      "Cheap-model CUA explores the demo login fixture and emits a deterministic replay artifact.",
      E2eAuthoring.DemoLoginExploration.WorkItemId,
      result.Trace,
      E2eAuthoring.DemoLoginExploration.Plan().Assertions,
      E2eAuthoring.DemoLoginExploration.Plan().EmitOptions,
      """{"capability":"demo-login","mustPassOn":"main"}""",
      "demo-login");
    var testcaseJson = JsonSerializer.Serialize(new
    {
      id = testCase.Id,
      name = testCase.Name,
      description = testCase.Description,
      sourceWorkItemId = testCase.SourceWorkItemId,
      automationKind = "E2eReplay",
      executableArtifactJson = testCase.ExecutableArtifactJson,
      conformanceJson = testCase.ConformanceJson,
      label = testCase.Label,
    }, JsonOptions);
    await File.WriteAllTextAsync(Path.Combine(fixtureDir, "demo-login-happy-path.testcase.json"), testcaseJson);
  }

  [Fact]
  public async Task Committed_fixture_artifact_replays_green_without_model()
  {
    var fixturePath = Path.Combine(
      AppContext.BaseDirectory,
      "Fixtures",
      "E2eReplay",
      "demo-login-happy-path.artifact.json");
    Assert.True(File.Exists(fixturePath), $"missing committed fixture at {fixturePath}");

    var json = await File.ReadAllTextAsync(fixturePath);
    var artifact = JsonSerializer.Deserialize<E2eReplayArtifact>(json, JsonOptions)
      ?? throw new InvalidOperationException("fixture artifact did not deserialize");

    Assert.True(E2eReplayArtifactValidation.TryValidate(artifact, out _, out var detail), detail);

    await using var replaySandbox = new E2eAuthoring.DemoLoginReplaySandbox();
    var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);
    var replay = await runtime.ExecuteAsync(artifact, replaySandbox);

    Assert.True(replay.Passed, replay.Summary);
  }

  [Fact]
  public void Committed_test_case_fixture_links_to_source_work_item()
  {
    var fixturePath = Path.Combine(
      AppContext.BaseDirectory,
      "Fixtures",
      "E2eReplay",
      "demo-login-happy-path.testcase.json");
    Assert.True(File.Exists(fixturePath), $"missing committed test case at {fixturePath}");

    var json = File.ReadAllText(fixturePath);
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    Assert.Equal(E2eAuthoring.DemoLoginExploration.WorkItemId, root.GetProperty("sourceWorkItemId").GetString());
    Assert.Equal("E2eReplay", root.GetProperty("automationKind").GetString());
    Assert.Equal(E2eAuthoring.DemoLoginExploration.TestCaseId, root.GetProperty("id").GetString());
    Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("executableArtifactJson").GetString()));
    Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("conformanceJson").GetString()));
  }
}
