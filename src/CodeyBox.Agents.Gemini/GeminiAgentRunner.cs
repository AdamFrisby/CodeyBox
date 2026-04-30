using System.Text.RegularExpressions;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Agents.Gemini;

/// <summary>
/// Drives the Google Gemini CLI (@google/gemini-cli) in non-interactive mode.
/// The agent is expected to be installed in the sandbox image; the host
/// injects the API key via GEMINI_API_KEY.
/// </summary>
public sealed class GeminiAgentRunner : CliAgentRunnerBase
{
    // @google/gemini-cli emits ANSI colour codes and progress spinners to
    // stderr (and occasionally stdout) even in non-TTY mode. Strip them so
    // the audit log stays clean and SIEM tools are not confused.
    private static readonly Regex AnsiEscape = new(
        @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public override AgentKind Kind => AgentKind.Gemini;

    /// <summary>
    /// Path to the gemini binary inside the sandbox. Override only if the
    /// sandbox image installs it elsewhere.
    /// </summary>
    public string Binary { get; init; } = "gemini";

    protected override AgentInvocation BuildInvocation(string prompt, AgentCredential? credential, string? modelId = null)
    {
        // gemini --yolo -p "<prompt>": sends a single non-interactive prompt and exits.
        // --yolo skips all tool-use confirmation prompts — appropriate inside the
        // sandbox where the VM boundary is the permission boundary.
        var argv = new List<string> { Binary, "--yolo" };
        if (!string.IsNullOrEmpty(modelId))
        {
            argv.Add("--model");
            argv.Add(modelId);
        }
        argv.Add("-p");
        argv.Add(prompt);
        return new AgentInvocation(argv);
    }

    public override async Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        CancellationToken ct = default)
    {
        var result = await base.RunAsync(sandbox, workingDirectory, prompt, credential, modelId, ct);
        return result with
        {
            Stdout = Strip(result.Stdout),
            Stderr = Strip(result.Stderr),
        };
    }

    private static string? Strip(string? s) =>
        s is null ? null : AnsiEscape.Replace(s, string.Empty);
}
