using System.Text.Json;
using CodeyBox.Core;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api;

internal interface ITaskTemplateRegistry
{
    Task<IReadOnlyList<TaskTemplateSummary>> ListAsync(CancellationToken ct = default);
    Task<TaskTemplateDefinition> LoadAsync(string templateRef, CancellationToken ct = default);
}

internal sealed record TaskTemplateSummary(
    string Name,
    string Path,
    int? CheckCount,
    string? Error = null);

internal sealed record TaskTemplateDefinition(
    string Name,
    string Path,
    IReadOnlyList<TaskTemplateCheck> Checks);

internal sealed record TaskTemplateCheck(
    string Question,
    TaskTemplateOnYesAction OnYes,
    bool? ActionableAnswer = null,
    string? Title = null,
    string? Prompt = null);

internal sealed record TaskTemplateOnYesAction(
    string Title,
    string Prompt,
    int? MinModelScore = null,
    int? Priority = null,
    string? Agent = null,
    string? AgentClassId = null,
    string[]? DependsOn = null);

internal class TaskTemplateLoadException : Exception
{
    public TaskTemplateLoadException(string message) : base(message) { }
}

internal sealed class TaskTemplateNotFoundException : TaskTemplateLoadException
{
    public TaskTemplateNotFoundException(string message) : base(message) { }
}

