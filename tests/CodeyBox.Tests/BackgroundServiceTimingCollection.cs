using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Groups tests that assert short wall-clock behavior of BackgroundService loops
/// and cancellation callbacks. Running these alone avoids suite-level threadpool
/// contention turning scheduler latency into false negatives.
/// </summary>
[CollectionDefinition("Background service timing", DisableParallelization = true)]
public sealed class BackgroundServiceTimingCollection
{
}
