// Keep the large main suite below host-wide CPU fan-out; several fixtures fork
// git/dotnet/incus child processes whose own CPU demand stacks on top of the
// xUnit worker threads. Audit hosts are small (2 logical cores), so a
// four-thread pool oversubscribed the box ~2x and starved the timing- and
// subprocess-sensitive fixtures (wall-clock deadlines, background timers, child
// signal delivery) into spurious timeouts and testhost aborts under full-suite
// load. Two workers matches the host core count and leaves the forked children
// room to run, which the tests need to observe their real outcomes.
[assembly: Xunit.CollectionBehavior(MaxParallelThreads = 2)]
