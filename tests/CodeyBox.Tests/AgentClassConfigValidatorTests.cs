using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeyBox.Api;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="AgentClassConfigValidator"/> — the startup hosted
/// service that cross-checks AgentClass ModelId values against each
/// provider's live model list.
/// </summary>
public sealed class AgentClassConfigValidatorTests
{
    [Fact]
    public async Task ValidModelId_NoWarnings()
    {
        var probe = new StubModelListProbe(AgentKind.Claude,
            AgentModelListResult.Success(new[] { "claude-haiku-4-5", "claude-opus-4-7", "claude-sonnet-4-6" }));
        var capture = new ModelListCapturingLogger();
        var validator = BuildValidator(
            new[] { ClassWithMember("frontier", "claude", "claude-opus-4-7") },
            new[] { probe },
            failOnUnknown: false,
            capture);

        await validator.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(capture.Records, r => r.Level == LogLevel.Warning);
        Assert.Contains(capture.Records, r => r.Level == LogLevel.Information &&
            r.Message.Contains("validated against provider"));
    }

    [Fact]
    public async Task UnknownModelId_LogsExactlyOneWarning_ListingValidIds()
    {
        var probe = new StubModelListProbe(AgentKind.Claude,
            AgentModelListResult.Success(new[] { "a", "b", "c" }));
        var capture = new ModelListCapturingLogger();
        var validator = BuildValidator(
            new[] { ClassWithMember("frontier", "claude", "d") },
            new[] { probe },
            failOnUnknown: false,
            capture);

        await validator.StartAsync(CancellationToken.None);

        var warnings = capture.Records.Where(r => r.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        var msg = warnings[0].Message;
        Assert.Contains("'frontier'", msg);
        Assert.Contains("claude", msg);
        Assert.Contains("d", msg);
        Assert.Contains("a,b,c", msg);
    }

    [Fact]
    public async Task FailOnUnknownModel_ThrowsAtStart()
    {
        var probe = new StubModelListProbe(AgentKind.Claude,
            AgentModelListResult.Success(new[] { "a", "b", "c" }));
        var validator = BuildValidator(
            new[] { ClassWithMember("frontier", "claude", "d") },
            new[] { probe },
            failOnUnknown: true,
            new ModelListCapturingLogger());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.StartAsync(CancellationToken.None));
        Assert.Contains("frontier", ex.Message);
        Assert.Contains("claude", ex.Message);
        Assert.Contains("/d", ex.Message);
    }

