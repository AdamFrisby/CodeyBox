using System.Text.Json;
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
                  "mode": "completion",
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
        Assert.Equal("completion", loaded.Checks[0].Mode);
        Assert.Equal("agentic", loaded.Checks[1].Mode);
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
    public async Task LoadAsync_ReadsTopLevelArrayTemplate()
    {
        await File.WriteAllTextAsync(Path.Combine(_templateDir, "security.json"), """
            [
              {
                "question": "Is user input interpolated into SQL?",
                "onYes": {
                  "title": "Fix SQL injection",
                  "prompt": "Replace unsafe SQL construction with parameters."
                }
              }
            ]
            """);

        var registry = new FileTaskTemplateRegistry(_templateDir);
        var loaded = await registry.LoadAsync("security");

        var check = Assert.Single(loaded.Checks);
        Assert.Equal("security", loaded.Name);
        Assert.Equal("Is user input interpolated into SQL?", check.Question);
        Assert.Equal("Fix SQL injection", check.OnYes.Title);
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

    [Theory]
    [MemberData(nameof(InvalidTemplateRefCases))]
    public async Task LoadAsync_InvalidTemplateReferences_AreRejected(string templateRef, string expectedMessage)
    {
        var registry = new FileTaskTemplateRegistry(_templateDir);
        var ex = await Assert.ThrowsAsync<TaskTemplateLoadException>(() => registry.LoadAsync(templateRef));

        Assert.Contains(expectedMessage, ex.Message);
    }

    [Fact]
    public async Task LoadAsync_TooManyChecks_ThrowsClearError()
    {
        await File.WriteAllTextAsync(Path.Combine(_templateDir, "large.json"), """
            {
              "checks": [
                {
                  "question": "Is logging missing?",
                  "onYes": { "title": "Add logging", "prompt": "Add useful logs." }
                },
                {
                  "question": "Is tracing missing?",
                  "onYes": { "title": "Add tracing", "prompt": "Add useful traces." }
                }
              ]
            }
            """);

        var registry = new FileTaskTemplateRegistry(_templateDir, maxCheckCount: 1);
        var ex = await Assert.ThrowsAsync<TaskTemplateLoadException>(() => registry.LoadAsync("large"));

        Assert.Contains("at most 1", ex.Message);
    }

    [Theory]
    [MemberData(nameof(InvalidTemplateCases))]
    public async Task LoadAsync_InvalidTemplateBranches_ThrowClearErrors(string json, string expectedMessage)
    {
        await File.WriteAllTextAsync(Path.Combine(_templateDir, "bad.json"), json);

        var registry = new FileTaskTemplateRegistry(_templateDir);
        var ex = await Assert.ThrowsAsync<TaskTemplateLoadException>(() => registry.LoadAsync("bad"));

        Assert.Contains(expectedMessage, ex.Message);
    }

    public static IEnumerable<object[]> InvalidTemplateCases()
    {
        yield return Case("""{"checks":[{"question":"   ","onYes":{"title":"Fix","prompt":"Prompt"}}]}""",
            ".question is required");
        yield return Case(TemplateWithCheck(
            "{\"question\":" + JsonString(new string('q', 64 * 1024 + 1)) +
            ",\"onYes\":{\"title\":\"Fix\",\"prompt\":\"Prompt\"}}"),
            ".question must be <= 64KB");
        yield return Case("""{"checks":[null]}""", "checks[0] must be an object");
        yield return Case("""{"checks":[1]}""", "not a valid check entry");
        yield return Case("{", "not valid JSON");
        yield return Case("42", "JSON array or an object");
        yield return Case("{}", "must contain a checks array");
        yield return Case("""{"checks":{}}""", "checks must be an array");
        yield return Case("""{"checks":[]}""", "at least one");
        yield return Case("""{"checks":[{"question":"q"}]}""", ".onYes is required");
        yield return Case("""{"checks":[{"question":"q","mode":"tools","onYes":{"title":"Fix","prompt":"Prompt"}}]}""",
            ".mode must be 'agentic' or 'completion'");
        yield return Case("""{"checks":[{"question":"q","onYes":{"prompt":"Prompt"}}]}""",
            ".onYes.title is required");
        yield return Case(TemplateWithCheck(
            "{\"question\":\"q\",\"onYes\":{\"title\":" + JsonString(new string('t', 201)) +
            ",\"prompt\":\"Prompt\"}}"),
            ".onYes.title must be <= 200 chars");
        yield return Case("""{"checks":[{"question":"q","onYes":{"title":"-Fix","prompt":"Prompt"}}]}""",
            ".onYes.title must not start with '-'");
        yield return Case("""{"checks":[{"question":"q","onYes":{"title":"Fix\nnow","prompt":"Prompt"}}]}""",
            ".onYes.title must not contain control characters");
        yield return Case("""{"checks":[{"question":"q","onYes":{"title":"Fix"}}]}""",
            ".onYes.prompt is required");
        yield return Case(TemplateWithCheck(
            "{\"question\":\"q\",\"onYes\":{\"title\":\"Fix\",\"prompt\":" +
            JsonString(new string('p', 64 * 1024 + 1)) + "}}"),
            ".onYes.prompt must be <= 64KB");
        yield return Case(TemplateWithCheck(
            "{\"question\":\"q\",\"onYes\":{\"title\":\"Fix\",\"prompt\":\"Prompt\",\"agentClassId\":" +
            JsonString(new string('c', 201)) + "}}"),
            ".onYes.agentClassId must be <= 200 chars");
        yield return Case(TemplateWithCheck(
            "{\"question\":\"q\",\"onYes\":{\"title\":\"Fix\",\"prompt\":\"Prompt\",\"dependsOn\":" +
            JsonSerializer.Serialize(Enumerable.Range(0, 101).Select(i => $"dep-{i}").ToArray()) + "}}"),
            ".onYes.dependsOn must contain at most 100 entries");
        yield return Case("""{"checks":[{"question":"q","onYes":{"title":"Fix","prompt":"Prompt","dependsOn":["  "]}}]}""",
            ".onYes.dependsOn must not contain empty entries");
        yield return Case(TemplateWithCheck(
            "{\"title\":" + JsonString(new string('t', 201)) +
            ",\"question\":\"q\",\"onYes\":{\"title\":\"Fix\",\"prompt\":\"Prompt\"}}"),
            ".title must be <= 200 chars");
        yield return Case("""{"checks":[{"title":"Fix\nnow","question":"q","onYes":{"title":"Fix","prompt":"Prompt"}}]}""",
            ".title must not contain control characters");
        yield return Case(TemplateWithCheck(
            "{\"question\":\"q\",\"prompt\":" + JsonString(new string('p', 64 * 1024 + 1)) +
            ",\"onYes\":{\"title\":\"Fix\",\"prompt\":\"Prompt\"}}"),
            ".prompt must be <= 64KB");
    }

    public static IEnumerable<object[]> InvalidTemplateRefCases()
    {
        var root = Path.GetPathRoot(Environment.CurrentDirectory) ?? Path.DirectorySeparatorChar.ToString();
        yield return new object[] { "", "template name is required" };
        yield return new object[] { "   ", "template name is required" };
        yield return new object[] { Path.Combine(root, "outside-template"), "relative to the templates directory" };
        yield return new object[] { "templates/", "template name is required" };
        yield return new object[] { "templates/.json", "template name is required" };
        yield return new object[] { "security//extra", "empty, '.', or '..' path segments" };
        yield return new object[] { "security/./extra", "empty, '.', or '..' path segments" };
    }

    private static object[] Case(string json, string expectedMessage) => [json, expectedMessage];

    private static string TemplateWithCheck(string checkJson) => "{\"checks\":[" + checkJson + "]}";

    private static string JsonString(string value) => JsonSerializer.Serialize(value);
}
