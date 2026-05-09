using System.Reflection;
using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeyBox.Audit.Llm;
using CodeyBox.Audit.Presets.Presets;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;
using Json.Schema;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CodeyBox.Audit.Presets;

internal sealed class PresetConfigLoader
{
    private const string ResourcePrefix = "CodeyBox.Audit.Presets.Defaults.";
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();
    private static readonly IDeserializer RawDeserializer = new DeserializerBuilder().Build();
    private static readonly JsonSerializerOptions SchemaJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly IReadOnlySet<string> KnownTools =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cargo", "dotnet", "eslint", "gitleaks", "go", "gofmt", "govulncheck",
            "mix", "mypy", "npm", "pip-audit", "prettier", "pytest", "pyright",
            "ruff", "safety", "semgrep", "sh"
        };

    public PresetConfigSnapshot Load(PresetCatalogOptions? options)
    {
        options ??= new PresetCatalogOptions();
        var assembly = typeof(PresetConfigLoader).Assembly;
        EnsureSchemaResourcesPresent(assembly);

        var languages = LoadEmbeddedLanguages(assembly);
        var auditTypes = LoadEmbeddedAuditTypes(assembly);
        var frame = LoadEmbeddedFrame(assembly);

        LoadUserLanguageFiles(options.ProjectRoot, languages);
        LoadUserAuditTypeFiles(options.ProjectRoot, auditTypes);
        LoadUserFrameFile(options.ProjectRoot, ref frame);

        ApplyProjectConfigOverrides(options, languages, auditTypes, ref frame);
        ValidateFrame("llm-prompt-frame.yaml", frame.Frame);

        return new PresetConfigSnapshot(languages, auditTypes, frame.Frame);
    }

    private static Dictionary<string, LanguagePresetDefinition> LoadEmbeddedLanguages(Assembly assembly)
    {
        var languages = new Dictionary<string, LanguagePresetDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var resourceName in ResourceNames(assembly, "languages.", ".yaml"))
        {
            var definition = ReadYamlResource<LanguagePresetDefinition>(assembly, resourceName, "language");
            ValidateLanguage(resourceName, definition, allowPartial: false);
            languages[definition.Id] = definition;
        }
        return languages;
    }

    private static Dictionary<string, AuditTypePresetDefinition> LoadEmbeddedAuditTypes(Assembly assembly)
    {
        var auditTypes = new Dictionary<string, AuditTypePresetDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var resourceName in ResourceNames(assembly, "audit_types.", ".yaml"))
        {
            var definition = ReadYamlResource<AuditTypePresetDefinition>(assembly, resourceName, "audit-type");
            ValidateAuditType(resourceName, definition);
            auditTypes[definition.Id] = definition;
        }
        return auditTypes;
    }

    private static FramePresetDefinition LoadEmbeddedFrame(Assembly assembly)
    {
        var frame = ReadYamlResource<FramePresetDefinition>(assembly, ResourcePrefix + "llm-prompt-frame.yaml", "frame");
        ValidateFrame("Defaults/llm-prompt-frame.yaml", frame.Frame);
        return frame;
    }

    private static void LoadUserLanguageFiles(string? projectRoot, Dictionary<string, LanguagePresetDefinition> languages)
    {
        foreach (var file in PresetFiles(projectRoot, "languages"))
        {
            var definition = ReadYamlFile<LanguagePresetDefinition>(file, "language");
            ValidateLanguage(file, definition, allowPartial: languages.ContainsKey(definition.Id));
            ComposeLanguage(languages, definition);
        }
    }

    private static void LoadUserAuditTypeFiles(string? projectRoot, Dictionary<string, AuditTypePresetDefinition> auditTypes)
    {
        foreach (var file in PresetFiles(projectRoot, "audit-types"))
        {
            var definition = ReadYamlFile<AuditTypePresetDefinition>(file, "audit-type");
            ValidateAuditType(file, definition);
            auditTypes[definition.Id] = definition;
        }
    }

    private static void LoadUserFrameFile(string? projectRoot, ref FramePresetDefinition frame)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            return;

        var path = Path.Combine(projectRoot, "codeybox", "llm-prompt-frame.yaml");
        if (!File.Exists(path))
            return;

        frame = ReadYamlFile<FramePresetDefinition>(path, "frame");
        ValidateFrame(path, frame.Frame);
    }

    private static void ApplyProjectConfigOverrides(
        PresetCatalogOptions options,
        Dictionary<string, LanguagePresetDefinition> languages,
        Dictionary<string, AuditTypePresetDefinition> auditTypes,
        ref FramePresetDefinition frame)
    {
        foreach (var (id, ov) in options.LanguageOverrides)
        {
            var definition = new LanguagePresetDefinition
            {
                Id = id,
                Replace = ov.Replace,
                Auditors = ov.Auditors.Select(a => new AuditorDefinition
                {
                    Name = a.Name,
                    Argv = [.. a.Argv],
                    Script = a.Script,
                    ToolName = a.ToolName,
                    TreatExit127AsMissingTool = a.TreatExit127AsMissingTool,
                }).ToList(),
            };
            ValidateLanguage($"Audit.Languages.Overrides[{id}]", definition, allowPartial: languages.ContainsKey(id));
            ComposeLanguage(languages, definition);
        }

        foreach (var (id, ov) in options.AuditTypeOverrides)
        {
            if (!auditTypes.TryGetValue(id, out var existing))
            {
                existing = new AuditTypePresetDefinition { Id = id, DisplayName = id };
                auditTypes[id] = existing;
            }
            if (!string.IsNullOrWhiteSpace(ov.DisplayName))
                existing.DisplayName = ov.DisplayName;
            if (ov.ReviewFocus is not null)
                existing.ReviewFocus = ov.ReviewFocus;
            ValidateAuditType($"Audit.AuditTypes[{id}]", existing);
        }

        if (options.LlmPromptFrameTemplate is not null)
        {
            frame = new FramePresetDefinition { Frame = options.LlmPromptFrameTemplate };
            ValidateFrame("Audit.LlmPromptFrame", frame.Frame);
        }
    }

    private static void ComposeLanguage(
        Dictionary<string, LanguagePresetDefinition> languages,
        LanguagePresetDefinition incoming)
    {
        if (!languages.TryGetValue(incoming.Id, out var existing) || incoming.Replace)
        {
            if (incoming.Replace && existing is not null)
            {
                incoming.Marker ??= existing.Marker;
                if (string.IsNullOrWhiteSpace(incoming.DisplayName))
                    incoming.DisplayName = existing.DisplayName;
            }
            languages[incoming.Id] = incoming;
            return;
        }

        if (!string.IsNullOrWhiteSpace(incoming.DisplayName))
            existing.DisplayName = incoming.DisplayName;
        if (incoming.Marker is not null)
            existing.Marker = incoming.Marker;
        existing.Auditors.AddRange(incoming.Auditors);
    }

    private static IEnumerable<string> PresetFiles(string? projectRoot, string childDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            return [];

        var directory = Path.Combine(projectRoot, "codeybox", childDirectory);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.yaml").Order(StringComparer.Ordinal)
            : [];
    }

    private static IEnumerable<string> ResourceNames(Assembly assembly, string child, string suffix)
        => assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix + child, StringComparison.Ordinal) &&
                        n.EndsWith(suffix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    private static T ReadYamlResource<T>(Assembly assembly, string resourceName, string schemaName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new PresetConfigurationException($"Embedded preset resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return ReadYamlText<T>(reader.ReadToEnd(), resourceName, schemaName);
    }

    private static T ReadYamlFile<T>(string path, string schemaName)
        => ReadYamlText<T>(File.ReadAllText(path), path, schemaName);

    private static T ReadYamlText<T>(string yaml, string source, string schemaName)
    {
        var json = ReadYamlJson(yaml, source);
        ValidateKnownProperties(schemaName, source, json);
        ValidateJsonSchema(typeof(PresetConfigLoader).Assembly, schemaName, source, json);
        return ReadYaml<T>(yaml, source);
    }

    private static T ReadYaml<T>(string yaml, string source)
    {
        try
        {
            return Deserializer.Deserialize<T>(yaml)
                ?? throw new PresetConfigurationException($"{source}: YAML document is empty.");
        }
        catch (YamlException ex)
        {
            var line = ex.Start.Line;
            var column = ex.Start.Column;
            throw new PresetConfigurationException(
                $"{source}: malformed YAML at line {line}, column {column}: {ex.Message}", ex);
        }
    }

    private static void ValidateJsonSchema<T>(Assembly assembly, string schemaName, string source, T value)
    {
        var schemaResource = schemaName switch
        {
            "language" => ResourcePrefix + "schemas.language.schema.json",
            "audit-type" => ResourcePrefix + "schemas.audit-type.schema.json",
            "frame" => ResourcePrefix + "schemas.frame.schema.json",
            _ => throw new ArgumentOutOfRangeException(nameof(schemaName), schemaName, "Unknown preset schema."),
        };
        using var stream = assembly.GetManifestResourceStream(schemaResource)
            ?? throw new PresetConfigurationException($"Embedded schema resource '{schemaResource}' was not found.");
        using var reader = new StreamReader(stream);
        var schema = JsonSchema.FromText(reader.ReadToEnd());
        var instance = JsonSerializer.SerializeToElement(value, SchemaJsonOptions);
        var result = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid)
            return;

        var detail = result.Details?.FirstOrDefault(d => !d.IsValid);
        var pointer = detail?.InstanceLocation.ToString() ?? "/";
        var message = detail?.Errors?.Values.FirstOrDefault() ?? "schema validation failed";
        throw new PresetConfigurationException($"{source}: {pointer} {message}");
    }

    private static void ValidateKnownProperties(string schemaName, string source, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.Object)
            return;

        switch (schemaName)
        {
            case "language":
                ValidateObjectProperties(source, "/", instance, ["id", "displayName", "replace", "marker", "auditors"]);
                if (instance.TryGetProperty("marker", out var marker) && marker.ValueKind == JsonValueKind.Object)
                    ValidateObjectProperties(source, "/marker", marker, ["globs", "script"]);
                if (instance.TryGetProperty("auditors", out var auditors) && auditors.ValueKind == JsonValueKind.Array)
                {
                    for (var i = 0; i < auditors.GetArrayLength(); i++)
                    {
                        var auditor = auditors[i];
                        if (auditor.ValueKind == JsonValueKind.Object)
                            ValidateObjectProperties(source, $"/auditors/{i}", auditor, ["name", "argv", "script", "toolName", "treatExit127AsMissingTool"]);
                    }
                }
                break;
            case "audit-type":
                ValidateObjectProperties(source, "/", instance, ["id", "displayName", "llmAuditorName", "reviewFocus"]);
                break;
            case "frame":
                ValidateObjectProperties(source, "/", instance, ["frame"]);
                break;
        }
    }

    private static void ValidateObjectProperties(string source, string pointer, JsonElement obj, IReadOnlyList<string> allowed)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new PresetConfigurationException($"{source}: {pointer}/{property.Name} is not allowed by the preset schema.");
        }
    }

    private static JsonElement ReadYamlJson(string yaml, string source)
    {
        try
        {
            var raw = RawDeserializer.Deserialize(new StringReader(yaml));
            if (raw is null)
                throw new PresetConfigurationException($"{source}: YAML document is empty.");
            return JsonSerializer.SerializeToElement(NormalizeYamlValue(raw), SchemaJsonOptions);
        }
        catch (YamlException ex)
        {
            var line = ex.Start.Line;
            var column = ex.Start.Column;
            throw new PresetConfigurationException(
                $"{source}: malformed YAML at line {line}, column {column}: {ex.Message}", ex);
        }
    }

    private static object? NormalizeYamlValue(object? value)
    {
        if (value is IDictionary dictionary)
        {
            var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString();
                if (string.IsNullOrWhiteSpace(key))
                    continue;
                normalized[key] = NormalizeYamlValue(entry.Value);
            }
            return normalized;
        }

        if (value is IEnumerable enumerable and not string)
        {
            var normalized = new List<object?>();
            foreach (var item in enumerable)
                normalized.Add(NormalizeYamlValue(item));
            return normalized;
        }

        if (value is string text)
        {
            if (bool.TryParse(text, out var boolValue))
                return boolValue;
        }

        return value;
    }

    private static void ValidateLanguage(string source, LanguagePresetDefinition definition, bool allowPartial)
    {
        if (string.IsNullOrWhiteSpace(definition.Id))
            throw new PresetConfigurationException($"{source}: /id is required.");
        if (!allowPartial && definition.Marker is null)
            throw new PresetConfigurationException($"{source}: /marker is required.");
        if (!allowPartial && definition.Auditors.Count == 0)
            throw new PresetConfigurationException($"{source}: /auditors must contain at least one auditor.");
        if (definition.Marker is not null &&
            string.IsNullOrWhiteSpace(definition.Marker.Script) &&
            definition.Marker.Globs.Count == 0)
        {
            throw new PresetConfigurationException($"{source}: /marker must include script or globs.");
        }

        for (var i = 0; i < definition.Auditors.Count; i++)
            ValidateAuditor(source, $"/auditors/{i}", definition.Auditors[i]);
    }

    private static void ValidateAuditor(string source, string pointer, AuditorDefinition auditor)
    {
        if (string.IsNullOrWhiteSpace(auditor.Name))
            throw new PresetConfigurationException($"{source}: {pointer}/name is required.");
        if (auditor.Argv.Count == 0 && string.IsNullOrWhiteSpace(auditor.Script))
            throw new PresetConfigurationException($"{source}: {pointer} must include argv or script.");
        if (auditor.Argv.Count > 0 && !string.IsNullOrWhiteSpace(auditor.Script))
            throw new PresetConfigurationException($"{source}: {pointer} cannot include both argv and script.");

        var tool = auditor.Argv.Count > 0 ? auditor.Argv[0] : auditor.ToolName;
        ValidateToolName(source, $"{pointer}/argv/0", tool);
    }

    private static void ValidateToolName(string source, string pointer, string? tool)
    {
        if (string.IsNullOrWhiteSpace(tool))
            return;
        if (KnownTools.Contains(tool))
            return;

        var suggestion = KnownTools
            .Select(t => new { Tool = t, Distance = EditDistance(tool, t) })
            .Where(x => x.Distance <= 2)
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Tool, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (suggestion is not null)
            throw new PresetConfigurationException(
                $"{source}: {pointer} = '{tool}' is not a known audit tool; did you mean '{suggestion.Tool}'?");
    }

    private static void ValidateAuditType(string source, AuditTypePresetDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id))
            throw new PresetConfigurationException($"{source}: /id is required.");
        if (string.IsNullOrWhiteSpace(definition.ReviewFocus))
            throw new PresetConfigurationException($"{source}: /reviewFocus is required.");
    }

    private static void ValidateFrame(string source, string? frame)
    {
        if (string.IsNullOrWhiteSpace(frame))
            throw new PresetConfigurationException($"{source}: /frame is required.");

        foreach (var placeholder in LlmPromptFrameTemplate.FindPlaceholders(frame))
        {
            if (!LlmPromptFrameTemplate.AllowedPlaceholders.Contains(placeholder))
                throw new PresetConfigurationException(
                    $"{source}: /frame contains unknown placeholder '{{{{{placeholder}}}}}'. Allowed placeholders: {string.Join(", ", LlmPromptFrameTemplate.AllowedPlaceholders)}.");
        }
    }

    private static void EnsureSchemaResourcesPresent(Assembly assembly)
    {
        foreach (var schema in new[]
        {
            ResourcePrefix + "schemas.language.schema.json",
            ResourcePrefix + "schemas.audit-type.schema.json",
            ResourcePrefix + "schemas.frame.schema.json",
        })
        {
            if (assembly.GetManifestResourceStream(schema) is null)
                throw new PresetConfigurationException($"Embedded schema resource '{schema}' was not found.");
        }
    }

    private static int EditDistance(string left, string right)
    {
        var dp = new int[left.Length + 1, right.Length + 1];
        for (var i = 0; i <= left.Length; i++)
            dp[i, 0] = i;
        for (var j = 0; j <= right.Length; j++)
            dp[0, j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = char.ToLowerInvariant(left[i - 1]) == char.ToLowerInvariant(right[j - 1]) ? 0 : 1;
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost);
            }
        }
        return dp[left.Length, right.Length];
    }

    public static IReadOnlyList<IAuditor> MaterialiseLanguage(LanguagePresetDefinition definition)
    {
        var marker = definition.Marker ?? throw new PresetConfigurationException($"Language '{definition.Id}' has no marker.");
        var markerDescription = marker.Globs.Count > 0
            ? string.Join("/", marker.Globs)
            : definition.DisplayName ?? definition.Id;
        var markerScript = !string.IsNullOrWhiteSpace(marker.Script)
            ? marker.Script
            : BuildMarkerScript(marker.Globs);

        return definition.Auditors.Select(a =>
            string.IsNullOrWhiteSpace(a.Script)
                ? LanguagePresetHelpers.Shell(
                    definition.Id,
                    markerDescription,
                    markerScript,
                    a.Name,
                    [.. a.Argv])
                : LanguagePresetHelpers.ShellScript(
                    definition.Id,
                    markerDescription,
                    markerScript,
                    a.Name,
                    a.Script,
                    a.ToolName,
                    a.TreatExit127AsMissingTool)).ToList();
    }

    private static string BuildMarkerScript(IReadOnlyList<string> globs)
    {
        var names = globs
            .Select(GlobToFindName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
            throw new PresetConfigurationException("marker.globs must include at least one file-name glob such as '**/*.csproj' or '**/mix.exs'.");

        var expression = string.Join(" -o ", names.Select(n => $"-name '{n.Replace("'", "'\\''", StringComparison.Ordinal)}'"));
        return $"find . -maxdepth 4 \\( {expression} \\) -print | sed 's#[^/]*$##; s#/$##; s#^$#.#' | sort -u";
    }

    private static string GlobToFindName(string glob)
    {
        var slash = glob.LastIndexOf('/');
        var name = slash >= 0 ? glob[(slash + 1)..] : glob;
        return name == "**" || name.Contains('[', StringComparison.Ordinal) ? string.Empty : name;
    }
}

internal sealed record PresetConfigSnapshot(
    IReadOnlyDictionary<string, LanguagePresetDefinition> Languages,
    IReadOnlyDictionary<string, AuditTypePresetDefinition> AuditTypes,
    string LlmPromptFrame);

internal sealed class LanguagePresetDefinition
{
    public string Id { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool Replace { get; set; }
    public MarkerDefinition? Marker { get; set; }
    public List<AuditorDefinition> Auditors { get; set; } = [];
}

internal sealed class MarkerDefinition
{
    public List<string> Globs { get; set; } = [];
    public string? Script { get; set; }
}

internal sealed class AuditorDefinition
{
    public string Name { get; set; } = string.Empty;
    public List<string> Argv { get; set; } = [];
    public string? Script { get; set; }
    public string? ToolName { get; set; }
    public bool? TreatExit127AsMissingTool { get; set; }
}

internal sealed class AuditTypePresetDefinition
{
    public string Id { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string ReviewFocus { get; set; } = string.Empty;
    public string? LlmAuditorName { get; set; }
}

internal sealed class FramePresetDefinition
{
    public string Frame { get; set; } = string.Empty;
}
