using CodeyBox.Audit.Presets;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class PresetCatalogTests
{
    private sealed class FakeAgent : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public Task<AgentResult> RunAsync(ISandbox _, string __, string ___, AgentCredential? ____, string? _____ = null, string? ______ = null, CancellationToken _______ = default)
            => Task.FromResult(new AgentResult(true, "ok", null, null));
    }

    [Fact]
    public void ShipsExpectedLanguagePresets()
    {
        var catalog = new PresetCatalog();
        Assert.Contains("python", catalog.KnownLanguages);
        Assert.Contains("typescript", catalog.KnownLanguages);
        Assert.Contains("go", catalog.KnownLanguages);
        Assert.Contains("rust", catalog.KnownLanguages);
        Assert.Contains("csharp", catalog.KnownLanguages);
        Assert.Contains("ruby", catalog.KnownLanguages);
        Assert.Contains("shell", catalog.KnownLanguages);
    }

    [Fact]
    public void ShipsExpectedAuditTypes()
    {
        var catalog = new PresetCatalog();
        Assert.Contains("security", catalog.KnownAuditTypes);
        Assert.Contains("architecture", catalog.KnownAuditTypes);
        Assert.Contains("quality", catalog.KnownAuditTypes);
        Assert.Contains("completeness", catalog.KnownAuditTypes);
        Assert.Contains("cheating", catalog.KnownAuditTypes);
        Assert.Contains("tests", catalog.KnownAuditTypes);
    }

    [Fact]
    public void TestsPreset_IncludesBothToolAndLlm()
    {
        var catalog = new PresetCatalog();
        var auditors = catalog.ResolveAuditType("tests", new PresetContext(new FakeAgent()));
        Assert.Contains(auditors, a => a.Required == AuditCapabilities.None); // diff-pattern
        Assert.Contains(auditors, a => a.Required.HasFlag(AuditCapabilities.AgentCredentials)); // llm reviewer
    }

    [Fact]
    public void SecurityPreset_HasGitleaksAndSemgrepAndLlm()
    {
        var catalog = new PresetCatalog();
        var auditors = catalog.ResolveAuditType("security", new PresetContext(new FakeAgent()));
        Assert.Contains(auditors, a => a.Name == "security:gitleaks");
        Assert.Contains(auditors, a => a.Name == "security:semgrep");
        Assert.Contains(auditors, a => a.Name == "security:llm-review");
    }

    [Fact]
    public void PythonPreset_ResolvesToShellAuditors()
    {
        var catalog = new PresetCatalog();
        var auditors = catalog.ResolveLanguage("python", new PresetContext(new FakeAgent()));
        Assert.NotEmpty(auditors);
        // All language presets are tool-only by design.
        Assert.All(auditors, a => Assert.Equal(AuditCapabilities.None, a.Required));
        Assert.Contains(auditors, a => a.Name == "python:ruff-check");
    }

    [Fact]
    public void CheatingPreset_IncludesBothToolAndLlm()
    {
        var catalog = new PresetCatalog();
        var auditors = catalog.ResolveAuditType("cheating", new PresetContext(new FakeAgent()));
        Assert.Contains(auditors, a => a.Required == AuditCapabilities.None); // diff-pattern
        Assert.Contains(auditors, a => a.Required.HasFlag(AuditCapabilities.AgentCredentials)); // llm reviewer
    }

    [Fact]
    public void UnknownPreset_ReturnsEmpty()
    {
        var catalog = new PresetCatalog();
        Assert.Empty(catalog.ResolveLanguage("klingon", new PresetContext(new FakeAgent())));
        Assert.Empty(catalog.ResolveAuditType("vibes", new PresetContext(new FakeAgent())));
    }
}
