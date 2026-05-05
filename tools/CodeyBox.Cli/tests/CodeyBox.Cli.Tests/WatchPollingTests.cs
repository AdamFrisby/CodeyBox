using CodeyBox.Cli.Commands;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class WatchPollingTests
{
    [Fact]
    public async Task Watch_PrintsEachStateTransition()
    {
        var states = new[] { "Queued", "Working", "Auditing", "Done" };
        var callIndex = 0;

        Func<ResolvedConfig, CodeyBoxClient> factory = config =>
            new CodeyBoxClient(
                new HttpClient(new FakeHttpMessageHandler(_ =>
                {
                    var state = states[Math.Min(callIndex++, states.Length - 1)];
                    return SampleData.WorkItemResponse(SampleData.WorkItem(state));
                }))
                { BaseAddress = new Uri(config.ApiBaseUrl) });

        QueueWatch.PollingInterval = TimeSpan.Zero;
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "watch", "aabbccdd-0000-0000-0000-000000000000"],
                factory);

            Assert.Equal(0, code);
            var stdout = output.Out.ToString();
            Assert.Contains("Queued", stdout);
            Assert.Contains("Working", stdout);
            Assert.Contains("Auditing", stdout);
            Assert.Contains("Done", stdout);
        }
        finally
        {
            QueueWatch.PollingInterval = TimeSpan.FromSeconds(2);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Watch_StopsAtTerminalState()
    {
        var callCount = 0;
        Func<ResolvedConfig, CodeyBoxClient> factory = config =>
            new CodeyBoxClient(
                new HttpClient(new FakeHttpMessageHandler(_ =>
                {
                    callCount++;
                    return SampleData.WorkItemResponse(SampleData.WorkItem("Done"));
                }))
                { BaseAddress = new Uri(config.ApiBaseUrl) });

        QueueWatch.PollingInterval = TimeSpan.Zero;
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            await CliApp.InvokeAsync(
                ["queue", "watch", "aabbccdd-0000-0000-0000-000000000000"],
                factory);

            Assert.Equal(1, callCount);
        }
        finally
        {
            QueueWatch.PollingInterval = TimeSpan.FromSeconds(2);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Watch_DeduplicatesStateLines()
    {
        var states = new[] { "Working", "Working", "Done" };
        var callIndex = 0;
        Func<ResolvedConfig, CodeyBoxClient> factory = config =>
            new CodeyBoxClient(
                new HttpClient(new FakeHttpMessageHandler(_ =>
                {
                    var state = states[Math.Min(callIndex++, states.Length - 1)];
                    return SampleData.WorkItemResponse(SampleData.WorkItem(state));
                }))
                { BaseAddress = new Uri(config.ApiBaseUrl) });

        QueueWatch.PollingInterval = TimeSpan.Zero;
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            await CliApp.InvokeAsync(
                ["queue", "watch", "aabbccdd-0000-0000-0000-000000000000"],
                factory);

            var workingLines = output.Out.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Count(l => l.Contains("Working"));
            Assert.Equal(1, workingLines);
        }
        finally
        {
            QueueWatch.PollingInterval = TimeSpan.FromSeconds(2);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }
}
