namespace CodeyBox.Orchestrator;

public interface IStartupRecoveryBarrier
{
    Task RecoveryInputReady { get; }
    Task InitialRecoveryCompleted { get; }
}

public interface IStartupRecoveryCompletionSink
{
    void MarkRecoveryInputReady();
    void MarkInitialRecoveryCompleted();
}

public sealed class StartupRecoveryBarrier : IStartupRecoveryBarrier, IStartupRecoveryCompletionSink
{
    private readonly TaskCompletionSource _recoveryInputReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _initialRecoveryCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task RecoveryInputReady => _recoveryInputReady.Task;
    public Task InitialRecoveryCompleted => _initialRecoveryCompleted.Task;

    public void MarkRecoveryInputReady() => _recoveryInputReady.TrySetResult();
    public void MarkInitialRecoveryCompleted() => _initialRecoveryCompleted.TrySetResult();
}
