using CodeyBox.Api;

namespace CodeyBox.Tests;

public sealed class TaskTemplateRegistryTests : IDisposable
{
    private readonly string _templateDir = Directory.CreateTempSubdirectory("codeybox-templates-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_templateDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task LoadAsync_ReadsChecksArray_AndReflectsFileChanges()
    {
        var path = Path.Combine(_templateDir, "security.json");
        await File.WriteAllTextAsync(path, """
            {
              "checks": [
                {
                  "question": "Is user input interpolated into SQL?",
                  "onYes": {
                    "title": "Fix SQL injection",
                    "prompt": "Replace unsafe SQL construction with parameters."
                  }
                },
                {
                  "question": "Are auth cookies missing SameSite?",
                  "actionableAnswer": false,
                  "onYes": {
                    "title": "Add SameSite cookie settings",
                    "prompt": "Set secure SameSite attributes on auth cookies."
                  }
                }
              ]
            }
            """);

        var registry = new FileTaskTemplateRegistry(_templateDir);
        var loaded = await registry.LoadAsync("templates/security");

        Assert.Equal("security", loaded.Name);
        Assert.Equal(2, loaded.Checks.Count);
        Assert.Equal("Is user input interpolated into SQL?", loaded.Checks[0].Question);
        Assert.False(loaded.Checks[1].ActionableAnswer);

        await File.WriteAllTextAsync(path, """
            {
              "checks": [
                {
                  "question": "Is CSP missing?",
                  "onYes": {
                    "title": "Add CSP",
                    "prompt": "Configure a content security policy."
                  }
                }
              ]
            }
            """);

        var reloaded = await registry.LoadAsync("security.json");
        Assert.Single(reloaded.Checks);
        Assert.Equal("Is CSP missing?", reloaded.Checks[0].Question);
    }

    [Fact]
    public async Task LoadAsync_InvalidTemplateShape_ThrowsClearError()
    {
        await File.WriteAllTextAsync(Path.Combine(_templateDir, "bad.json"), """{"checks":[{"question":"missing action"}]}""");

        var registry = new FileTaskTemplateRegistry(_templateDir);
        var ex = await Assert.ThrowsAsync<TaskTemplateLoadException>(() => registry.LoadAsync("bad"));

        Assert.Contains("bad", ex.Message);
        Assert.Contains("onYes", ex.Message);
    }

    [Fact]
    public async Task ListAsync_SurfacesBadTemplateErrorsWithoutHidingGoodTemplates()
    {
        await File.WriteAllTextAsync(Path.Combine(_templateDir, "good.json"), """
            {
              "checks": [
                {
                  "question": "Is logging missing?",
                  "onYes": { "title": "Add logging", "prompt": "Add useful logs." }
                }
              ]
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(_templateDir, "bad.json"), """{"checks":[]}""");

        var registry = new FileTaskTemplateRegistry(_templateDir);
        var list = await registry.ListAsync();

        Assert.Contains(list, t => t.Name == "good" && t.CheckCount == 1 && t.Error is null);
        var bad = Assert.Single(list, t => t.Name == "bad");
        Assert.Null(bad.CheckCount);
        Assert.Contains("at least one", bad.Error);
    }

    [Fact]
    public async Task LoadAsync_PathTraversal_IsRejected()
    {
        var registry = new FileTaskTemplateRegistry(_templateDir);
        var ex = await Assert.ThrowsAsync<TaskTemplateLoadException>(() => registry.LoadAsync("../outside"));

        Assert.Contains("..", ex.Message);
    }
}
