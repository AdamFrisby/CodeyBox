using System.Reflection;
using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
    private const string UnrunnableTestsRule = "Tests which cannot be run in this environment are not part of the scoring or auditing criteria.";
    private static readonly IReadOnlySet<string> AuditTypesWithUnrunnableTestsRule =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tests",
            "completeness",
            "quality",
        };
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .Build();
    private static readonly IDeserializer RawDeserializer = new DeserializerBuilder()
        .WithDuplicateKeyChecking()
        .Build();
    private static readonly JsonSerializerOptions SchemaJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly IReadOnlySet<string> KnownTools =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cargo", "dotnet", "eslint", "gitleaks", "go", "gofmt", "govulncheck",
            "mix", "mypy", "mypy or pyright", "npm", "pip-audit", "prettier",
            "pytest", "pyright", "ruff", "safety", "semgrep"
        };

    public PresetConfigSnapshot Load(PresetCatalogOptions? options)
    {
        options ??= new PresetCatalogOptions();
        var assembly = typeof(PresetConfigLoader).Assembly;
        EnsureSchemaResourcesPresent(assembly);

        var languages = LoadEmbeddedLanguages(assembly);
        var auditTypes = LoadEmbeddedAuditTypes(assembly);
        var frame = LoadEmbeddedFrame(assembly);

        foreach (var projectRoot in ProjectRoots(options))
        {
            LoadUserLanguageFiles(projectRoot, languages);
            LoadUserAuditTypeFiles(projectRoot, auditTypes);
        }

        ApplyProjectConfigOverrides(options, languages, auditTypes, ref frame);
        ApplyMandatoryReviewFocusRules(auditTypes);
        ValidateFrame("llm-prompt-frame.yaml", frame.Frame);

        return new PresetConfigSnapshot(languages, auditTypes, frame.Frame);
    }

    private static Dictionary<string, LanguagePresetDefinition> LoadEmbeddedLanguages(Assembly assembly)
    {
        var languages = new Dictionary<string, LanguagePresetDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var resourceName in ResourceNames(assembly, "languages.", ".yaml"))
        {
            var definition = ReadYamlResource<LanguagePresetDefinition>(assembly, resourceName, "language");
            ValidateLanguage(resourceName, definition, allowPartial: false, isTrusted: true);
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
            ValidateAuditType(resourceName, definition, isTrusted: true);
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
            if (definition.Marker?.Script != null)
                throw new PresetConfigurationException($"{file}: /marker/script is not allowed in repository-provided configuration for security reasons. Use /marker/globs instead.");

            ValidateLanguage(file, definition, allowPartial: languages.ContainsKey(definition.Id), isTrusted: false);
            ComposeLanguage(languages, definition, isTrusted: false);
        }
    }

    private static void LoadUserAuditTypeFiles(string? projectRoot, Dictionary<string, AuditTypePresetDefinition> auditTypes)
    {
        foreach (var file in PresetFiles(projectRoot, "audit-types"))
        {
            var definition = ReadYamlFile<AuditTypePresetDefinition>(file, "audit-type");
            if (!string.IsNullOrWhiteSpace(definition.LlmAuditorName))
                throw new PresetConfigurationException($"{file}: /llmAuditorName is not allowed in repository-provided configuration for security reasons.");
            if (!string.IsNullOrWhiteSpace(definition.ReviewFocus))
                throw new PresetConfigurationException($"{file}: /reviewFocus is not allowed in repository-provided configuration for security reasons.");

            ValidateAuditType(file, definition, isTrusted: false);
            ComposeAuditType(auditTypes, definition, isTrusted: false);
        }
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
                    CanShortCircuitOnBlockingFinding = a.CanShortCircuitOnBlockingFinding,
                    Role = a.Role,
                    GateEvidence = a.GateEvidence,
                }).ToList(),
            };
            ValidateLanguage($"Audit.Languages.Overrides[{id}]", definition, allowPartial: languages.ContainsKey(id), isTrusted: true);
            ComposeLanguage(languages, definition, isTrusted: true);
        }

        foreach (var (id, ov) in options.AuditTypeOverrides)
        {
            var definition = new AuditTypePresetDefinition
            {
                Id = id,
                DisplayName = ov.DisplayName,
                ReviewFocus = ov.ReviewFocus ?? string.Empty,
                Replace = ov.Replace,
                Auditors = ov.Auditors.Select(a => new AuditorDefinition
                {
                    Name = a.Name,
                    Argv = [.. a.Argv],
                    Script = a.Script,
                    ToolName = a.ToolName,
                    TreatExit127AsMissingTool = a.TreatExit127AsMissingTool,
                    CanShortCircuitOnBlockingFinding = a.CanShortCircuitOnBlockingFinding,
                    Role = a.Role,
                    GateEvidence = a.GateEvidence,
                }).ToList(),
                Patterns = ov.Patterns.Select(p => new DiffPatternDefinition
                {
                    Regex = p.Regex,
                    Description = p.Description,
                    Severity = p.Severity,
                }).ToList(),
            };
            ValidateAuditType($"Audit.AuditTypes[{id}]", definition, isTrusted: true);
            ComposeAuditType(auditTypes, definition, isTrusted: true);
        }

        if (options.LlmPromptFrameTemplate is not null)
        {
            frame = new FramePresetDefinition { Frame = options.LlmPromptFrameTemplate };
            ValidateFrame("Audit.LlmPromptFrame", frame.Frame);
        }
    }

    private static void ComposeAuditType(
        Dictionary<string, AuditTypePresetDefinition> auditTypes,
        AuditTypePresetDefinition incoming,
        bool isTrusted)
    {
        var shouldReplace = incoming.Replace && isTrusted;
        if (!auditTypes.TryGetValue(incoming.Id, out var existing) || shouldReplace)
        {
            auditTypes[incoming.Id] = incoming;
            return;
        }

        if (!string.IsNullOrWhiteSpace(incoming.DisplayName))
            existing.DisplayName = incoming.DisplayName;
        if (!string.IsNullOrWhiteSpace(incoming.ReviewFocus))
            existing.ReviewFocus = incoming.ReviewFocus;
        if (!string.IsNullOrWhiteSpace(incoming.LlmAuditorName))
            existing.LlmAuditorName = incoming.LlmAuditorName;

        existing.Auditors.AddRange(incoming.Auditors);
        existing.Patterns.AddRange(incoming.Patterns);
    }

    private static void ApplyMandatoryReviewFocusRules(Dictionary<string, AuditTypePresetDefinition> auditTypes)
    {
        foreach (var id in AuditTypesWithUnrunnableTestsRule)
        {
            if (!auditTypes.TryGetValue(id, out var definition) ||
                string.IsNullOrWhiteSpace(definition.ReviewFocus) ||
                ContainsLine(definition.ReviewFocus, UnrunnableTestsRule))
            {
                continue;
            }

            definition.ReviewFocus = definition.ReviewFocus.TrimEnd() + "\n" + UnrunnableTestsRule;
        }
    }

    private static bool ContainsLine(string text, string line)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Any(existingLine => existingLine.Trim().Equals(line, StringComparison.Ordinal));

    private static void ComposeLanguage(
        Dictionary<string, LanguagePresetDefinition> languages,
        LanguagePresetDefinition incoming,
        bool isTrusted)
    {
        languages.TryGetValue(incoming.Id, out var existing);

        var shouldReplace = incoming.Replace && isTrusted;
        if (existing == null || shouldReplace)
        {
            if (shouldReplace && existing != null && incoming.Marker == null)
                incoming.Marker = existing.Marker;

            languages[incoming.Id] = incoming;
            return;
        }

        if (!string.IsNullOrWhiteSpace(incoming.DisplayName))
            existing.DisplayName = incoming.DisplayName;
        if (incoming.Marker is not null)
        {
            if (!isTrusted && HasBuildTestGateMetadata(existing))
            {
                throw new PresetConfigurationException(
                    $"Repository-provided language '{incoming.Id}' cannot override /marker for a language with trusted build-test-gate auditors. Use trusted project configuration to change build/test discovery.");
            }

            existing.Marker = incoming.Marker;
        }
        existing.Auditors.AddRange(incoming.Auditors);
    }

    private static bool HasBuildTestGateMetadata(LanguagePresetDefinition definition)
        => definition.Auditors.Any(static a =>
            !string.IsNullOrWhiteSpace(a.Role) ||
            !string.IsNullOrWhiteSpace(a.GateEvidence));

    private static IEnumerable<string> PresetFiles(string? projectRoot, string childDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            return [];

        var directory = Path.Combine(projectRoot, "codeybox", childDirectory);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.yaml").Order(StringComparer.Ordinal)
            : [];
    }

    private static IEnumerable<string> ProjectRoots(PresetCatalogOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ProjectRoot))
            yield return options.ProjectRoot;

        foreach (var root in options.AdditionalProjectRoots)
        {
            if (!string.IsNullOrWhiteSpace(root))
                yield return root;
        }
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
        var result = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
        if (result.IsValid)
            return;

        var errors = new List<string>();
        CollectErrors(result, errors);

        var message = errors.Count > 0
            ? string.Join("; ", errors.Distinct())
            : "schema validation failed";

        throw new PresetConfigurationException($"{source}: {message}");
    }

    private static void CollectErrors(EvaluationResults result, List<string> errors)
    {
        if (result.Errors != null)
        {
            foreach (var error in result.Errors)
            {
                errors.Add($"{result.InstanceLocation}: {error.Value}");
            }
        }
        if (result.Details != null)
        {
            foreach (var detail in result.Details)
            {
                CollectErrors(detail, errors);
            }
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
            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) return false;
        }

        return value;
    }

    private static void ValidateLanguage(string source, LanguagePresetDefinition definition, bool allowPartial, bool isTrusted)
    {
        if (string.IsNullOrWhiteSpace(definition.Id))
            throw new PresetConfigurationException($"{source}: /id is required.");
        if (!allowPartial && definition.Marker is null)
            throw new PresetConfigurationException($"{source}: /marker is required.");
        if (!allowPartial && (definition.Auditors == null || definition.Auditors.Count == 0))
            throw new PresetConfigurationException($"{source}: /auditors must contain at least one auditor.");
        if (definition.Marker is not null &&
            string.IsNullOrWhiteSpace(definition.Marker.Script) &&
            definition.Marker.Globs.Count == 0)
        {
            throw new PresetConfigurationException($"{source}: /marker must include script or globs.");
        }

        for (var i = 0; i < definition.Auditors.Count; i++)
            ValidateAuditor(source, $"/auditors/{i}", definition.Auditors[i], isTrusted);
    }

    private static void ValidateAuditor(string source, string pointer, AuditorDefinition auditor, bool isTrusted)
    {
        if (string.IsNullOrWhiteSpace(auditor.Name))
            throw new PresetConfigurationException($"{source}: {pointer}/name is required.");
        if (auditor.Argv.Count == 0 && string.IsNullOrWhiteSpace(auditor.Script))
            throw new PresetConfigurationException($"{source}: {pointer} must include argv or script.");
        if (auditor.Argv.Count > 0 && !string.IsNullOrWhiteSpace(auditor.Script))
            throw new PresetConfigurationException($"{source}: {pointer} cannot include both argv and script.");

        if (!isTrusted && !string.IsNullOrWhiteSpace(auditor.Script))
            throw new PresetConfigurationException($"{source}: {pointer}/script is not allowed in repository-provided configuration for security reasons. Use /argv instead.");

        if (auditor.Argv.Count > 0)
        {
            ValidateToolName(source, $"{pointer}/argv/0", auditor.Argv[0], isTrusted);
        }
        else if (!string.IsNullOrWhiteSpace(auditor.ToolName))
        {
            ValidateToolName(source, $"{pointer}/toolName", auditor.ToolName, isTrusted);
        }

        if (!string.IsNullOrWhiteSpace(auditor.Role))
        {
            if (!isTrusted)
                throw new PresetConfigurationException($"{source}: {pointer}/role is not allowed in repository-provided configuration. Build/test gate metadata must come from trusted configuration.");
            _ = ParseAuditorRole(source, $"{pointer}/role", auditor.Role);
        }
        if (!string.IsNullOrWhiteSpace(auditor.GateEvidence))
        {
            if (!isTrusted)
                throw new PresetConfigurationException($"{source}: {pointer}/gateEvidence is not allowed in repository-provided configuration. Build/test gate metadata must come from trusted configuration.");
            _ = ParseBuildTestGateEvidence(source, $"{pointer}/gateEvidence", auditor.Role, auditor.GateEvidence);
        }
    }

    internal static AuditorRole ParseAuditorRole(string source, string pointer, string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return AuditorRole.None;
        return role.Equals("build-test-gate", StringComparison.OrdinalIgnoreCase)
            ? AuditorRole.BuildTestGate
            : throw new PresetConfigurationException($"{source}: {pointer} = '{role}' is not a recognised auditor role. Allowed values: build-test-gate.");
    }

    internal static BuildTestGateEvidence ParseBuildTestGateEvidence(
        string source,
        string pointer,
        string? role,
        string? evidence)
    {
        var parsedRole = ParseAuditorRole(
            source,
            pointer.Replace("/gateEvidence", "/role", StringComparison.Ordinal),
            role);
        if (string.IsNullOrWhiteSpace(evidence))
            return BuildTestGateEvidence.None;

        if (parsedRole != AuditorRole.BuildTestGate)
            throw new PresetConfigurationException($"{source}: {pointer} requires role 'build-test-gate'.");

        return evidence.Trim().ToLowerInvariant() switch
        {
            "build" => BuildTestGateEvidence.Build,
            "test" => BuildTestGateEvidence.Test,
            "build-and-test" => BuildTestGateEvidence.BuildAndTest,
            _ => throw new PresetConfigurationException($"{source}: {pointer} = '{evidence}' is not a recognised gate evidence value. Allowed values: build, test, build-and-test."),
        };
    }

    private static void ValidateToolName(string source, string pointer, string? tool, bool isTrusted)
    {
        if (string.IsNullOrWhiteSpace(tool))
            return;

        if (KnownTools.Contains(tool))
            return;

        var suggestion = KnownTools
            .Select(t => new { Tool = t, Distance = EditDistanceHelper.Compute(tool, t) })
            .Where(x => x.Distance <= 2)
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Tool, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (suggestion is not null)
        {
            throw new PresetConfigurationException(
                $"{source}: {pointer} = '{tool}' is not a known audit tool. Did you mean '{suggestion.Tool}'?");
        }

        if (isTrusted)
            return;

        throw new PresetConfigurationException($"{source}: {pointer} = '{tool}' is not a known audit tool.");
    }

    private static void ValidateAuditType(string source, AuditTypePresetDefinition definition, bool isTrusted)
    {
        if (string.IsNullOrWhiteSpace(definition.Id))
            throw new PresetConfigurationException($"{source}: /id is required.");

        for (var i = 0; i < definition.Auditors.Count; i++)
            ValidateAuditor(source, $"/auditors/{i}", definition.Auditors[i], isTrusted);

        for (var i = 0; i < definition.Patterns.Count; i++)
        {
            var pattern = definition.Patterns[i];
            var pointer = $"/patterns/{i}";
            if (string.IsNullOrWhiteSpace(pattern.Regex))
                throw new PresetConfigurationException($"{source}: {pointer}/regex is required.");
            if (string.IsNullOrWhiteSpace(pattern.Description))
                throw new PresetConfigurationException($"{source}: {pointer}/description is required.");

            try
            {
                _ = new Regex(pattern.Regex, RegexOptions.None, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                throw new PresetConfigurationException($"{source}: {pointer}/regex '{pattern.Regex}' is not a valid regex: {ex.Message}", ex);
            }
        }
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
        {
            var role = ParseAuditorRole($"language '{definition.Id}'", $"/auditors/{a.Name}/role", a.Role);
            var gateEvidence = ParseBuildTestGateEvidence(
                $"language '{definition.Id}'",
                $"/auditors/{a.Name}/gateEvidence",
                a.Role,
                a.GateEvidence);
            return string.IsNullOrWhiteSpace(a.Script)
                ? LanguagePresetHelpers.Shell(
                    definition.Id,
                    markerDescription,
                    markerScript,
                    a.Name,
                    [.. a.Argv],
                    a.CanShortCircuitOnBlockingFinding,
                    role,
                    gateEvidence)
                : LanguagePresetHelpers.ShellScript(
                    definition.Id,
                    markerDescription,
                    markerScript,
                    a.Name,
                    a.Script,
                    a.ToolName ?? a.Name,
                    a.TreatExit127AsMissingTool,
                    a.CanShortCircuitOnBlockingFinding,
                    role,
                    gateEvidence);
        }).ToList();
    }

    private static string BuildMarkerScript(IReadOnlyList<string> globs)
    {
        var expressions = new List<string>();
        foreach (var glob in globs)
        {
            if (string.IsNullOrWhiteSpace(glob)) continue;

            var escaped = glob.Replace("'", "'\\''", StringComparison.Ordinal);
            if (glob.StartsWith("**/", StringComparison.Ordinal))
            {
                var name = glob[3..];
                if (!name.Contains('/') && !name.Contains('[', StringComparison.Ordinal) && !name.Contains('*', StringComparison.Ordinal))
                {
                    // Pure filename search
                    expressions.Add($"-name '{name.Replace("'", "'\\''", StringComparison.Ordinal)}'");
                }
                else
                {
                    // Path-based glob search
                    expressions.Add($"-path './{escaped}'");
                }
            }
            else
            {
                // Explicit path search
                var path = glob.StartsWith("./", StringComparison.Ordinal) ? escaped : "./" + escaped;
                expressions.Add($"-path '{path}'");
            }
        }

        if (expressions.Count == 0)
            throw new PresetConfigurationException("marker.globs must include at least one file-name glob such as '**/*.csproj' or '**/mix.exs'.");

        var joined = string.Join(" -o ", expressions);
        return $"find . -maxdepth 4 \\( {joined} \\) -print | sed 's#[^/]*$##; s#/$##; s#^$#.#' | sort -u";
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
    public bool CanShortCircuitOnBlockingFinding { get; set; }

    /// <summary>
    /// Optional role marker. The only accepted non-default value today is
    /// <c>build-test-gate</c>, which marks the auditor as a deterministic
    /// build or test gate the pipeline runs and passes before any LLM panel.
    /// </summary>
    public string? Role { get; set; }

    /// <summary>
    /// Optional evidence produced by a passing build-test gate:
    /// <c>build</c>, <c>test</c>, or <c>build-and-test</c>.
    /// </summary>
    public string? GateEvidence { get; set; }
}

internal sealed class AuditTypePresetDefinition
{
    public string Id { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string ReviewFocus { get; set; } = string.Empty;
    public string? LlmAuditorName { get; set; }
    public bool Replace { get; set; }
    public List<AuditorDefinition> Auditors { get; set; } = [];
    public List<DiffPatternDefinition> Patterns { get; set; } = [];
}

internal sealed class DiffPatternDefinition
{
    public string Regex { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Severity { get; set; }
}

internal sealed class FramePresetDefinition
{
    public string Frame { get; set; } = string.Empty;
}
