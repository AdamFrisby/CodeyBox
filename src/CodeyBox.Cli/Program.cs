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
var displayName = AnsiConsole.Ask<string>("[bold yellow]Display name[/]:");

// ── Repository URL ────────────────────────────────────────────────────────────
var repositoryUrl = AnsiConsole.Prompt(
    new TextPrompt<string>("[bold yellow]Repository URL[/] [dim](git/https/ssh URL or filesystem path)[/]:")
        .PromptStyle("green")
        .Validate(url =>
        {
            if (!string.IsNullOrWhiteSpace(url) && url[0] != '-')
                return ValidationResult.Success();
            return ValidationResult.Error("Enter a non-empty URL that does not start with '-'.");
        }));

// ── Base branch ───────────────────────────────────────────────────────────────
var baseBranch = AnsiConsole.Prompt(
    new TextPrompt<string>("[bold yellow]Base branch[/]:")
        .DefaultValue("main"));

// ── Agent ─────────────────────────────────────────────────────────────────────
var agent = AnsiConsole.Prompt(
    new SelectionPrompt<string>()
        .Title("[bold yellow]Agent[/]:")
        .HighlightStyle(new Style(Color.Cyan1, decoration: Decoration.Bold))
        .AddChoices("claude", "copilot", "codex"));

// ── Upstream kind ─────────────────────────────────────────────────────────────
var upstreamKind = AnsiConsole.Prompt(
    new SelectionPrompt<string>()
        .Title("[bold yellow]Upstream kind[/]:")
        .HighlightStyle(new Style(Color.Cyan1, decoration: Decoration.Bold))
        .AddChoices("noop", "github", "git-generic"));

UpstreamEntry? upstream = upstreamKind switch
{
    "github" => Wizard.BuildGitHubUpstream(),
    "git-generic" => Wizard.BuildGenericUpstream(),
    _ => null,
};

// ── Audit: Languages ──────────────────────────────────────────────────────────
AnsiConsole.WriteLine();
var selectedLanguages = AnsiConsole.Prompt(
    new MultiSelectionPrompt<string>()
        .Title("[bold yellow]Audit languages[/] [dim](space to toggle, enter to confirm — can be empty)[/]:")
        .NotRequired()
        .InstructionsText("[dim grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to accept)[/]")
        .HighlightStyle(new Style(Color.Cyan1))
        .AddChoices("python", "typescript", "javascript", "go", "rust", "csharp", "ruby", "shell"));

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
AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("[bold yellow]Network profiles[/] [dim](per-phase; skip to inherit from Defaults)[/]:");
AnsiConsole.MarkupLine("[dim]Available built-ins: claude · isolated · internet · internet-only[/]");
AnsiConsole.WriteLine();

const string SkipChoice = "(skip — use default)";
string[] profileChoices = [SkipChoice, "claude", "isolated", "internet", "internet-only"];
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
AnsiConsole.WriteLine();
AnsiConsole.Write(new Rule("[bold green]Generated entry[/]").RuleStyle(new Style(Color.Green)));
AnsiConsole.WriteLine();
AnsiConsole.Write(
    new Panel(new Text(json))
        .Header("Paste into CodeyBox.Projects[] in appsettings.json")
        .Expand());

// ── Save to file ──────────────────────────────────────────────────────────────
AnsiConsole.WriteLine();
if (AnsiConsole.Confirm("Save to file?", defaultValue: false))
{
    var filePath = AnsiConsole.Ask<string>("Output file path:");
    File.WriteAllText(filePath, json);
    AnsiConsole.MarkupLine($"[green]Written to[/] [bold]{Markup.Escape(filePath)}[/]");
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

    internal static UpstreamEntry BuildGitHubUpstream()
    {
        var owner = AnsiConsole.Ask<string>("  [bold]GitHub owner[/] (user or org):");
        var repo = AnsiConsole.Ask<string>("  [bold]GitHub repository[/] name:");
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
        var url = AnsiConsole.Ask<string>("  [bold]Generic URL[/]:");
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
