using CodeyBox.Audit.Presets;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class LanguageDetectionTests
{
    private sealed class FakeAgent : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public Task<AgentResult> RunAsync(ISandbox _, string __, string ___, AgentCredential? ____, string? _____ = null, string? ______ = null, CancellationToken _______ = default, Action<string>? stdoutChunkCallback = null)
            => Task.FromResult(new AgentResult(true, "ok", null, null));
    }

    [Fact]
    public async Task EnabledLanguageWithoutMarker_ReportsInfoAndDoesNotRunTool()
    {
        var catalog = new PresetCatalog();
        var auditor = catalog.ResolveLanguage("python", new PresetContext(new FakeAgent()))
            .Single(a => a.Name == "python:test-pass");
        var sandbox = new MarkerlessSandbox();

        var result = await auditor.RunAsync(sandbox, "/repo", FakeAuditContext());

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
        Assert.Contains("python preset enabled", finding.Title);
        Assert.DoesNotContain(sandbox.Commands, c => c.Contains("pytest", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("csharp", "csharp:format-check")]
    [InlineData("python", "python:format-check")]
    [InlineData("node", "node:lint")]
    [InlineData("go", "go:vet")]
    [InlineData("rust", "rust:lint")]
    public void SupportedLanguagePresetsResolveExpectedAuditors(string language, string auditorName)
    {
        var catalog = new PresetCatalog();
        var auditors = catalog.ResolveLanguage(language, new PresetContext(new FakeAgent()));
        Assert.Contains(auditors, a => a.Name == auditorName);
        Assert.All(auditors, a => Assert.Equal(AuditCapabilities.None, a.Required));
    }

    private static AuditContext FakeAuditContext() =>
        new(WorkItemId.New(), "feature", "main", 1, "do x");

    private sealed class MarkerlessSandbox : ISandbox
    {
        public List<string> Commands { get; } = [];
        public string Id => "markerless";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Commands.Add(string.Join(' ', exec.Argv));
            return Task.FromResult(new SandboxExecResult(1, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
