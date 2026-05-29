using CodeyBox.Cli.Commands;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class WatchSseTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });

    [Fact]
    public async Task Watch_Default_UsesSseAndPrintsStateTransitions()
    {
        var factory = MakeFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
                return SampleData.SseEventsResponse("Queued", "Working", "Done");

            return SampleData.WorkItemResponse(SampleData.WorkItem("Done"));
        });

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
    public async Task Watch_SseConnectError_FallsBackToPolling()
    {
        var pollCount = 0;
        var factory = MakeFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
                throw new HttpRequestException("connection refused");

            pollCount++;
            return SampleData.WorkItemResponse(SampleData.WorkItem("Done"));
        });

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
    public async Task Watch_SseUnavailable_FallsBackToPolling()
    {
        var pollCount = 0;
        var factory = MakeFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
                return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);

            pollCount++;
            return SampleData.WorkItemResponse(SampleData.WorkItem("Done"));
        });

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
    public async Task Watch_SseNotFound_ExitsWithoutPollingFallback()
    {
        var pollAttempted = false;
        var factory = MakeFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);

            pollAttempted = true;
            return SampleData.WorkItemResponse(SampleData.WorkItem("Done"));
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "watch", "aabbccdd-0000-0000-0000-000000000000"],
                factory);

            Assert.Equal(1, code);
            Assert.Contains("not found", output.Error.ToString());
            Assert.DoesNotContain("SSE unavailable", output.Error.ToString());
            Assert.False(pollAttempted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Watch_SseEndsWithoutTerminal_FallsBackToPolling()
    {
        var pollCount = 0;
        var factory = MakeFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
                return SampleData.SseEventsResponse("Working");

            pollCount++;
            return SampleData.WorkItemResponse(SampleData.WorkItem("Done"));
        });

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
            Assert.True(pollCount >= 1);
            Assert.Contains("Done", output.Out.ToString());
        }
        finally
        {
            QueueWatch.PollingInterval = TimeSpan.FromSeconds(2);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Watch_SseStopsAtTerminalBeforeExtraEvents()
    {
        var factory = MakeFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
                return SampleData.SseEventsResponse("Working", "Done", "Failed");

            return SampleData.WorkItemResponse(SampleData.WorkItem("Done"));
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "watch", "aabbccdd-0000-0000-0000-000000000000"],
                factory);

            Assert.Equal(0, code);
            var stdout = output.Out.ToString();
            Assert.Contains("Done", stdout);
            Assert.DoesNotContain("Failed", stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Watch_SseDeduplicatesStateLines()
    {
        var factory = MakeFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
                return SampleData.SseEventsResponse("Working", "Working", "Done");

            return SampleData.WorkItemResponse(SampleData.WorkItem("Done"));
        });

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
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Watch_SseSkipsMalformedDataLines()
    {
        var factory = MakeFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
                return SampleData.SseEventsResponse(
                    "raw:{not-json",
                    "raw:{\"wrongShape\":true}",
                    "raw:{\"workItem\":{}}",
                    "Working",
                    "Done");

            return SampleData.WorkItemResponse(SampleData.WorkItem("Done"));
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "watch", "aabbccdd-0000-0000-0000-000000000000"],
                factory);

            Assert.Equal(0, code);
            Assert.Contains("Working", output.Out.ToString());
            Assert.Contains("Done", output.Out.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Watch_Poll_NotFound_ExitsWithError()
    {
        var factory = MakeFactory(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "watch", "missing-id", "--poll"],
                factory);

            Assert.Equal(1, code);
            Assert.Contains("not found", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Watch_StreamFlag_ReturnsError()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "watch", "aabbccdd-0000-0000-0000-000000000000", "--stream"],
                MakeFactory(_ => SampleData.WorkItemResponse()));

            Assert.Equal(1, code);
            Assert.Contains("not implemented", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Watch_Poll_SkipsSseEndpoint()
    {
        var sseAttempted = false;
        var factory = MakeFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
            {
                sseAttempted = true;
                return SampleData.SseEventsResponse("Done");
            }

            return SampleData.WorkItemResponse(SampleData.WorkItem("Done"));
        });

        QueueWatch.PollingInterval = TimeSpan.Zero;
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "watch", "aabbccdd-0000-0000-0000-000000000000", "--poll"],
                factory);

            Assert.Equal(0, code);
            Assert.False(sseAttempted);
            Assert.DoesNotContain("SSE unavailable", output.Error.ToString());
            Assert.Contains("Done", output.Out.ToString());
        }
        finally
        {
            QueueWatch.PollingInterval = TimeSpan.FromSeconds(2);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }
}
