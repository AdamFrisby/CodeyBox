using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Spectre.Console;

// ── Banner ────────────────────────────────────────────────────────────────────
AnsiConsole.Write(new FigletText("CodeyBox").Color(Color.Cyan1));
AnsiConsole.MarkupLine("[bold]Project Configuration Wizard[/]");
AnsiConsole.MarkupLine("[dim]Answer the prompts to generate an appsettings.json project entry.[/]");
AnsiConsole.WriteLine();

// ── Project ID ────────────────────────────────────────────────────────────────
var projectId = AnsiConsole.Prompt(
    new TextPrompt<string>("[bold yellow]Project ID[/] [dim](alphanumeric, dash, underscore; 1–64 chars)[/]:")
        .PromptStyle("green")
        .Validate(id =>
        {
            if (Wizard.ProjectIdRegex().IsMatch(id))
                return ValidationResult.Success();
            return ValidationResult.Error("Must be 1–64 alphanumeric, dash, or underscore characters.");
        }));

// ── Display name ──────────────────────────────────────────────────────────────
var displayName = AnsiConsole.Prompt(
    new TextPrompt<string>("[bold yellow]Display name[/]:").PromptStyle("green"));

// ── Repository URL ────────────────────────────────────────────────────────────
var repositoryUrl = AnsiConsole.Prompt(
    new TextPrompt<string>("[bold yellow]Repository URL[/] [dim](https://, http://, git@, ssh://, or absolute path)[/]:")
        .PromptStyle("green")
        .Validate(url =>
        {
            if (string.IsNullOrWhiteSpace(url) || url[0] == '-')
                return ValidationResult.Error("Enter a non-empty URL that does not start with '-'.");
            if (url.AsSpan().IndexOfAny(['\n', '\r', '\0']) >= 0)
                return ValidationResult.Error("URL must not contain control characters.");
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("git@", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
                Path.IsPathRooted(url))
                return ValidationResult.Success();
            return ValidationResult.Error("Must start with https://, http://, git@, ssh://, or be an absolute filesystem path.");
        }));

// ── Base branch ───────────────────────────────────────────────────────────────
var baseBranch = AnsiConsole.Prompt(
    new TextPrompt<string>("[bold yellow]Base branch[/]:")
        .DefaultValue("main")
        .Validate(branch =>
        {
            if (branch.Contains("..", StringComparison.Ordinal))
                return ValidationResult.Error("Branch name must not contain '..'.");
            if (branch.EndsWith(".lock", StringComparison.Ordinal))
                return ValidationResult.Error("Branch name must not end with '.lock'.");
            if (Wizard.BranchNameRegex().IsMatch(branch))
                return ValidationResult.Success();
            return ValidationResult.Error(
                "Branch name must start with a letter or digit, be 1–200 chars, " +
                "and contain only letters, digits, '.', '_', '/', or '-'.");
        }));

// ── Agent ─────────────────────────────────────────────────────────────────────
// "claude" is listed first so Spectre.Console highlights it as the default.
var agent = AnsiConsole.Prompt(
    new SelectionPrompt<string>()
        .Title("[bold yellow]Agent[/]:")
        .HighlightStyle(new Style(Color.Cyan1, decoration: Decoration.Bold))
        .AddChoices("claude", "copilot", "codex", "gemini"));

// ── Upstream kind ─────────────────────────────────────────────────────────────
var upstreamKind = AnsiConsole.Prompt(
    new SelectionPrompt<string>()
        .Title("[bold yellow]Upstream kind[/]:")
        .HighlightStyle(new Style(Color.Cyan1, decoration: Decoration.Bold))
        .AddChoices("noop", "github", "git-generic"));

// Noop is emitted explicitly so the config snippet encodes the choice.
UpstreamEntry upstream = upstreamKind switch
{
    "github" => Wizard.BuildGitHubUpstream(),
    "git-generic" => Wizard.BuildGenericUpstream(),
    _ => new UpstreamEntry { Kind = "noop" },
};

// ── Audit: Languages ──────────────────────────────────────────────────────────
AnsiConsole.WriteLine();
var selectedLanguages = AnsiConsole.Prompt(
    new MultiSelectionPrompt<string>()
        .Title("[bold yellow]Audit languages[/] [dim](space to toggle, enter to confirm — can be empty)[/]:")
        .NotRequired()
        .InstructionsText("[dim grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to accept)[/]")
        .HighlightStyle(new Style(Color.Cyan1))
        .AddChoices("csharp", "python", "node", "javascript", "typescript", "go", "rust"));

