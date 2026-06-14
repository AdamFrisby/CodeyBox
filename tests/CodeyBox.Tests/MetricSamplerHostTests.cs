using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Behaviour tests for <see cref="MetricSamplerHost"/>: it drives every
/// registered <see cref="IMetricSampler"/> on its own loop, re-reads
/// <see cref="IMetricSampler.Enabled"/> and <see cref="IMetricSampler.Interval"/>
/// each tick, and survives a per-sampler exception.
/// </summary>
public sealed class MetricSamplerHostTests
{
    [Fact]
    public async Task NoSamplers_ExitsImmediately_NoException()
    {
        var host = new MetricSamplerHost(Array.Empty<IMetricSampler>(), NullLogger<MetricSamplerHost>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await host.StartAsync(cts.Token);
        await host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task EnabledSampler_IsInvoked_AfterFirstInterval()
    {
        var sampler = new RecordingSampler { Interval = TimeSpan.FromMilliseconds(10), Enabled = true };
        var host = new MetricSamplerHost([sampler], NullLogger<MetricSamplerHost>.Instance);

        using var cts = new CancellationTokenSource();
        await host.StartAsync(cts.Token);

        // Wait until at least one sample is recorded — bounded by a generous
        // wall-clock budget so a slow CI box doesn't flake.
        var observed = await WaitForAsync(() => sampler.SampleCount, c => c >= 1, TimeSpan.FromSeconds(5));
        Assert.True(observed >= 1, $"expected sampler to fire at least once, saw {observed}");

        cts.Cancel();
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DisabledSampler_DoesNotInvoke()
    {
        var sampler = new RecordingSampler { Interval = TimeSpan.FromMilliseconds(10), Enabled = false };
        var host = new MetricSamplerHost([sampler], NullLogger<MetricSamplerHost>.Instance);

        using var cts = new CancellationTokenSource();
        await host.StartAsync(cts.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(250), cts.Token);

        cts.Cancel();
        await host.StopAsync(CancellationToken.None);

        Assert.Equal(0, sampler.SampleCount);
    }

    [Fact]
    public async Task SamplerThrows_LoopContinues_OnNextTick()
    {
        var sampler = new RecordingSampler
        {
            Interval = TimeSpan.FromMilliseconds(10),
            Enabled = true,
            ThrowOnNextCall = true,
        };
        var host = new MetricSamplerHost([sampler], NullLogger<MetricSamplerHost>.Instance);

        using var cts = new CancellationTokenSource();
        await host.StartAsync(cts.Token);

        // First call throws; subsequent calls succeed. We assert that at least
        // 2 ticks fire so we know the loop wasn't killed by the exception.
        var observed = await WaitForAsync(() => sampler.SampleCount, c => c >= 2, TimeSpan.FromSeconds(5));
        Assert.True(observed >= 2, $"expected loop to survive throw, saw {observed} calls");

        cts.Cancel();
        await host.StopAsync(CancellationToken.None);
    }

    private static async Task<T> WaitForAsync<T>(Func<T> read, Func<T, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        T current = read();
        while (!predicate(current) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20);
            current = read();
        }
        return current;
    }

    private sealed class RecordingSampler : IMetricSampler
    {
        public string Kind => "test-sampler";
        public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(50);
        public bool Enabled { get; set; } = true;
        public bool ThrowOnNextCall { get; set; }
        private int _count;
        public int SampleCount => Volatile.Read(ref _count);

        public Task SampleOnceAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _count);
            if (ThrowOnNextCall)
            {
                ThrowOnNextCall = false;
                throw new InvalidOperationException("first call must blow up");
            }
            return Task.CompletedTask;
        }
    }
}
