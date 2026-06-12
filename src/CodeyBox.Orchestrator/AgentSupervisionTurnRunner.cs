using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal static class AgentSupervisionTurnRunner
{
    public static async Task<AgentResult> RunAutonomousAndQueuedInjectionsAsync(
        IAgentRunner runner,
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId,
        string? reasoningMode,
        IAgentSupervisionSession supervision,
        Action<string>? stdoutCallback,
        bool captureStructuredStream,
        Func<string, CancellationToken, Task<string>>? promptPreprocessor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(supervision);

        var supervisedStdout = supervision.WrapStdoutCallback(stdoutCallback);
        await supervision.PublishCodeyBoxCommandAsync("autonomous", prompt, injectionId: null, ct)
            .ConfigureAwait(false);

        if (runner is ISessionAgentRunner sessionRunner)
        {
            var shielded = new NonDisposingSandbox(sandbox);
            var handle = await sessionRunner.OpenSessionAsync(
                    shielded,
                    workingDirectory,
                    credential,
                    modelId,
                    reasoningMode,
                    ct)
                .ConfigureAwait(false);
            try
            {
                var result = await sessionRunner.SendTurnAsync(
                        handle,
                        prompt,
                        ct,
                        supervisedStdout,
                        captureStructuredStream)
                    .ConfigureAwait(false);

                return await supervision.RunPendingInjectionsAsync(
                        result,
                        async (turn, turnCt) =>
                        {
                            var turnPrompt = promptPreprocessor is null
                                ? turn.Prompt
                                : await promptPreprocessor(turn.Prompt, turnCt).ConfigureAwait(false);
                            return await sessionRunner.SendTurnAsync(
                                    handle,
                                    turnPrompt,
                                    turnCt,
                                    supervisedStdout,
                                    captureStructuredStream)
                                .ConfigureAwait(false);
                        },
                        ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await sessionRunner.CloseSessionAsync(handle, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // The pipeline owns the sandbox lifecycle. Session teardown
                    // failures after a completed/failed turn should not mask the
                    // turn result or dispose the caller-owned sandbox.
                }
            }
        }

        var agentResult = await runner.RunAsync(
                sandbox,
                workingDirectory,
                prompt,
                credential,
                modelId,
                reasoningMode,
                ct,
                stdoutChunkCallback: supervisedStdout,
                captureStructuredStream)
            .ConfigureAwait(false);

        var dispatcher = new SupervisedTurnDispatcher(
            runner,
            sandbox,
            workingDirectory,
            credential,
            modelId,
            reasoningMode,
            supervisedStdout,
            captureStructuredStream,
            promptPreprocessor);
        return await supervision.RunPendingInjectionsAsync(
                agentResult,
                dispatcher.RunInjectionTurnAsync,
                ct)
            .ConfigureAwait(false);
    }
}
