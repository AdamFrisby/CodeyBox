using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.Projects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that referencing an unknown plugin ID in project config does not
/// fail the audit. The composer logs a warning and skips the missing entry,
/// allowing all other auditors (preset and custom) to run normally.
/// </summary>
public sealed class MissingPluginIdTests
{
    private sealed class FakeAgent : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public Task<AgentResult> RunAsync(ISandbox _, string __, string ___, AgentCredential? ____, string? _____ = null, string? ______ = null, CancellationToken _______ = default, Action<string>? ________ = null)
            => Task.FromResult(new AgentResult(true, "ok", null, null));
    }

    [CodeyBoxPlugin(
        id: "test.present-plugin",
        displayName: "Present Plugin",
        minHostApiVersion: "1.0")]
    private sealed class PresentPluginAuditor : IAuditor
    {
        public string Name => "test:present-plugin";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
            => Task.FromResult(new AuditResult(Passed: true, Findings: []));
    }

    [Fact]
    public void UnknownPluginId_IsSkipped_AuditContinues()
    {
        var present = new PresentPluginAuditor();
        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            [present],
            NullLogger<ProjectAuditorComposer>.Instance);

        var project = new Project
        {
            Id = new ProjectId("test"),
            DisplayName = "Test",
            RepositoryUrl = "https://example.com/r.git",
            Audit = new ProjectAudit
            {
                Custom =
                [
                    // Unknown plugin ID — not registered
                    new CustomAuditorDescriptor
                    {
                        Kind = "plugin",
                        PluginId = "nonexistent.plugin-that-does-not-exist",
                    },
                    // Known plugin ID — must still be included
                    new CustomAuditorDescriptor
                    {
                        Kind = "plugin",
                        PluginId = "test.present-plugin",
                    },
                ],
            },
        };

        var auditors = composer.Compose(project, new FakeAgent());

        // The missing one is skipped; the present one is included
        Assert.Single(auditors);
        Assert.Equal("test:present-plugin", auditors[0].Name);
    }

    [Fact]
    public void MissingPluginId_LogsWarning()
    {
        var logged = new List<(LogLevel Level, string Message)>();
        var logger = new CapturingLogger<ProjectAuditorComposer>(logged);

        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            [],
            logger);

        var project = new Project
        {
            Id = new ProjectId("warn-test"),
            DisplayName = "Warn Test",
            RepositoryUrl = "https://example.com/r.git",
            Audit = new ProjectAudit
            {
                Custom =
                [
                    new CustomAuditorDescriptor
                    {
                        Kind = "plugin",
                        PluginId = "missing.plugin",
                    },
                ],
            },
        };

        composer.Compose(project, new FakeAgent());

        Assert.Contains(logged, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("missing.plugin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmptyPluginId_LogsWarning_AndSkips()
    {
        var logged = new List<(LogLevel Level, string Message)>();
        var logger = new CapturingLogger<ProjectAuditorComposer>(logged);

        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            [],
            logger);

        var project = new Project
        {
            Id = new ProjectId("empty-id-test"),
            DisplayName = "Empty ID Test",
            RepositoryUrl = "https://example.com/r.git",
            Audit = new ProjectAudit
            {
                Custom =
                [
                    // Kind=plugin but no PluginId set
                    new CustomAuditorDescriptor { Kind = "plugin" },
                ],
            },
        };

        var auditors = composer.Compose(project, new FakeAgent());

        Assert.Empty(auditors);
        Assert.Contains(logged, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// Minimal ILogger implementation that captures log calls for assertion.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _log;
        public CapturingLogger(List<(LogLevel Level, string Message)> log) => _log = log;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _log.Add((logLevel, formatter(state, exception)));
        }
    }
}
