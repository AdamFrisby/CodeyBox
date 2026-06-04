using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public interface IStartupRecoveryInputBarrier
{
    Task RecoveryInputReady { get; }
}

public interface IStartupRecoveryInputSink
{
    void MarkRecoveryInputReady();
}

public interface IInfrastructureDeferralScheduler
{
    void ScheduleInfrastructureDeferredRequeue(WorkItemId id, TimeSpan delay, CancellationToken stoppingToken = default);
}

public interface IStartupInitialRecoveryBarrier
{
    Task InitialRecoveryCompleted { get; }
}

public interface IStartupInitialRecoverySink
{
    void MarkInitialRecoveryCompleted();
}

public sealed class StartupRecoveryBarrier :
    IStartupRecoveryInputBarrier,
    IStartupRecoveryInputSink,
    IStartupInitialRecoveryBarrier,
    IStartupInitialRecoverySink
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
