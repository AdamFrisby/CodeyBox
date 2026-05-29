using CodeyBox.Cli.Commands;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class WatchSseTests
{
    [Fact]
    public async Task Watch_Default_UsesSseAndPrintsStateTransitions()
    {
        Func<ResolvedConfig, CodeyBoxClient> factory = config =>
            new CodeyBoxClient(
                new HttpClient(new FakeHttpMessageHandler(req =>
                {
                    if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
                        return SampleData.SseEventsResponse("Queued", "Working", "Done");

                    return SampleData.WorkItemResponse(SampleData.WorkItem("Done"));
                }))
                { BaseAddress = new Uri(config.ApiBaseUrl) });

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
            Assert.Contains("Done", stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Watch_SseUnavailable_FallsBackToPolling()
    {
        var pollCount = 0;
        Func<ResolvedConfig, CodeyBoxClient> factory = config =>
            new CodeyBoxClient(
                new HttpClient(new FakeHttpMessageHandler(req =>
                {
                    if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
                        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);

                    pollCount++;
                    return SampleData.WorkItemResponse(SampleData.WorkItem("Done"));
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
            Assert.Contains("SSE unavailable", output.Error.ToString());
            Assert.Equal(1, pollCount);
            Assert.Contains("Done", output.Out.ToString());
        }
        finally
        {
            QueueWatch.PollingInterval = TimeSpan.FromSeconds(2);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Watch_Poll_SkipsSseEndpoint()
    {
        var sseAttempted = false;
        Func<ResolvedConfig, CodeyBoxClient> factory = config =>
            new CodeyBoxClient(
                new HttpClient(new FakeHttpMessageHandler(req =>
                {
                    if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
                    {
                        sseAttempted = true;
                        return SampleData.SseEventsResponse("Done");
                    }

                    return SampleData.WorkItemResponse(SampleData.WorkItem("Done"));
                }))
                { BaseAddress = new Uri(config.ApiBaseUrl) });

        QueueWatch.PollingInterval = TimeSpan.Zero;
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            await CliApp.InvokeAsync(
                ["queue", "watch", "aabbccdd-0000-0000-0000-000000000000", "--poll"],
                factory);

            Assert.False(sseAttempted);
            Assert.DoesNotContain("SSE unavailable", output.Error.ToString());
        }
        finally
        {
            QueueWatch.PollingInterval = TimeSpan.FromSeconds(2);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }
}
