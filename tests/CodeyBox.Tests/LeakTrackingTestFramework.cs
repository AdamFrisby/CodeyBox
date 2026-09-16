using System.Collections.Concurrent;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework("CodeyBox.Tests.LeakTrackingTestFramework", "CodeyBox.Tests")]

namespace CodeyBox.Tests;

public sealed class LeakTrackingTestFramework : XunitTestFramework
{
    public LeakTrackingTestFramework(IMessageSink messageSink)
        : base(messageSink)
    {
    }

    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName)
        => new LeakTrackingTestFrameworkExecutor(
            assemblyName,
            SourceInformationProvider,
            DiagnosticMessageSink);

    /// <summary>
    /// Bounds every await the harness performs on test execution so a test that
    /// stops making progress fails with a diagnostic naming what was pending
    /// instead of hanging the whole assembly run (which consumes a pipeline
    /// slot until a human kills it). Timeouts are configured via environment
    /// variables (seconds; 0 disables that guard); see
    /// <see cref="TestRunGuard"/> for names and defaults.
    /// </summary>
    internal static class TestRunGuard
    {
        internal const string CaseTimeoutEnvironmentVariable = "CODEYBOX_TESTS_CASE_TIMEOUT_SECONDS";
        internal const string RunTimeoutEnvironmentVariable = "CODEYBOX_TESTS_RUN_TIMEOUT_SECONDS";
        internal const string RunStallTimeoutEnvironmentVariable = "CODEYBOX_TESTS_RUN_STALL_TIMEOUT_SECONDS";

        private const int DefaultCaseTimeoutSeconds = 600;
        // The main suite runs strictly serially (see XunitAssemblyConfig: no
        // parallelization on the 2-core audit hosts, by design, to keep
        // wall-clock-sensitive fixtures deterministic); ~12k cases execute
        // strictly one at a time and need well over 40 minutes in Debug.
        // Serial wall-clock therefore grows with every added test: ~6,200 tests
        // need ~25 min in Debug on an unloaded host even when failures
        // short-circuit real pipeline work, and a healthy audited run now
        // reaches past 40 minutes while still making progress — the old
        // budget axed a run whose sole in-flight test had started a fraction
        // of a second earlier, and shared-host load variance trips a 40 min
        // budget on runs that are slow but progressing (the stall guard below
        // never fires for those). Raise the overall budget with headroom
        // (90 minutes). Hang detection does not rely on this budget: a single
        // stuck case still fails via the per-case timeout, and a wedged run
        // (no completions) still fails via the stall timeout, both at
        // 10 minutes, so this stays a backstop, not hang detection.
        private const int DefaultRunTimeoutSeconds = 5400;
        private const int DefaultRunStallTimeoutSeconds = 600;
        private const int MaxNamedPendingTests = 5;

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

        private static readonly ConcurrentDictionary<object, InFlightTest> InFlight = new();
        private static readonly object ProgressLock = new();
        private static DateTimeOffset _lastProgressUtc = DateTimeOffset.UtcNow;

        internal static TimeSpan CaseTimeout { get; } = ResolveTimeout(CaseTimeoutEnvironmentVariable, DefaultCaseTimeoutSeconds);
        internal static TimeSpan RunTimeout { get; } = ResolveTimeout(RunTimeoutEnvironmentVariable, DefaultRunTimeoutSeconds);
        internal static TimeSpan RunStallTimeout { get; } = ResolveTimeout(RunStallTimeoutEnvironmentVariable, DefaultRunStallTimeoutSeconds);

        internal static void RunStarted()
        {
            InFlight.Clear();
            lock (ProgressLock)
                _lastProgressUtc = DateTimeOffset.UtcNow;
        }

        internal static void Enter(object key, string displayName)
        {
            InFlight[key] = new InFlightTest(displayName, DateTimeOffset.UtcNow);
        }

        internal static void Exit(object key)
        {
            InFlight.TryRemove(key, out _);
            lock (ProgressLock)
                _lastProgressUtc = DateTimeOffset.UtcNow;
        }

        internal static void WaitForCompletion(Task runTask)
        {
            var runDeadline = RunTimeout == Timeout.InfiniteTimeSpan
                ? DateTimeOffset.MaxValue
                : DateTimeOffset.UtcNow + RunTimeout;
            var waitHandle = ((IAsyncResult)runTask).AsyncWaitHandle;

            while (!waitHandle.WaitOne(PollInterval))
            {
                var now = DateTimeOffset.UtcNow;
                DateTimeOffset lastProgress;
                lock (ProgressLock)
                    lastProgress = _lastProgressUtc;

                if (now >= runDeadline)
                    throw new TimeoutException(BuildMessage("overall run timeout", RunTimeout, now));

                if (RunStallTimeout != Timeout.InfiniteTimeSpan && now - lastProgress >= RunStallTimeout)
                    throw new TimeoutException(BuildMessage("progress stall timeout", RunStallTimeout, now));
            }
        }

        internal static string BuildCaseTimeoutMessage(string displayName, TimeSpan timeout)
            => $"CodeyBox test harness: test case timed out after {timeout} without completing: {displayName}. "
                + $"The test awaited something that was never signalled. Failing the test instead of hanging the run. "
                + $"Tune with {CaseTimeoutEnvironmentVariable} (seconds, 0 disables).";

        private static string BuildMessage(string kind, TimeSpan timeout, DateTimeOffset now)
        {
            var pending = InFlight.Values
                .OrderBy(p => p.StartedAtUtc)
                .Take(MaxNamedPendingTests)
                .Select(p => $"{p.DisplayName} (waiting {(now - p.StartedAtUtc):g})")
                .ToArray();
            var pendingText = pending.Length == 0
                ? "no test cases tracked as in-flight"
                : $"{InFlight.Count} still in-flight: {string.Join("; ", pending)}"
                    + (InFlight.Count > MaxNamedPendingTests
                        ? $"; and {InFlight.Count - MaxNamedPendingTests} more"
                        : string.Empty);
            return $"CodeyBox test harness: {kind} of {timeout} elapsed with no run completion; "
                + $"failing the run instead of hanging indefinitely. Pending: {pendingText}. "
                + $"Tune with {RunTimeoutEnvironmentVariable}/{RunStallTimeoutEnvironmentVariable} (seconds, 0 disables).";
        }

        private static TimeSpan ResolveTimeout(string variable, int defaultSeconds)
        {
            var raw = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(raw))
                return TimeSpan.FromSeconds(defaultSeconds);
            if (int.TryParse(raw.Trim(), out var seconds) && seconds <= 0)
                return Timeout.InfiniteTimeSpan;
            if (int.TryParse(raw.Trim(), out seconds) && seconds > 0)
                return TimeSpan.FromSeconds(seconds);
            return TimeSpan.FromSeconds(defaultSeconds);
        }

        internal static void ObserveInBackground(Task task)
        {
            task.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private sealed record InFlightTest(string DisplayName, DateTimeOffset StartedAtUtc);
    }

    private sealed class LeakTrackingTestFrameworkExecutor : XunitTestFrameworkExecutor
    {
        public LeakTrackingTestFrameworkExecutor(
            AssemblyName assemblyName,
            ISourceInformationProvider sourceInformationProvider,
            IMessageSink diagnosticMessageSink)
            : base(assemblyName, sourceInformationProvider, diagnosticMessageSink)
        {
        }

        protected override void RunTestCases(
            IEnumerable<IXunitTestCase> testCases,
            IMessageSink executionMessageSink,
            ITestFrameworkExecutionOptions executionOptions)
        {
            using var runner = new LeakTrackingTestAssemblyRunner(
                TestAssembly,
                testCases,
                DiagnosticMessageSink,
                executionMessageSink,
                executionOptions);
            TestRunGuard.RunStarted();
            var runTask = runner.RunAsync();
            try
            {
                TestRunGuard.WaitForCompletion(runTask);
            }
            catch (TimeoutException ex)
            {
                try
                {
                    executionMessageSink.OnMessage(new DiagnosticMessage(ex.Message));
                }
                catch (InvalidOperationException)
                {
                }

                try
                {
                    Console.Error.WriteLine(ex.Message);
                }
                catch (IOException)
                {
                }

                TestRunGuard.ObserveInBackground(runTask);
                throw;
            }

            runTask.GetAwaiter().GetResult();
        }
    }

    private sealed class LeakTrackingTestAssemblyRunner : XunitTestAssemblyRunner
    {
        public LeakTrackingTestAssemblyRunner(
            ITestAssembly testAssembly,
            IEnumerable<IXunitTestCase> testCases,
            IMessageSink diagnosticMessageSink,
            IMessageSink executionMessageSink,
            ITestFrameworkExecutionOptions executionOptions)
            : base(testAssembly, testCases, diagnosticMessageSink, executionMessageSink, executionOptions)
        {
        }

        protected override Task<RunSummary> RunTestCollectionAsync(
            IMessageBus messageBus,
            ITestCollection testCollection,
            IEnumerable<IXunitTestCase> testCases,
            CancellationTokenSource cancellationTokenSource)
            => new LeakTrackingTestCollectionRunner(
                testCollection,
                testCases,
                DiagnosticMessageSink,
                messageBus,
                TestCaseOrderer,
                new ExceptionAggregator(Aggregator),
                cancellationTokenSource).RunAsync();
    }

    private sealed class LeakTrackingTestCollectionRunner : XunitTestCollectionRunner
    {
        public LeakTrackingTestCollectionRunner(
            ITestCollection testCollection,
            IEnumerable<IXunitTestCase> testCases,
            IMessageSink diagnosticMessageSink,
            IMessageBus messageBus,
            ITestCaseOrderer testCaseOrderer,
            ExceptionAggregator aggregator,
            CancellationTokenSource cancellationTokenSource)
            : base(testCollection, testCases, diagnosticMessageSink, messageBus, testCaseOrderer, aggregator, cancellationTokenSource)
        {
        }

        protected override Task<RunSummary> RunTestClassAsync(
            ITestClass testClass,
            IReflectionTypeInfo @class,
            IEnumerable<IXunitTestCase> testCases)
            => new LeakTrackingTestClassRunner(
                testClass,
                @class,
                testCases,
                DiagnosticMessageSink,
                MessageBus,
                TestCaseOrderer,
                new ExceptionAggregator(Aggregator),
                CancellationTokenSource,
                CollectionFixtureMappings).RunAsync();
    }

    private sealed class LeakTrackingTestClassRunner : XunitTestClassRunner
    {
        public LeakTrackingTestClassRunner(
            ITestClass testClass,
            IReflectionTypeInfo @class,
            IEnumerable<IXunitTestCase> testCases,
            IMessageSink diagnosticMessageSink,
            IMessageBus messageBus,
            ITestCaseOrderer testCaseOrderer,
            ExceptionAggregator aggregator,
            CancellationTokenSource cancellationTokenSource,
            IDictionary<Type, object> collectionFixtureMappings)
            : base(
                testClass,
                @class,
                testCases,
                diagnosticMessageSink,
                messageBus,
                testCaseOrderer,
                aggregator,
                cancellationTokenSource,
                collectionFixtureMappings)
        {
        }

        protected override Task<RunSummary> RunTestMethodAsync(
            ITestMethod testMethod,
            IReflectionMethodInfo method,
            IEnumerable<IXunitTestCase> testCases,
            object[] constructorArguments)
            => new LeakTrackingTestMethodRunner(
                testMethod,
                Class,
                method,
                testCases,
                DiagnosticMessageSink,
                MessageBus,
                new ExceptionAggregator(Aggregator),
                CancellationTokenSource,
                constructorArguments).RunAsync();
    }

    private sealed class LeakTrackingTestMethodRunner : XunitTestMethodRunner
    {
        public LeakTrackingTestMethodRunner(
            ITestMethod testMethod,
            IReflectionTypeInfo @class,
            IReflectionMethodInfo method,
            IEnumerable<IXunitTestCase> testCases,
            IMessageSink diagnosticMessageSink,
            IMessageBus messageBus,
            ExceptionAggregator aggregator,
            CancellationTokenSource cancellationTokenSource,
            object[] constructorArguments)
            : base(
                testMethod,
                @class,
                method,
                testCases,
                diagnosticMessageSink,
                messageBus,
                aggregator,
                cancellationTokenSource,
                constructorArguments)
        {
        }

        protected override async Task<RunSummary> RunTestCaseAsync(IXunitTestCase testCase)
        {
            var scope = TestFileSystemWatcherLeakTracker.BeginTestCase(testCase.DisplayName);
            TestRunGuard.Enter(testCase, testCase.DisplayName);
            try
            {
                var testTask = base.RunTestCaseAsync(testCase);
                var timeout = TestRunGuard.CaseTimeout;
                if (timeout == Timeout.InfiniteTimeSpan)
                    return await testTask.ConfigureAwait(false);

                using var delayCts = new CancellationTokenSource();
                var completed = await Task.WhenAny(
                    testTask,
                    Task.Delay(timeout, delayCts.Token)).ConfigureAwait(false);
                if (ReferenceEquals(completed, testTask))
                {
                    await delayCts.CancelAsync().ConfigureAwait(false);
                    return await testTask.ConfigureAwait(false);
                }

                var message = TestRunGuard.BuildCaseTimeoutMessage(testCase.DisplayName, timeout);
                try
                {
                    MessageBus.QueueMessage(new DiagnosticMessage(message));
                }
                catch (InvalidOperationException)
                {
                }

                try
                {
                    Console.Error.WriteLine(message);
                }
                catch (IOException)
                {
                }

                TestRunGuard.ObserveInBackground(testTask);
                throw new TimeoutException(message);
            }
            finally
            {
                TestRunGuard.Exit(testCase);
                try
                {
                    scope.ReportLeaks(line =>
                    {
                        MessageBus.QueueMessage(new DiagnosticMessage(line));
                        Console.Error.WriteLine(line);
                    });
                }
                finally
                {
                    scope.Dispose();
                }
            }
        }
    }
}
