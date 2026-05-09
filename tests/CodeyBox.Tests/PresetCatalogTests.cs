using CodeyBox.Audit.Presets;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class PresetCatalogTests
{
    private sealed class FakeAgent : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public Task<AgentResult> RunAsync(ISandbox _, string __, string ___, AgentCredential? ____, string? _____ = null, string? ______ = null, CancellationToken _______ = default, Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
            => Task.FromResult(new AgentResult(true, "ok", null, null));
    }

    [Fact]
    public void ShipsExpectedLanguagePresets()
    {
        var catalog = new PresetCatalog();
        Assert.Contains("python", catalog.KnownLanguages);
        Assert.Contains("node", catalog.KnownLanguages);
        Assert.Contains("go", catalog.KnownLanguages);
        Assert.Contains("rust", catalog.KnownLanguages);
        Assert.Contains("csharp", catalog.KnownLanguages);
        Assert.DoesNotContain("typescript", catalog.KnownLanguages);
        Assert.DoesNotContain("javascript", catalog.KnownLanguages);
        Assert.DoesNotContain("ruby", catalog.KnownLanguages);
        Assert.DoesNotContain("shell", catalog.KnownLanguages);
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
        Assert.Contains(auditors, a => a.Name == "python:format-check");
        Assert.Contains(auditors, a => a.Name == "python:typecheck");
        Assert.Contains(auditors, a => a.Name == "python:test-pass");
    }

    [Fact]
    public void LanguagePresetYamlLoading_LoadsBuiltInDefaultsWithExpectedAuditorNames()
    {
        var catalog = new PresetCatalog();
        var ctx = new PresetContext(new FakeAgent());

        Assert.Equal(["csharp:format-check", "csharp:build-WaE", "csharp:test-pass"],
            catalog.ResolveLanguage("csharp", ctx).Select(a => a.Name).ToArray());
        Assert.Equal(["python:format-check", "python:typecheck", "python:test-pass"],
            catalog.ResolveLanguage("python", ctx).Select(a => a.Name).ToArray());
        Assert.Equal(["node:format-check", "node:lint", "node:test-pass"],
            catalog.ResolveLanguage("node", ctx).Select(a => a.Name).ToArray());
        Assert.Equal(["go:format-check", "go:vet", "go:test-pass"],
            catalog.ResolveLanguage("go", ctx).Select(a => a.Name).ToArray());
        Assert.Equal(["rust:format-check", "rust:lint", "rust:test-pass"],
            catalog.ResolveLanguage("rust", ctx).Select(a => a.Name).ToArray());
    }

    [Fact]
    public void AuditTypeYamlLoading_LoadsBuiltInReviewFocus()
    {
        var catalog = new PresetCatalog();

        Assert.StartsWith("You are performing a security code review aligned to OWASP ASVS 5.0,", catalog.GetAuditTypeReviewFocus("security"), StringComparison.Ordinal);
        Assert.Equal("""
            - TODO / FIXME / XXX markers added in this change
            - New functionality without corresponding tests
            - Half-finished implementations (functions that return early, swallowed branches)
            - Public functions whose docstrings/comments describe behaviour the code doesn't implement
            - Test files that were renamed or deleted instead of fixed
            """.Replace("\r\n", "\n", StringComparison.Ordinal), catalog.GetAuditTypeReviewFocus("completeness"));
        Assert.Equal("""
            Compare the diff against the original task. Look for shortcuts the agent took rather than fully solving the problem:
            - Stubbed or trivially-faked implementations (NotImplementedException, hardcoded returns where logic was requested)
            - Disabled compiler/linter/type-checker warnings instead of fixing the underlying issue
            - Overly broad exception catches that swallow errors
            - Skipped or removed failing tests rather than fixing them
            - Commented-out code that should be active
            - 'Mock' or 'temporary' implementations marked as such
            - Functions that return success without actually doing the work
            Any of these should be flagged as Error.
            """.Replace("\r\n", "\n", StringComparison.Ordinal), catalog.GetAuditTypeReviewFocus("cheating"));
    }

    [Fact]
    public void SchemaValidation_RejectsTypoInArgv()
    {
        using var temp = TempProject();
        Directory.CreateDirectory(Path.Combine(temp.Path, "codeybox", "languages"));
        File.WriteAllText(Path.Combine(temp.Path, "codeybox", "languages", "csharp.yaml"), """
            id: csharp
            auditors:
              - name: csharp:bad
                argv: ["dottest", "build"]
            """);

        var ex = Assert.Throws<PresetConfigurationException>(() => new PresetCatalog(new PresetCatalogOptions { ProjectRoot = temp.Path }));

        Assert.Contains("/auditors/0/argv/0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("did you mean 'dotnet'", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SchemaValidation_RejectsUnknownPlaceholderInFrame()
    {
        using var temp = TempProject();
        Directory.CreateDirectory(Path.Combine(temp.Path, "codeybox"));
        File.WriteAllText(Path.Combine(temp.Path, "codeybox", "llm-prompt-frame.yaml"), """
            frame: "Review {{unknownVar}}"
            """);

        var ex = Assert.Throws<PresetConfigurationException>(() => new PresetCatalog(new PresetCatalogOptions { ProjectRoot = temp.Path }));

        Assert.Contains("{{unknownVar}}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("/frame", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaValidation_RejectsMalformedYaml()
    {
        using var temp = TempProject();
        Directory.CreateDirectory(Path.Combine(temp.Path, "codeybox", "languages"));
        File.WriteAllText(Path.Combine(temp.Path, "codeybox", "languages", "bad.yaml"), "not: yaml: at all:");

        var ex = Assert.Throws<PresetConfigurationException>(() => new PresetCatalog(new PresetCatalogOptions { ProjectRoot = temp.Path }));

        Assert.Contains("malformed YAML", ex.Message, StringComparison.Ordinal);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SchemaValidation_RejectsUnknownYamlField()
    {
        using var temp = TempProject();
        Directory.CreateDirectory(Path.Combine(temp.Path, "codeybox", "languages"));
        File.WriteAllText(Path.Combine(temp.Path, "codeybox", "languages", "csharp.yaml"), """
            id: csharp
            audtors:
              - name: csharp:bad
                argv: ["dotnet", "test"]
            """);

        var ex = Assert.Throws<PresetConfigurationException>(() => new PresetCatalog(new PresetCatalogOptions { ProjectRoot = temp.Path }));

        Assert.Contains("audtors", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserOverride_AdditiveAuditors_AppendsInOrder()
    {
        using var temp = TempProject();
        Directory.CreateDirectory(Path.Combine(temp.Path, "codeybox", "languages"));
        File.WriteAllText(Path.Combine(temp.Path, "codeybox", "languages", "csharp.yaml"), """
            id: csharp
            auditors:
              - name: csharp:custom
                argv: ["dotnet", "tool", "run", "custom"]
            """);

        var names = new PresetCatalog(new PresetCatalogOptions { ProjectRoot = temp.Path })
            .ResolveLanguage("csharp", new PresetContext(new FakeAgent()))
            .Select(a => a.Name)
            .ToArray();

        Assert.Equal(["csharp:format-check", "csharp:build-WaE", "csharp:test-pass", "csharp:custom"], names);
    }

    [Fact]
    public void UserOverride_ReplaceMode_ReplacesBuiltInAuditors()
    {
        using var temp = TempProject();
        Directory.CreateDirectory(Path.Combine(temp.Path, "codeybox", "languages"));
        File.WriteAllText(Path.Combine(temp.Path, "codeybox", "languages", "csharp.yaml"), """
            id: csharp
            replace: true
            marker:
              globs: ["**/*.csproj"]
            auditors:
              - name: csharp:replacement
                argv: ["dotnet", "test"]
            """);

        var names = new PresetCatalog(new PresetCatalogOptions { ProjectRoot = temp.Path })
            .ResolveLanguage("csharp", new PresetContext(new FakeAgent()))
            .Select(a => a.Name)
            .ToArray();

        Assert.Equal(["csharp:replacement"], names);
    }

    [Fact]
    public void UserOverride_ProjectConfigReplaceMode_PreservesBuiltInMarker()
    {
        var names = new PresetCatalog(new PresetCatalogOptions
        {
            LanguageOverrides =
            {
                ["csharp"] = new LanguagePresetOverride
                {
                    Replace = true,
                    Auditors =
                    [
                        new ConfiguredAuditor
                        {
                            Name = "csharp:replacement",
                            Argv = ["dotnet", "test"],
                        },
                    ],
                },
            },
        })
            .ResolveLanguage("csharp", new PresetContext(new FakeAgent()))
            .Select(a => a.Name)
            .ToArray();

        Assert.Equal(["csharp:replacement"], names);
    }

    [Fact]
    public void ProjectFiles_LoadRepositoryLocalLanguageYaml()
    {
        var catalog = new PresetCatalog(new PresetCatalogOptions
        {
            ProjectFiles =
            {
                ["codeybox/languages/elixir.yaml"] = """
                    id: elixir
                    displayName: "Elixir"
                    marker:
                      globs: ["**/mix.exs"]
                    auditors:
                      - name: elixir:test-pass
                        argv: ["mix", "test"]
                    """,
            },
        });

        Assert.Contains("elixir", catalog.KnownLanguages);
        Assert.Equal(["elixir:test-pass"], catalog.ResolveLanguage("elixir", new PresetContext(new FakeAgent())).Select(a => a.Name).ToArray());
    }

    [Fact]
    public void UserOverride_ProjectConfigWinsForAuditTypeFocus()
    {
        using var temp = TempProject();
        Directory.CreateDirectory(Path.Combine(temp.Path, "codeybox", "audit-types"));
        File.WriteAllText(Path.Combine(temp.Path, "codeybox", "audit-types", "completeness.yaml"), """
            id: completeness
            reviewFocus: "from file"
            """);

        var catalog = new PresetCatalog(new PresetCatalogOptions
        {
            ProjectRoot = temp.Path,
            AuditTypeOverrides =
            {
                ["completeness"] = new AuditTypePresetOverride { ReviewFocus = "from appsettings" },
            },
        });

        Assert.Equal("from appsettings", catalog.GetAuditTypeReviewFocus("completeness"));
    }

    [Fact]
    public void AddNewLanguageWithoutRecompile_LoadsProjectLanguageYaml()
    {
        using var temp = TempProject();
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

        var catalog = new PresetCatalog(new PresetCatalogOptions { ProjectRoot = temp.Path });

        Assert.Contains("elixir", catalog.KnownLanguages);
        Assert.Equal(["elixir:test-pass"], catalog.ResolveLanguage("elixir", new PresetContext(new FakeAgent())).Select(a => a.Name).ToArray());
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

    private static TempDirectory TempProject() => new();

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
