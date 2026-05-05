using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that when AllowAgentQuestions=false (the default), the pipeline
/// ignores <codeybox-question> blocks completely and proceeds normally.
/// </summary>
[Collection("Pipeline integration")]
public sealed class OptOutTests : IDisposable
{
    private readonly string _workspace;
    public OptOutTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-optout-").FullName;
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public async Task WorkPrompt_WhenOptOut_DoesNotContainQuestionProtocol()
    {
        // BuildInitialWorkPrompt with allowAgentQuestions=false should not contain the protocol.
        var prompt = PipelineRunner.BuildInitialWorkPrompt("Do the thing.", allowAgentQuestions: false);
        Assert.DoesNotContain("codeybox-question", prompt);
        Assert.DoesNotContain("escalate ambiguity", prompt);
    }

    [Fact]
    public void WorkPrompt_WhenOptIn_ContainsQuestionProtocol()
    {
        var prompt = PipelineRunner.BuildInitialWorkPrompt("Do the thing.", allowAgentQuestions: true);
        Assert.Contains("codeybox-question", prompt);
        Assert.Contains("continue working with your default", prompt);
    }

    [Fact]
    public async Task PipelineWithOptOut_AgentEmitsQuestion_CompletesNormally()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        // ScriptedAgent writes a file (no question block in its stdout).
        // Even if it did emit a question, with AllowAgentQuestions=false (default) it's ignored.
        tp.Agent.WorkPlan.Enqueue(new FileWrite("opt-out-test.txt", "content\n"));

        var item = new CodeyBox.Core.WorkItem
        {
            Id = CodeyBox.Core.WorkItemId.New(),
            ProjectId = new CodeyBox.Core.ProjectId("test-project"),
            Title = "Opt-out test",
            Prompt = "do something",
        };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, System.Threading.CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(CodeyBox.Core.WorkItemState.Done, final!.State);
    }
}
