using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodeyBox.Admin.Model;

/// <summary>
/// Web mirror of the <c>CodeyBox.Wizard</c> project-configuration wizard
/// (<c>src/CodeyBox.Cli</c>): the same questions, the same validation,
/// the same JSON snippet — answerable from the admin instead of a
/// terminal. The emitted shape is intentionally identical so snippets
/// from either surface paste into <c>CodeyBox.Projects</c> unchanged.
/// Pure over its inputs.
/// </summary>
public static partial class ProjectSetupSnippet
{
    /// <summary>Agents the wizard offers, in offer order.</summary>
    public static readonly IReadOnlyList<string> Agents = ["claude", "copilot", "codex", "gemini"];

    /// <summary>Upstream kinds the wizard offers.</summary>
    public static readonly IReadOnlyList<string> UpstreamKinds = ["noop", "github", "git-generic"];

    /// <summary>Audit language presets.</summary>
    public static readonly IReadOnlyList<string> AuditLanguages = ["csharp", "python", "node", "go", "rust"];

    /// <summary>Audit type presets.</summary>
    public static readonly IReadOnlyList<string> AuditTypes =
        ["security", "architecture", "quality", "completeness", "cheating", "tests"];

    /// <summary>Pipeline phases that take a network profile.</summary>
    public static readonly IReadOnlyList<string> PipelinePhases =
        ["Work", "Rework", "AuditAgent", "AuditTool", "Merge"];

    /// <summary>Built-in network profile names offered when none are configured.</summary>
    public static readonly IReadOnlyList<string> BuiltInProfiles =
        ["claude", "isolated", "internet", "internet-only"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Validates <paramref name="input"/> with the wizard's rules.
    /// Returns the failures in prompt order; empty means the snippet builds.
    /// </summary>
    public static IReadOnlyList<string> Validate(ProjectSetupInput input)
    {
        var errors = new List<string>();
        if (input is null)
        {
            errors.Add("All fields are required.");
            return errors;
        }

        if (!ProjectIdRegex().IsMatch(input.ProjectId ?? string.Empty))
        {
            errors.Add("Project ID must be 1–64 alphanumeric, dash, or underscore characters.");
        }

        if (string.IsNullOrWhiteSpace(input.DisplayName))
        {
            errors.Add("Display name must not be empty.");
        }

        if (!IsRepositoryUrl(input.RepositoryUrl))
        {
            errors.Add("Repository URL must start with https://, http://, git@, ssh://, or be an absolute filesystem path.");
        }

        var branchError = ValidateBranchName(input.BaseBranch);
        if (branchError is not null)
        {
            errors.Add(branchError);
        }

        if (!Agents.Contains(input.Agent ?? string.Empty, StringComparer.Ordinal))
        {
            errors.Add($"Agent must be one of: {string.Join(", ", Agents)}.");
        }

        if (!UpstreamKinds.Contains(input.UpstreamKind ?? string.Empty, StringComparer.Ordinal))
        {
            errors.Add($"Upstream kind must be one of: {string.Join(", ", UpstreamKinds)}.");
        }
        else if (input.UpstreamKind == "github")
        {
            if (IsBlankOrOptionLike(input.GitHubOwner) || HasControlChars(input.GitHubOwner!))
            {
                errors.Add("GitHub owner must not be empty or start with '-'.");
            }

            if (IsBlankOrOptionLike(input.GitHubRepository) || HasControlChars(input.GitHubRepository!))
            {
                errors.Add("GitHub repository must not be empty or start with '-'.");
            }
        }
        else if (input.UpstreamKind == "git-generic")
        {
            if (!IsRepositoryUrl(input.GenericUrl))
            {
                errors.Add("Generic URL must start with https://, http://, git@, ssh://, or be an absolute filesystem path.");
            }
        }

        return errors;
    }

    /// <summary>
    /// Builds the indented JSON snippet. Assumes <see cref="Validate"/>
    /// passed; unknown presets are still emitted verbatim.
    /// </summary>
    public static string BuildJson(ProjectSetupInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        SetupAuditEntry? audit = null;
        var languages = (input.Languages ?? []).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var types = (input.AuditTypes ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (languages.Count > 0 || types.Count > 0)
        {
            audit = new SetupAuditEntry(
                languages.Count > 0 ? languages : null,
                types.Count > 0 ? types : null);
        }

        SetupNetworkProfilesEntry? profiles = null;
        if (input.PhaseProfiles is { Count: > 0 })
        {
            var picked = PipelinePhases.ToDictionary(
                phase => phase,
                phase => input.PhaseProfiles.TryGetValue(phase, out var name) && !string.IsNullOrWhiteSpace(name)
                    ? name.Trim()
                    : null);
            if (picked.Values.Any(v => v is not null))
            {
                profiles = new SetupNetworkProfilesEntry(
                    picked["Work"], picked["Rework"], picked["AuditAgent"], picked["AuditTool"], picked["Merge"]);
            }
        }

        var entry = new SetupProjectEntry
        {
            Id = input.ProjectId?.Trim(),
            DisplayName = input.DisplayName?.Trim(),
            RepositoryUrl = input.RepositoryUrl?.Trim(),
            BaseBranch = string.IsNullOrWhiteSpace(input.BaseBranch) ? "main" : input.BaseBranch.Trim(),
            Agent = input.Agent,
            Upstream = BuildUpstream(input),
            Audit = audit,
            NetworkProfiles = profiles,
        };
        return JsonSerializer.Serialize(entry, JsonOptions);
    }

    private static SetupUpstreamEntry BuildUpstream(ProjectSetupInput input) => input.UpstreamKind switch
    {
        "github" => new SetupUpstreamEntry
        {
            Kind = "github",
            GitHubOwner = input.GitHubOwner?.Trim(),
            GitHubRepository = input.GitHubRepository?.Trim(),
            TokenEnvVar = BlankToNull(input.GitHubTokenEnvVar),
        },
        "git-generic" => new SetupUpstreamEntry
        {
            Kind = "git-generic",
            GenericUrl = input.GenericUrl?.Trim(),
            TokenEnvVar = BlankToNull(input.GenericTokenEnvVar),
        },
        _ => new SetupUpstreamEntry { Kind = "noop" },
    };

    private static string? ValidateBranchName(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return "Base branch must not be empty.";
        }

        if (branch.Contains("..", StringComparison.Ordinal))
        {
            return "Branch name must not contain '..'.";
        }

        if (branch.EndsWith(".lock", StringComparison.Ordinal))
        {
            return "Branch name must not end with '.lock'.";
        }

        if (!BranchNameRegex().IsMatch(branch))
        {
            return "Branch name must start with a letter or digit, be 1–200 chars, and contain only letters, digits, '.', '_', '/', or '-'.";
        }

        return null;
    }

    private static bool IsRepositoryUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url[0] == '-')
        {
            return false;
        }

