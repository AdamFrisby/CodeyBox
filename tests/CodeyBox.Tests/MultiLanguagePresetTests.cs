using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Projects;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Tests;

public sealed class MultiLanguagePresetTests
{
    private sealed class FakeAgent : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public Task<AgentResult> RunAsync(ISandbox _, string __, string ___, AgentCredential? ____, string? _____ = null, string? ______ = null, CancellationToken _______ = default, Action<string>? stdoutChunkCallback = null)
            => Task.FromResult(new AgentResult(true, "ok", null, null));
    }

    [Fact]
    public void Compose_IncludesAuditorsForAllDeclaredLanguages()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = ProjectWithLanguages(["csharp", "python", "node"]);

        var auditors = composer.Compose(project, new FakeAgent());

        Assert.Contains(auditors, a => a.Name == "csharp:build-WaE");
        Assert.DoesNotContain(auditors, a => a.Name == "csharp:test-pass");
        Assert.Contains(auditors, a => a.Name == "python:test-pass");
        Assert.Contains(auditors, a => a.Name == "node:test-pass");
        Assert.DoesNotContain(auditors, a => a.Name.StartsWith("go:", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownLanguage_IsLoggedAndSkippedByComposer()
    {
        var logger = new CapturingLogger<ProjectAuditorComposer>();
        var composer = new ProjectAuditorComposer(new PresetCatalog(), [], logger);
        var project = ProjectWithLanguages(["zig"]);

        var auditors = composer.Compose(project, new FakeAgent());

        Assert.Empty(auditors);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("unsupported audit language 'zig'", StringComparison.Ordinal));
    }

    [Fact]
    public void JavaScriptAndTypeScriptLanguagesResolveCompatibilityAuditors()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = ProjectWithLanguages(["javascript", "typescript"]);

        var auditors = composer.Compose(project, new FakeAgent());

        Assert.Contains(auditors, a => a.Name == "javascript:lint");
        Assert.Contains(auditors, a => a.Name == "typescript:test-pass");
    }

    private static Project ProjectWithLanguages(IReadOnlyList<string> languages) => new()
    {
        Id = new ProjectId("test-project"),
        DisplayName = "Test Project",
        RepositoryUrl = "https://example.com/test.git",
        Audit = new ProjectAudit { Languages = languages },
    };
}
