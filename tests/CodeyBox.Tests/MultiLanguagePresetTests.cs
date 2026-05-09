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
        public Task<AgentResult> RunAsync(ISandbox _, string __, string ___, AgentCredential? ____, string? _____ = null, string? ______ = null, CancellationToken _______ = default, Action<string>? stdoutChunkCallback = null, bool ________ = false)
            => Task.FromResult(new AgentResult(true, "ok", null, null));
    }

    [Fact]
    public void Compose_IncludesAuditorsForAllDeclaredLanguages()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = ProjectWithLanguages(["csharp", "python", "node"]);

        var auditors = composer.Compose(project, new FakeAgent());

        Assert.Contains(auditors, a => a.Name == "csharp:build-WaE");
        Assert.Contains(auditors, a => a.Name == "csharp:test-pass");
        Assert.Contains(auditors, a => a.Name == "python:test-pass");
        Assert.Contains(auditors, a => a.Name == "node:test-pass");
        Assert.DoesNotContain(auditors, a => a.Name.StartsWith("go:", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownLanguage_IsRejectedByComposer()
    {
        var logger = new CapturingLogger<ProjectAuditorComposer>();
        var composer = new ProjectAuditorComposer(new PresetCatalog(), [], logger);
        var project = ProjectWithLanguages(["zig"]);

        var ex = Assert.Throws<PresetConfigurationException>(() => composer.Compose(project, new FakeAgent()));

        Assert.Contains("unknown language id 'zig'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Did you mean", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JavaScriptAndTypeScriptLanguagesAreRejected()
    {
        var logger = new CapturingLogger<ProjectAuditorComposer>();
        var composer = new ProjectAuditorComposer(new PresetCatalog(), [], logger);
        var project = ProjectWithLanguages(["javascript", "typescript"]);

        var ex = Assert.Throws<PresetConfigurationException>(() => composer.Compose(project, new FakeAgent()));

        Assert.Contains("unknown language id 'javascript'", ex.Message, StringComparison.Ordinal);
    }

    private static Project ProjectWithLanguages(IReadOnlyList<string> languages) => new()
    {
        Id = new ProjectId("test-project"),
        DisplayName = "Test Project",
        RepositoryUrl = "https://example.com/test.git",
        Audit = new ProjectAudit { Languages = languages },
    };
}
