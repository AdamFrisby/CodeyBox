using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Projects;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

public sealed class ProjectAuditorComposerPresetTests
{
    [Fact]
    public async Task Compose_AppliesProjectAuditTypeFocusAndFrame()
    {
        var runner = new CapturingAgent();
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.com/repo.git",
            Audit = new ProjectAudit
            {
                AuditTypes = ["completeness"],
                AuditTypeOverrides = new Dictionary<string, ProjectAuditTypeOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["completeness"] = new() { ReviewFocus = "project-specific completeness focus" },
                },
                LlmPromptFrameTemplate = "frame-start\n{{reviewFocus}}\n{{resultFile}}",
            },
        };

        var auditor = Assert.Single(composer.Compose(project, runner));
        await auditor.RunAsync(new ResultFileSandbox(), "/work", new AuditContext(
            WorkItemId.New(),
            WorkBranch: "codeybox/test",
            BaseBranch: "main",
            Iteration: 1,
            OriginalPrompt: "do work"));

        Assert.Contains("frame-start", runner.Prompt, StringComparison.Ordinal);
        Assert.Contains("project-specific completeness focus", runner.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("TODO / FIXME / XXX", runner.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_AppliesProjectLanguageOverride()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = "https://example.com/repo.git",
            Audit = new ProjectAudit
            {
                Languages = ["csharp"],
                LanguageOverrides = new Dictionary<string, ProjectLanguagePresetOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["csharp"] = new()
                    {
                        Replace = true,
                        Auditors =
                        [
                            new ProjectConfiguredAuditor
                            {
                                Name = "csharp:project-test",
                                Argv = ["dotnet", "test"],
                            },
                        ],
                    },
                },
            },
        };

        var auditors = composer.Compose(project, new CapturingAgent());

        Assert.Equal(["csharp:project-test"], auditors.Select(a => a.Name).ToArray());
    }

    [Fact]
    public void Compose_LoadsLanguagePresetFromLocalRepository()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "codeybox", "languages"));
        File.WriteAllText(Path.Combine(temp.Path, "codeybox", "languages", "elixir.yaml"), """
            id: elixir
            displayName: "Elixir"
            marker:
              globs: ["**/mix.exs"]
            auditors:
              - name: elixir:test-pass
                argv: ["mix", "test"]
            """);

        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = new Project
        {
            Id = new ProjectId("alpha"),
            DisplayName = "Alpha",
            RepositoryUrl = new Uri(temp.Path).AbsoluteUri,
            Audit = new ProjectAudit { Languages = ["elixir"] },
        };

        var auditors = composer.Compose(project, new CapturingAgent());

        Assert.Equal(["elixir:test-pass"], auditors.Select(a => a.Name).ToArray());
    }

    private sealed class CapturingAgent : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public string Prompt { get; private set; } = string.Empty;

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
        {
            Prompt = prompt;
            return Task.FromResult(new AgentResult(true, "ok", "review complete", null));
        }
    }

    private sealed class ResultFileSandbox : ISandbox
    {
        public string Id => "result-file-sandbox";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count > 0 && exec.Argv[0] == "cat")
                return Task.FromResult(new SandboxExecResult(0, "{\"passed\":true,\"findings\":[]}", ""));

            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codeybox-presets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