// ── Audit: AuditTypes ────────────────────────────────────────────────────────
AnsiConsole.WriteLine();
var selectedAuditTypes = AnsiConsole.Prompt(
    new MultiSelectionPrompt<string>()
        .Title("[bold yellow]Audit types[/] [dim](space to toggle, enter to confirm — can be empty)[/]:")
        .NotRequired()
        .InstructionsText("[dim grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to accept)[/]")
        .HighlightStyle(new Style(Color.Cyan1))
        .AddChoices("security", "architecture", "quality", "completeness", "cheating", "tests"));

// ── Network profiles ──────────────────────────────────────────────────────────
// Profile names come from CODEYBOX_NETWORK_PROFILES (comma-separated) when
// the operator has defined custom profiles in SandboxNetworkProfiles; falls
// back to the four built-in names if the env var is not set.
AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("[bold yellow]Network profiles[/] [dim](per-phase; skip to inherit from Defaults)[/]:");

string[] builtInProfiles = ["claude", "isolated", "internet", "internet-only"];
var envProfiles = Environment.GetEnvironmentVariable("CODEYBOX_NETWORK_PROFILES");
string[] availableProfiles = envProfiles is { Length: > 0 }
    ? envProfiles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    : builtInProfiles;

var profileHint = envProfiles is { Length: > 0 }
    ? $"[dim]Profiles (from CODEYBOX_NETWORK_PROFILES): {string.Join(" · ", availableProfiles)}[/]"
    : "[dim]Available built-ins: claude · isolated · internet · internet-only[/]";
AnsiConsole.MarkupLine(profileHint);
AnsiConsole.WriteLine();

const string SkipChoice = "(skip — use default)";
string[] profileChoices = [SkipChoice, .. availableProfiles];
string[] pipelinePhases = ["Work", "Rework", "AuditAgent", "AuditTool", "Merge"];
var phaseProfiles = new Dictionary<string, string?>(pipelinePhases.Length);

foreach (var phase in pipelinePhases)
{
    var selected = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title($"  [cyan]{phase}[/] profile:")
            .HighlightStyle(new Style(Color.Cyan1))
            .AddChoices(profileChoices));

    phaseProfiles[phase] = selected == SkipChoice ? null : selected;
}

// ── Assemble entry ────────────────────────────────────────────────────────────
AuditEntry? auditEntry = null;
{
    IReadOnlyList<string>? langs = selectedLanguages.Count > 0 ? selectedLanguages : null;
    IReadOnlyList<string>? types = selectedAuditTypes.Count > 0 ? selectedAuditTypes : null;
    if (langs is not null || types is not null)
        auditEntry = new AuditEntry { Languages = langs, AuditTypes = types };
}

NetworkProfilesEntry? networkEntry = null;
{
    var np = new NetworkProfilesEntry
    {
        Work = phaseProfiles["Work"],
        Rework = phaseProfiles["Rework"],
        AuditAgent = phaseProfiles["AuditAgent"],
        AuditTool = phaseProfiles["AuditTool"],
        Merge = phaseProfiles["Merge"],
    };
    if (np.Work is not null || np.Rework is not null || np.AuditAgent is not null ||
        np.AuditTool is not null || np.Merge is not null)
        networkEntry = np;
}

var entry = new ProjectEntry
{
    Id = projectId,
    DisplayName = displayName,
    RepositoryUrl = repositoryUrl,
    BaseBranch = baseBranch,
    Agent = agent,
    Upstream = upstream,
    Audit = auditEntry,
    NetworkProfiles = networkEntry,
};

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};
var json = JsonSerializer.Serialize(entry, jsonOptions);

// ── Display result ────────────────────────────────────────────────────────────
// When stdout is redirected (e.g. `dotnet run ... > snippet.json`), write
// plain JSON so the captured file is valid JSON. In an interactive terminal,
// render the fancy panel instead.
if (Console.IsOutputRedirected)
{
    Console.WriteLine(json);
}
else
{
    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule("[bold green]Generated entry[/]").RuleStyle(new Style(Color.Green)));
    AnsiConsole.WriteLine();
    AnsiConsole.Write(
        new Panel(new Text(json))
            .Header("Paste into CodeyBox.Projects[] in appsettings.json")
            .Expand());
}

// ── Save to file ──────────────────────────────────────────────────────────────
AnsiConsole.WriteLine();
if (AnsiConsole.Confirm("Save to file?", defaultValue: false))
{
    var rawPath = AnsiConsole.Ask<string>("Output file path:");
    var resolvedPath = Path.GetFullPath(rawPath);
    AnsiConsole.MarkupLine($"[dim]Resolved path:[/] [bold]{Markup.Escape(resolvedPath)}[/]");

    var doWrite = true;
    if (File.Exists(resolvedPath))
        doWrite = AnsiConsole.Confirm("[yellow]File already exists. Overwrite?[/]", defaultValue: false);

    if (doWrite)
    {
        try
        {
            File.WriteAllText(resolvedPath, json);
            AnsiConsole.MarkupLine($"[green]Written to[/] [bold]{Markup.Escape(resolvedPath)}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Could not write file:[/] {Markup.Escape(ex.Message)}");
        }
    }
    else
    {
        AnsiConsole.MarkupLine("[yellow]Write cancelled.[/]");
    }
}

