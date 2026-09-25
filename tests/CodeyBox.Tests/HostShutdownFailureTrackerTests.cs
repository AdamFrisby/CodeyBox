using CodeyBox.Api;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Acceptance tests for host shutdown:
/// 1. Orderly stop exits cleanly without an unhandled ObjectDisposedException
///    when inspecting post-Run services.
/// 2. Background service fault maps to the non-zero exit code resolved by
///    <see cref="BackgroundServiceFailureTracker"/>.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class HostShutdownFailureTrackerTests : IDisposable
{
    private readonly TestSink _sink = new();

    public HostShutdownFailureTrackerTests()
    {
        Environment.ExitCode = 0;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose()
    {
        Environment.ExitCode = 0;
        Log.CloseAndFlush();
    }

    [Fact]
    public void NormalStop_ExitsCleanlyWithNoUnhandledException()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<BackgroundServiceFailureTracker>();
        builder.Services.AddHostedService<SelfStoppingBackgroundService>();

        var app = builder.Build();

        // Must exit cleanly without throwing ObjectDisposedException from post-Run service resolution
        var exception = Record.Exception(() => Program.RunHost(app));

        Assert.Null(exception);
        Assert.Equal(0, Environment.ExitCode);
        Assert.DoesNotContain(_sink.Events, e => e.Level >= LogEventLevel.Error);
    }

    [Fact]
    public void NormalStop_WithoutTrackerRegistered_ExitsCleanlyWithNoUnhandledException()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddHostedService<SelfStoppingBackgroundService>();

        var app = builder.Build();

        var exception = Record.Exception(() => Program.RunHost(app));

        Assert.Null(exception);
        Assert.Equal(0, Environment.ExitCode);
        Assert.DoesNotContain(_sink.Events, e => e.Level >= LogEventLevel.Error);
    }

    [Fact]
    public void BackgroundServiceFault_StopsHostAndSetsExitCodeToTrackerValue()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.Configure<HostOptions>(static o =>
        {
            o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
        });
        builder.Services.AddSingleton<BackgroundServiceFailureTracker>();
        builder.Services.AddHostedService<FaultingBackgroundService>();

        var app = builder.Build();

        var exception = Record.Exception(() => Program.RunHost(app));

        Assert.Null(exception);
        var expectedExitCode = BackgroundServiceFailureTracker.ResolveExitCode(backgroundServiceFaulted: true);
        Assert.Equal(expectedExitCode, Environment.ExitCode);
        Assert.NotEqual(0, Environment.ExitCode);

        // Verify the failure was logged as an error
        Assert.Contains(
            _sink.Events,
            e => e.Level == LogEventLevel.Error &&
                 e.MessageTemplate.Text.Contains("Host stopped after a background service fault"));
    }

    private sealed class SelfStoppingBackgroundService(IHostApplicationLifetime lifetime) : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            lifetime.ApplicationStarted.Register(() => lifetime.StopApplication());
            return Task.CompletedTask;
        }
    }

    private sealed class FaultingBackgroundService(
        BackgroundServiceFailureTracker failureTracker,
        IHostApplicationLifetime lifetime) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var tcs = new TaskCompletionSource();
            using var reg = lifetime.ApplicationStarted.Register(() => tcs.SetResult());
            await tcs.Task;

            var fault = new InvalidOperationException("Simulated background service fault");
            failureTracker.ReportFailure(fault);
            throw fault;
        }
    }
}