    [Fact]
    public async Task ProbeThrows_LogsWarning_HostStarts()
    {
        var probe = new ThrowingModelListProbe(AgentKind.Claude, new HttpRequestException("network down"));
        var capture = new ModelListCapturingLogger();
        var validator = BuildValidator(
            new[] { ClassWithMember("frontier", "claude", "claude-opus-4-7") },
            new[] { probe },
            failOnUnknown: false,
            capture);

        await validator.StartAsync(CancellationToken.None);

        var warnings = capture.Records.Where(r => r.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains("Could not validate claude", warnings[0].Message);
    }

    [Fact]
    public async Task ProbeReturnsEmptyList_LogsWarning_SkipsValidation()
    {
        var probe = new StubModelListProbe(AgentKind.Claude,
            AgentModelListResult.Success(Array.Empty<string>()));
        var capture = new ModelListCapturingLogger();
        var validator = BuildValidator(
            new[] { ClassWithMember("frontier", "claude", "claude-opus-4-7") },
            new[] { probe },
            failOnUnknown: true, // even with throw-on-unknown, empty list is a skip
            capture);

        await validator.StartAsync(CancellationToken.None);

        var warnings = capture.Records.Where(r => r.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains("returned no models", warnings[0].Message);
    }

    [Fact]
    public async Task ProbeReturnsFailure_LogsWarning_HostStarts()
    {
        var probe = new StubModelListProbe(AgentKind.Claude,
            AgentModelListResult.Failed("HTTP 401"));
        var capture = new ModelListCapturingLogger();
        var validator = BuildValidator(
            new[] { ClassWithMember("frontier", "claude", "claude-opus-4-7") },
            new[] { probe },
            failOnUnknown: false,
            capture);

        await validator.StartAsync(CancellationToken.None);

        var warnings = capture.Records.Where(r => r.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains("HTTP 401", warnings[0].Message);
    }

    [Fact]
    public async Task MultipleClasses_PartialOverlap_OneWarningPerInvalid()
    {
        var probe = new StubModelListProbe(AgentKind.Claude,
            AgentModelListResult.Success(new[] { "good-1", "good-2" }));
        var capture = new ModelListCapturingLogger();

        // Two classes; two members reference an unknown id, one references a valid id.
        var classes = new[]
        {
            new AgentClassOptions
            {
                Id = "alpha",
                Members =
                {
                    new AgentMembershipOptions { Agent = "claude", QualityScore = 100, ModelId = "good-1" },
                    new AgentMembershipOptions { Agent = "claude", QualityScore = 100, ModelId = "typo-1" },
                },
            },
            new AgentClassOptions
            {
                Id = "beta",
                Members =
                {
                    new AgentMembershipOptions { Agent = "claude", QualityScore = 100, ModelId = "typo-2" },
                },
            },
        };

        var validator = BuildValidator(classes, new[] { probe }, failOnUnknown: false, capture);
        await validator.StartAsync(CancellationToken.None);

        var unknownWarnings = capture.Records
            .Where(r => r.Level == LogLevel.Warning && r.Message.Contains("NOT in provider model list"))
            .ToList();
        Assert.Equal(2, unknownWarnings.Count);
        Assert.Contains(unknownWarnings, w => w.Message.Contains("typo-1") && w.Message.Contains("'alpha'"));
        Assert.Contains(unknownWarnings, w => w.Message.Contains("typo-2") && w.Message.Contains("'beta'"));
    }

    [Fact]
    public async Task MemberWithoutModelId_NotValidated()
    {
        // The probe will throw if invoked — assert the validator never calls it
        // when no member declares a ModelId.
        var probe = new ThrowingModelListProbe(AgentKind.Claude, new InvalidOperationException("should not invoke"));
        var capture = new ModelListCapturingLogger();

        var classes = new[]
        {
            new AgentClassOptions
            {
                Id = "alpha",
                Members =
                {
                    new AgentMembershipOptions { Agent = "claude", QualityScore = 100, ModelId = null },
                },
            },
        };

        var validator = BuildValidator(classes, new[] { probe }, failOnUnknown: false, capture);
        await validator.StartAsync(CancellationToken.None);

        Assert.Equal(0, probe.CallCount);
        Assert.DoesNotContain(capture.Records, r => r.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task NoProbeRegisteredForAgent_LogsSkipWarning()
    {
        var capture = new ModelListCapturingLogger();
        var validator = BuildValidator(
            new[] { ClassWithMember("frontier", "gemini", "gemini-3.1-flash-lite") },
            Array.Empty<IAgentModelListProbe>(),
            failOnUnknown: false,
            capture);

        await validator.StartAsync(CancellationToken.None);

        var warnings = capture.Records.Where(r => r.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains("No IAgentModelListProbe registered", warnings[0].Message);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static AgentClassOptions ClassWithMember(string classId, string agent, string modelId) =>
        new()
        {
            Id = classId,
            Members =
            {
                new AgentMembershipOptions
                {
                    Agent = agent,
                    QualityScore = 100,
                    ModelId = modelId,
                },
            },
        };

    private static AgentClassConfigValidator BuildValidator(
        IEnumerable<AgentClassOptions> classes,
        IEnumerable<IAgentModelListProbe> probes,
        bool failOnUnknown,
        ModelListCapturingLogger capture)
    {
        var opts = new CodeyBoxOptions
        {
            AgentClasses = classes.ToList(),
            ConfigValidation = new ConfigValidationOptions { FailOnUnknownModel = failOnUnknown },
        };
        return new AgentClassConfigValidator(
            Options.Create(opts),
            probes,
            new ModelListCapturingLoggerAdapter<AgentClassConfigValidator>(capture));
    }
}

internal sealed class StubModelListProbe : IAgentModelListProbe
{
    private readonly AgentModelListResult _result;
    public StubModelListProbe(AgentKind kind, AgentModelListResult result)
    {
        Kind = kind;
        _result = result;
    }

    public AgentKind Kind { get; }
    public int CallCount { get; private set; }

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
    {
        CallCount++;
        return Task.FromResult(_result);
    }
}

internal sealed class ThrowingModelListProbe : IAgentModelListProbe
{
    private readonly Exception _ex;
    public ThrowingModelListProbe(AgentKind kind, Exception ex)
    {
        Kind = kind;
        _ex = ex;
    }

    public AgentKind Kind { get; }
    public int CallCount { get; private set; }

    public Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
    {
        CallCount++;
        throw _ex;
    }
}

internal sealed class ModelListCapturingLogger
{
    public List<(LogLevel Level, string Message)> Records { get; } = new();
    public void Log(LogLevel level, string message) => Records.Add((level, message));
}

internal sealed class ModelListCapturingLoggerAdapter<T> : ILogger<T>
{
    private readonly ModelListCapturingLogger _sink;
    public ModelListCapturingLoggerAdapter(ModelListCapturingLogger sink) { _sink = sink; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _sink.Log(level, formatter(state, exception));
    }
}