AnsiConsole.WriteLine();
AnsiConsole.MarkupLine(
    "[dim]Done. Run [white]dotnet run --project src/CodeyBox.Cli[/] again to configure another project.[/]");

// ─────────────────────────────────────────────────────────────────────────────
// Types and helpers
// ─────────────────────────────────────────────────────────────────────────────

internal static partial class Wizard
{
    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,64}$")]
    internal static partial Regex ProjectIdRegex();

    // First char must be alphanumeric (mirrors Validation.ValidateBranchName).
    // Callers also check for ".." and ".lock" explicitly.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/\-]{0,199}$")]
    internal static partial Regex BranchNameRegex();

    internal static UpstreamEntry BuildGitHubUpstream()
    {
        var owner = AnsiConsole.Prompt(
            new TextPrompt<string>("  [bold]GitHub owner[/] (user or org):")
                .PromptStyle("green")
                .Validate(o =>
                {
                    if (string.IsNullOrWhiteSpace(o) || o[0] == '-')
                        return ValidationResult.Error("Owner must not be empty or start with '-'.");
                    if (o.AsSpan().IndexOfAny(['\n', '\r', '\0']) >= 0)
                        return ValidationResult.Error("Owner must not contain control characters.");
                    return ValidationResult.Success();
                }));
        var repo = AnsiConsole.Prompt(
            new TextPrompt<string>("  [bold]GitHub repository[/] name:")
                .PromptStyle("green")
                .Validate(r =>
                {
                    if (string.IsNullOrWhiteSpace(r) || r[0] == '-')
                        return ValidationResult.Error("Repository must not be empty or start with '-'.");
                    if (r.AsSpan().IndexOfAny(['\n', '\r', '\0']) >= 0)
                        return ValidationResult.Error("Repository must not contain control characters.");
                    return ValidationResult.Success();
                }));
        var tokenVar = AnsiConsole.Ask<string>("  [bold]Token env var[/] (env var holding the PAT):");
        return new UpstreamEntry
        {
            Kind = "github",
            GitHubOwner = owner,
            GitHubRepository = repo,
            TokenEnvVar = tokenVar,
        };
    }

    internal static UpstreamEntry BuildGenericUpstream()
    {
        var url = AnsiConsole.Prompt(
            new TextPrompt<string>("  [bold]Generic URL[/]:")
                .PromptStyle("green")
                .Validate(u =>
                {
                    if (string.IsNullOrWhiteSpace(u) || u[0] == '-')
                        return ValidationResult.Error("Enter a non-empty URL that does not start with '-'.");
                    if (u.AsSpan().IndexOfAny(['\n', '\r', '\0']) >= 0)
                        return ValidationResult.Error("URL must not contain control characters.");
                    if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                        u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        u.StartsWith("git@", StringComparison.OrdinalIgnoreCase) ||
                        u.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
                        Path.IsPathRooted(u))
                        return ValidationResult.Success();
                    return ValidationResult.Error(
                        "Must start with https://, http://, git@, ssh://, or be an absolute filesystem path.");
                }));
        var tokenVar = AnsiConsole.Prompt(
            new TextPrompt<string>("  [bold]Token env var[/] [dim](optional — press Enter to skip)[/]:")
                .AllowEmpty());
        return new UpstreamEntry
        {
            Kind = "git-generic",
            GenericUrl = url,
            TokenEnvVar = string.IsNullOrWhiteSpace(tokenVar) ? null : tokenVar,
        };
    }
}

internal sealed class ProjectEntry
{
    public string? Id { get; init; }
    public string? DisplayName { get; init; }
    public string? RepositoryUrl { get; init; }
    public string? BaseBranch { get; init; }
    public string? Agent { get; init; }
    public UpstreamEntry? Upstream { get; init; }
    public AuditEntry? Audit { get; init; }
    public NetworkProfilesEntry? NetworkProfiles { get; init; }
}

internal sealed class UpstreamEntry
{
    public string? Kind { get; init; }
    public string? GitHubOwner { get; init; }
    public string? GitHubRepository { get; init; }
    public string? GenericUrl { get; init; }
    public string? TokenEnvVar { get; init; }
}

internal sealed class AuditEntry
{
    public IReadOnlyList<string>? Languages { get; init; }
    public IReadOnlyList<string>? AuditTypes { get; init; }
}

internal sealed class NetworkProfilesEntry
{
    public string? Work { get; init; }
    public string? Rework { get; init; }
    public string? AuditAgent { get; init; }
    public string? AuditTool { get; init; }
    public string? Merge { get; init; }
}
