namespace CodeyBox.Audit.Presets;

public sealed class PresetCatalogOptions
{
    public string? ProjectRoot { get; set; }
    public List<string> AdditionalProjectRoots { get; set; } = [];
    public Dictionary<string, LanguagePresetOverride> LanguageOverrides { get; set; } =
        new Dictionary<string, LanguagePresetOverride>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AuditTypePresetOverride> AuditTypeOverrides { get; set; } =
        new Dictionary<string, AuditTypePresetOverride>(StringComparer.OrdinalIgnoreCase);
    public string? LlmPromptFrameTemplate { get; set; }

    public PresetCatalogOptions Clone()
        => new()
        {
            ProjectRoot = ProjectRoot,
            AdditionalProjectRoots = [.. AdditionalProjectRoots],
            LanguageOverrides = LanguageOverrides.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Clone(),
                StringComparer.OrdinalIgnoreCase),
            AuditTypeOverrides = AuditTypeOverrides.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Clone(),
                StringComparer.OrdinalIgnoreCase),
            LlmPromptFrameTemplate = LlmPromptFrameTemplate,
        };
}

public sealed class LanguagePresetOverride
{
    public bool Replace { get; set; }
    public List<ConfiguredAuditor> Auditors { get; set; } = [];

    public LanguagePresetOverride Clone()
        => new()
        {
            Replace = Replace,
            Auditors = Auditors.Select(a => a.Clone()).ToList(),
        };
}

public sealed class AuditTypePresetOverride
{
    public string? DisplayName { get; set; }
    public string? ReviewFocus { get; set; }

    public AuditTypePresetOverride Clone()
        => new()
        {
            DisplayName = DisplayName,
            ReviewFocus = ReviewFocus,
        };
}

public sealed class ConfiguredAuditor
{
    public string Name { get; set; } = string.Empty;
    public List<string> Argv { get; set; } = [];
    public string? Script { get; set; }
    public string? ToolName { get; set; }
    public bool? TreatExit127AsMissingTool { get; set; }

    public ConfiguredAuditor Clone()
        => new()
        {
            Name = Name,
            Argv = [.. Argv],
            Script = Script,
            ToolName = ToolName,
            TreatExit127AsMissingTool = TreatExit127AsMissingTool,
        };
}

public sealed class PresetConfigurationException : InvalidOperationException
{
    public PresetConfigurationException(string message)
        : base(message) { }

    public PresetConfigurationException(string message, Exception innerException)
        : base(message, innerException) { }
}
