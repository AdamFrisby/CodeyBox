using CodeyBox.Audit;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Wiring tests for the prompt-revision trailer auditor: it is registered as
/// a singleton IAuditor in DI but ProjectAuditorComposer.Compose is the only
/// place auditors actually become part of a project's audit run. Without an
/// IncludeRegisteredAuditor call inside Compose, the auditor is dead code in
/// production even though every unit test for the auditor itself passes.
/// </summary>
public sealed class ProjectAuditorComposerPromptRevisionTests
{
    [Fact]
    public void Compose_IncludesPromptRevisionTrailerAuditor_WhenRegistered()
    {
        var trailerAuditor = new PromptRevisionTrailerAuditor();
        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            new IAuditor[] { trailerAuditor },
            NullLogger<ProjectAuditorComposer>.Instance);

        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.com/repo.git",
            Audit = new ProjectAudit { Languages = [], AuditTypes = [] },
        };

        var auditors = composer.Compose(project, new StubAgent());
        Assert.Contains(auditors, a => a.Name == PromptRevisionTrailerAuditor.AuditorName);
    }

    [Fact]
    public void Compose_PromptRevisionTrailerAuditor_AppendedToPresetAuditors()
    {
        // Trailer auditor sits AFTER the preset auditors (it's a process
        // check, not a code check) so language-specific failures surface
        // before the trailer enforcement does.
        var trailerAuditor = new PromptRevisionTrailerAuditor();
        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            new IAuditor[] { trailerAuditor },
            NullLogger<ProjectAuditorComposer>.Instance);
        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.com/repo.git",
            Audit = new ProjectAudit
            {
                Profile = AuditProfilePresets.Uat,
                Profiles = AuditProfilePresets.CreateBuiltIns(),
            },
        };

        var auditors = composer.Compose(project, new StubAgent());
        var names = auditors.Select(a => a.Name).ToList();

        Assert.Contains(PromptRevisionTrailerAuditor.AuditorName, names);
        Assert.True(
            names.IndexOf(PromptRevisionTrailerAuditor.AuditorName)
            > names.IndexOf("csharp:format-check"),
            "process:prompt-revision-trailer should be appended after preset auditors");
    }

    [Fact]
    public void Compose_PromptRevisionTrailerAuditor_RespectsExclusion()
    {
        // Project-level ExcludedAuditors must still be able to drop the trailer
        // auditor (e.g. for projects with a custom commit-trailer policy). The
        // composer treats the registered auditor identically to preset auditors
        // for exclusion purposes.
        var trailerAuditor = new PromptRevisionTrailerAuditor();
        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            new IAuditor[] { trailerAuditor },
            NullLogger<ProjectAuditorComposer>.Instance);

        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.com/repo.git",
            Audit = new ProjectAudit
            {
                Languages = [],
                AuditTypes = [],
                ExcludedAuditors = [PromptRevisionTrailerAuditor.AuditorName],
            },
        };

        var auditors = composer.Compose(project, new StubAgent());
        Assert.DoesNotContain(auditors, a => a.Name == PromptRevisionTrailerAuditor.AuditorName);
    }

    [Fact]
    public void Compose_PromptRevisionTrailerAuditor_NotDuplicated_OnRepeatCompose()
    {
        var trailerAuditor = new PromptRevisionTrailerAuditor();
        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            new IAuditor[] { trailerAuditor },
            NullLogger<ProjectAuditorComposer>.Instance);

        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.com/repo.git",
            Audit = new ProjectAudit { Languages = [], AuditTypes = [] },
        };

        var first = composer.Compose(project, new StubAgent());
        var second = composer.Compose(project, new StubAgent());

        Assert.Single(first, a => a.Name == PromptRevisionTrailerAuditor.AuditorName);
        Assert.Single(second, a => a.Name == PromptRevisionTrailerAuditor.AuditorName);
    }

    private sealed class StubAgent : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;

        public Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
            => Task.FromResult(new AgentResult(true, "ok", "done", null));
    }
}
