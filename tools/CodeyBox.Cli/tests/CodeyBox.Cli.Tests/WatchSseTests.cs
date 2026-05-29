using CodeyBox.Cli.Commands;
using CodeyBox.Cli.Services;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class WatchSseTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        TimeSpan? sseTimeout = null) =>
        SseTestHttp.MakeFactory(handler, sseTimeout);

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
    public async Task Watch_SseEvents404_FallsBackToPolling()
    {
        var pollCount = 0;
        var factory = MakeFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);

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
    public async Task Watch_SseEvents404_PollingNotFound_ExitsWithError()
    {
        var factory = MakeFactory(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "watch", "missing-id"],
                factory);

            Assert.Equal(1, code);
            Assert.Contains("SSE unavailable", output.Error.ToString());
            Assert.Contains("not found", output.Error.ToString());
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
    public async Task Watch_StreamFlag_PrintsNoteAndContinuesWatching()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "watch", "aabbccdd-0000-0000-0000-000000000000", "--stream"],
                MakeFactory(req =>
                {
                    if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
                        return SampleData.SseEventsResponse("Done");

                    return SampleData.WorkItemResponse();
                }));

            Assert.Equal(0, code);
            Assert.Contains("not implemented", output.Error.ToString());
            Assert.Contains("Done", output.Out.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Watch_SseRequest_SendsAcceptEventStreamHeader()
    {
        string? acceptHeader = null;
        var factory = MakeFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
            {
                acceptHeader = string.Join(
                    ", ",
                    req.Headers.Accept.Select(h => h.ToString()));
                return SampleData.SseEventsResponse("Done");
            }

            return SampleData.WorkItemResponse();
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        try
        {
            await CliApp.InvokeAsync(
                ["queue", "watch", "aabbccdd-0000-0000-0000-000000000000"],
                factory);

            Assert.Contains("text/event-stream", acceptHeader);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task TryWatchWorkItemEventsAsync_ConnectTimeout_ReturnsShouldFallback()
    {
        var config = new ResolvedConfig
        {
            ApiBaseUrl = "http://localhost:5036",
            ApiKey = "test-key",
        };
        var sse = new HttpClient(new SseTestHttp.NeverCompletesHandler())
        {
            BaseAddress = new Uri(config.ApiBaseUrl),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var http = new HttpClient(new FakeHttpMessageHandler(_ => SampleData.WorkItemResponse()))
        {
            BaseAddress = new Uri(config.ApiBaseUrl),
        };
        var client = new CodeyBoxClient(http, sse);

        var result = await client.TryWatchWorkItemEventsAsync(
            "aabbccdd-0000-0000-0000-000000000000",
            _ => { });

        Assert.Equal(SseWatchResult.ShouldFallback, result);
    }

    [Fact]
    public async Task TryWatchWorkItemEventsAsync_ReadTimeout_ReturnsShouldFallback()
    {
        var config = new ResolvedConfig
        {
            ApiBaseUrl = "http://localhost:5036",
            ApiKey = "test-key",
        };
        var client = MakeFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StreamContent(new SseTestHttp.TimeoutOnSecondReadStream("Working")),
                };
            }

            return SampleData.WorkItemResponse();
        })(config);

        var result = await client.TryWatchWorkItemEventsAsync(
            "aabbccdd-0000-0000-0000-000000000000",
            _ => { });

        Assert.Equal(SseWatchResult.ShouldFallback, result);
    }

    [Fact]
    public async Task TryWatchWorkItemEventsAsync_SlowConnect_RequiresInfiniteSseTimeout()
    {
        // Regression guard: production uses a dedicated _sseHttp with infinite timeout.
        var config = new ResolvedConfig
        {
            ApiBaseUrl = "http://localhost:5036",
            ApiKey = "test-key",
        };
        var handler = new SseTestHttp.DelayingEventsHandler(
            TimeSpan.FromMilliseconds(150),
            req => req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true
                ? SampleData.SseEventsResponse("Done")
                : SampleData.WorkItemResponse());
        var baseUri = new Uri(config.ApiBaseUrl);

        var shortSseClient = new CodeyBoxClient(
            new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) },
            new HttpClient(handler)
            {
                BaseAddress = baseUri,
                Timeout = TimeSpan.FromMilliseconds(50),
            });

        var infiniteSseClient = new CodeyBoxClient(
            new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) },
            new HttpClient(handler) { BaseAddress = baseUri, Timeout = Timeout.InfiniteTimeSpan });

        var shortResult = await shortSseClient.TryWatchWorkItemEventsAsync(
            "aabbccdd-0000-0000-0000-000000000000", _ => { });
        var infiniteResult = await infiniteSseClient.TryWatchWorkItemEventsAsync(
            "aabbccdd-0000-0000-0000-000000000000", _ => { });

        Assert.Equal(SseWatchResult.ShouldFallback, shortResult);
        Assert.Equal(SseWatchResult.Completed, infiniteResult);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("AuditFailed")]
    [InlineData("MergeConflictResolutionFailed")]
    [InlineData("AbandonedAfterRecoveryAttempts")]
    public async Task Watch_SseStopsOnEachTerminalState(string terminalState)
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "watch", "aabbccdd-0000-0000-0000-000000000000"],
                MakeFactory(req =>
                {
                    if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
                        return SampleData.SseEventsResponse("Working", terminalState, "Queued");

                    return SampleData.WorkItemResponse();
                }));

            Assert.Equal(0, code);
            var stdout = output.Out.ToString();
            Assert.Contains(terminalState, stdout);
            Assert.DoesNotContain("Queued", stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Watch_CancellationDuringSse_ExitsZeroWithoutFallbackNote()
    {
        using var cts = new CancellationTokenSource();
        var factory = MakeFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StreamContent(new SseTestHttp.BlockUntilCancelledStream()),
                };
            }

            return SampleData.WorkItemResponse(SampleData.WorkItem("Done"));
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var invokeTask = CliApp.InvokeAsync(
                ["queue", "watch", "aabbccdd-0000-0000-0000-000000000000"],
                factory,
                cts.Token);
            await Task.Delay(100);
            cts.Cancel();
            var code = await invokeTask;

            Assert.Equal(0, code);
            Assert.DoesNotContain("SSE unavailable", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Watch_SseUnauthorized_DoesNotPrintFallbackNote()
    {
        var factory = MakeFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
                return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);

            return SampleData.WorkItemResponse();
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "watch", "aabbccdd-0000-0000-0000-000000000000"],
                factory);

            Assert.Equal(1, code);
            Assert.DoesNotContain("SSE unavailable", output.Error.ToString());
            Assert.Contains("401", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Watch_SseFallback_DoesNotReprintLastSseState()
    {
        var pollStates = new[] { "Working", "Done" };
        var pollIndex = 0;
        var factory = MakeFactory(req =>
        {
            if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
                return SampleData.SseEventsResponse("Working");

            var state = pollStates[Math.Min(pollIndex++, pollStates.Length - 1)];
            return SampleData.WorkItemResponse(SampleData.WorkItem(state));
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
            var workingLines = output.Out.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Count(l => l.Contains("Working"));
            Assert.Equal(1, workingLines);
            Assert.Contains("Done", output.Out.ToString());
        }
        finally
        {
            QueueWatch.PollingInterval = TimeSpan.FromSeconds(2);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Watch_SseMergedThenDone_ContinuesUntilTerminal()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "watch", "aabbccdd-0000-0000-0000-000000000000"],
                MakeFactory(req =>
                {
                    if (req.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
                        return SampleData.SseEventsResponse("Working", "Merged", "Done");

                    return SampleData.WorkItemResponse();
                }));

            Assert.Equal(0, code);
            var stdout = output.Out.ToString();
            Assert.Contains("Merged", stdout);
            Assert.Contains("Done", stdout);
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
