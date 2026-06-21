// Keep the large main suite below host-wide CPU fan-out; several fixtures fork
// git/dotnet child processes and have crashed the testhost under audit load.
[assembly: Xunit.CollectionBehavior(MaxParallelThreads = 4)]
