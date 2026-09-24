using CodeyBox.Core;

namespace CodeyBox.Agents.Unreal;

/// <summary>
/// In-VM smoke check for Unreal Labs' unreal-agent:
/// <list type="number">
/// <item><c>unreal-agent-runner -h</c> — binary present and runnable on PATH.</item>
/// <item>When credentials are present, a minimal stdin prompt dispatch step
/// exercising the JSON-on-stdin request channel without polluting any repo workspace.</item>
/// </list>
/// </summary>
public sealed class UnrealInVmSmokeProbe : IInVmSmokeProbe
{
    public AgentKind Kind => AgentKind.Unreal;

    public IReadOnlyList<InVmSmokeStep> BuildSteps(AgentCredential? credential)
    {
        var steps = new List<InVmSmokeStep>
        {
            new(
                [UnrealAgentRunner.DefaultBinary, "-h"],
                FailureHint: "unreal-agent-runner binary not runnable on sandbox PATH")
        };

        if (credential is not null)
        {
            steps.Add(new(
                [
                    UnrealAgentRunner.DefaultBinary,
                    "-workspace", "/tmp",
                    "-log-directory", "/tmp",
                    "-session-directory", "/tmp"
                ],
                Stdin: UnrealAgentRunner.FormatRequestJson("ping", model: null, reasoningMode: null),
                FailureHint: "unreal-agent-runner prompt dispatch failed"));
        }

        return steps;
    }
}