internal sealed class FileTaskTemplateRegistry : ITaskTemplateRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Func<string> _rootFactory;
    private readonly Func<int> _maxCheckCountFactory;

    public FileTaskTemplateRegistry(IOptionsMonitor<CodeyBoxOptions> options, IHostEnvironment env)
        : this(
            () => ResolveRoot(options.CurrentValue.TemplateDirectory, env.ContentRootPath),
            () => options.CurrentValue.MaxTemplateChecks)
    { }

    internal FileTaskTemplateRegistry(
        string templateDirectory,
        int maxCheckCount = CodeyBoxOptions.DefaultMaxTemplateChecks)
        : this(() => Path.GetFullPath(templateDirectory), () => maxCheckCount) { }

    private FileTaskTemplateRegistry(Func<string> rootFactory, Func<int> maxCheckCountFactory)
    {
        _rootFactory = rootFactory;
        _maxCheckCountFactory = maxCheckCountFactory;
    }

    public async Task<IReadOnlyList<TaskTemplateSummary>> ListAsync(CancellationToken ct = default)
    {
        var root = _rootFactory();
        var maxCheckCount = _maxCheckCountFactory();
        if (!Directory.Exists(root))
            return [];

        var summaries = new List<TaskTemplateSummary>();
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            try
            {
                var template = await LoadFromPathAsync(name, path, maxCheckCount, ct);
                summaries.Add(new TaskTemplateSummary(name, RelativeDisplayPath(root, path), template.Checks.Count));
            }
            catch (TaskTemplateLoadException ex)
            {
                summaries.Add(new TaskTemplateSummary(name, RelativeDisplayPath(root, path), null, ex.Message));
            }
        }

        return summaries;
    }

    public async Task<TaskTemplateDefinition> LoadAsync(string templateRef, CancellationToken ct = default)
    {
        var root = _rootFactory();
        var maxCheckCount = _maxCheckCountFactory();
        var (name, path) = ResolveTemplatePath(root, templateRef);
        if (!File.Exists(path))
            throw new TaskTemplateNotFoundException(
                $"template '{templateRef}' was not found under '{root}'");

        return await LoadFromPathAsync(name, path, maxCheckCount, ct);
    }

    private static string ResolveRoot(string? configured, string contentRootPath)
    {
        var dir = string.IsNullOrWhiteSpace(configured) ? "templates" : configured.Trim();
        return Path.GetFullPath(Path.IsPathFullyQualified(dir)
            ? dir
            : Path.Combine(contentRootPath, dir));
    }

    private static (string Name, string Path) ResolveTemplatePath(string root, string templateRef)
    {
        if (string.IsNullOrWhiteSpace(templateRef))
            throw new TaskTemplateLoadException("template name is required");
        if (Path.IsPathFullyQualified(templateRef))
            throw new TaskTemplateLoadException("template name must be relative to the templates directory");

        var normalised = templateRef.Trim().Replace('\\', '/');
        if (normalised.StartsWith("templates/", StringComparison.OrdinalIgnoreCase))
            normalised = normalised["templates/".Length..];
        if (normalised.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            normalised = normalised[..^".json".Length];

        if (normalised.Length == 0)
            throw new TaskTemplateLoadException("template name is required");
        if (normalised.Split('/').Any(part => part is "" or "." or ".."))
            throw new TaskTemplateLoadException("template name must not contain empty, '.', or '..' path segments");

        var relativeJson = normalised + ".json";
        var path = Path.GetFullPath(Path.Combine(root, relativeJson));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            throw new TaskTemplateLoadException("template path escapes the templates directory");

        return (normalised.Replace('/', Path.DirectorySeparatorChar), path);
    }

    private static async Task<TaskTemplateDefinition> LoadFromPathAsync(
        string name,
        string path,
        int maxCheckCount,
        CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            await using var stream = File.OpenRead(path);
            doc = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                },
                ct);
        }
        catch (JsonException ex)
        {
            throw new TaskTemplateLoadException(
                $"template '{name}' is not valid JSON: {ex.Message}");
        }
        catch (IOException ex)
        {
            throw new TaskTemplateLoadException(
                $"template '{name}' could not be read: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new TaskTemplateLoadException(
                $"template '{name}' could not be read: {ex.Message}");
        }

        using (doc)
        {
            var checksElement = GetChecksElement(name, doc.RootElement);
            var checks = new List<TaskTemplateCheck>();
            var index = 0;
            foreach (var element in checksElement.EnumerateArray())
            {
                if (checks.Count >= maxCheckCount)
                    throw new TaskTemplateLoadException(
                        $"template '{name}' checks must contain at most {maxCheckCount} entries");

                TaskTemplateCheck? check;
                try
                {
                    check = element.Deserialize<TaskTemplateCheck>(JsonOptions);
                }
                catch (JsonException ex)
                {
                    throw new TaskTemplateLoadException(
                        $"template '{name}' checks[{index}] is not a valid check entry: {ex.Message}");
                }

                checks.Add(ValidateCheck(name, index, check));
                index++;
            }

            if (checks.Count == 0)
                throw new TaskTemplateLoadException(
                    $"template '{name}' must contain at least one entry in its checks array");

            return new TaskTemplateDefinition(name, path, checks);
        }
    }

    private static JsonElement GetChecksElement(string name, JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root;

        if (root.ValueKind != JsonValueKind.Object)
            throw new TaskTemplateLoadException(
                $"template '{name}' must be a JSON array or an object with a checks array");

        if (!root.TryGetProperty("checks", out var checks))
            throw new TaskTemplateLoadException(
                $"template '{name}' must contain a checks array");
        if (checks.ValueKind != JsonValueKind.Array)
            throw new TaskTemplateLoadException(
                $"template '{name}' checks must be an array");
        return checks;
    }

    private static TaskTemplateCheck ValidateCheck(
        string templateName,
        int index,
        TaskTemplateCheck? check)
    {
        var prefix = $"template '{templateName}' checks[{index}]";
        if (check is null)
            throw new TaskTemplateLoadException($"{prefix} must be an object");
        if (string.IsNullOrWhiteSpace(check.Question))
            throw new TaskTemplateLoadException($"{prefix}.question is required");
        if (check.Question.Length > 64 * 1024)
            throw new TaskTemplateLoadException($"{prefix}.question must be <= 64KB");
        if (check.OnYes is null)
            throw new TaskTemplateLoadException($"{prefix}.onYes is required");

        var onYes = check.OnYes;
        if (string.IsNullOrWhiteSpace(onYes.Title))
            throw new TaskTemplateLoadException($"{prefix}.onYes.title is required");
        try { Validation.ValidateNoOptionLikeOrControl(onYes.Title, $"{prefix}.onYes.title"); }
        catch (ArgumentException ex) { throw new TaskTemplateLoadException(ex.Message); }
        if (onYes.Title.Length > 200)
            throw new TaskTemplateLoadException($"{prefix}.onYes.title must be <= 200 chars");
        if (string.IsNullOrWhiteSpace(onYes.Prompt))
            throw new TaskTemplateLoadException($"{prefix}.onYes.prompt is required");
        if (onYes.Prompt.Length > 64 * 1024)
            throw new TaskTemplateLoadException($"{prefix}.onYes.prompt must be <= 64KB");
        if (onYes.AgentClassId is { Length: > 200 })
            throw new TaskTemplateLoadException($"{prefix}.onYes.agentClassId must be <= 200 chars");
        if (onYes.DependsOn is { Length: > 100 })
            throw new TaskTemplateLoadException($"{prefix}.onYes.dependsOn must contain at most 100 entries");
        if (onYes.DependsOn is not null && onYes.DependsOn.Any(string.IsNullOrWhiteSpace))
            throw new TaskTemplateLoadException($"{prefix}.onYes.dependsOn must not contain empty entries");

        if (!string.IsNullOrWhiteSpace(check.Title))
        {
            try { Validation.ValidateNoOptionLikeOrControl(check.Title, $"{prefix}.title"); }
            catch (ArgumentException ex) { throw new TaskTemplateLoadException(ex.Message); }
            if (check.Title.Length > 200)
                throw new TaskTemplateLoadException($"{prefix}.title must be <= 200 chars");
        }

        if (check.Prompt is { Length: > 64 * 1024 })
            throw new TaskTemplateLoadException($"{prefix}.prompt must be <= 64KB");

        return check with
        {
            Question = check.Question.Trim(),
            Title = string.IsNullOrWhiteSpace(check.Title) ? null : check.Title.Trim(),
            Prompt = string.IsNullOrWhiteSpace(check.Prompt) ? null : check.Prompt,
            OnYes = onYes with
            {
                Title = onYes.Title.Trim(),
                Prompt = onYes.Prompt,
                Agent = string.IsNullOrWhiteSpace(onYes.Agent) ? null : onYes.Agent.Trim(),
                AgentClassId = string.IsNullOrWhiteSpace(onYes.AgentClassId) ? null : onYes.AgentClassId.Trim(),
                DependsOn = onYes.DependsOn is null
                    ? null
                    : onYes.DependsOn.Select(d => d.Trim()).ToArray(),
            },
        };
    }

    private static string RelativeDisplayPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return Path.Combine("templates", relative).Replace('\\', '/');
    }
}