        if (HasControlChars(url))
        {
            return false;
        }

        return url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
            || Path.IsPathRooted(url);
    }

    private static bool IsBlankOrOptionLike(string? value) =>
        string.IsNullOrWhiteSpace(value) || value[0] == '-';

    private static bool HasControlChars(string value) =>
        value.AsSpan().IndexOfAny(['\n', '\r', '\0']) >= 0;

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectIdRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/\-]{0,199}$", RegexOptions.CultureInvariant)]
    private static partial Regex BranchNameRegex();
}

/// <summary>Answers to the setup wizard's questions.</summary>
public sealed record ProjectSetupInput(
    string? ProjectId = null,
    string? DisplayName = null,
    string? RepositoryUrl = null,
    string? BaseBranch = "main",
    string? Agent = "claude",
    string? UpstreamKind = "noop",
    string? GitHubOwner = null,
    string? GitHubRepository = null,
    string? GitHubTokenEnvVar = null,
    string? GenericUrl = null,
    string? GenericTokenEnvVar = null,
    IReadOnlyList<string>? Languages = null,
    IReadOnlyList<string>? AuditTypes = null,
    IReadOnlyDictionary<string, string?>? PhaseProfiles = null);

/// <summary>Config entry as the CLI wizard emits it (PascalCase keys).</summary>
public sealed class SetupProjectEntry
{
    public string? Id { get; init; }
    public string? DisplayName { get; init; }
    public string? RepositoryUrl { get; init; }
    public string? BaseBranch { get; init; }
    public string? Agent { get; init; }
    public SetupUpstreamEntry? Upstream { get; init; }
    public SetupAuditEntry? Audit { get; init; }
    public SetupNetworkProfilesEntry? NetworkProfiles { get; init; }
}

/// <summary>Upstream block as the CLI wizard emits it.</summary>
public sealed class SetupUpstreamEntry
{
    public string? Kind { get; init; }
    public string? GitHubOwner { get; init; }
    public string? GitHubRepository { get; init; }
    public string? GenericUrl { get; init; }
    public string? TokenEnvVar { get; init; }
}

/// <summary>Audit block as the CLI wizard emits it.</summary>
public sealed record SetupAuditEntry(
    IReadOnlyList<string>? Languages,
    IReadOnlyList<string>? AuditTypes);

/// <summary>Network-profiles block as the CLI wizard emits it.</summary>
public sealed record SetupNetworkProfilesEntry(
    string? Work,
    string? Rework,
    string? AuditAgent,
    string? AuditTool,
    string? Merge);
