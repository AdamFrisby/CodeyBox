using System.Diagnostics;
using CodeyBox.Core;

namespace CodeyBox.Tests.Integration.AgentSuspendResilience;

/// <summary>
/// Gate for real-runtime suspend smoke tests. Enabled only when
/// <c>CODEYBOX_RUN_AGENT_SUSPEND_SMOKE=1</c>, multipass is on PATH, and the
/// agent's host credential env vars are present.
/// </summary>
internal static class AgentSuspendSmokeEnvironment
{
    public const string EnableVariable = "CODEYBOX_RUN_AGENT_SUSPEND_SMOKE";

    public static readonly AgentKind[] AllAgents =
    [
        AgentKind.Claude,
        AgentKind.Codex,
        AgentKind.Gemini,
        AgentKind.Cursor,
        AgentKind.Opencode,
    ];

    public static readonly int[] SuspendDurationsSeconds = [5, 60, 120, 300];

    private static readonly bool MultipassOnPath = ProbeMultipass();

    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal)
        && MultipassOnPath;

    public static string? SkipReason(AgentKind agent)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal))
            return $"{EnableVariable} is not set to 1";
        if (!MultipassOnPath)
            return "multipass CLI is not available on PATH";
        if (!HasCredential(agent))
            return $"no host credential configured for agent '{agent.Value}'";
        return null;
    }

    public static bool HasCredential(AgentKind agent) =>
        TryBuildSandboxEnvironment(agent) is not null;

    /// <summary>
    /// Maps host credential env vars into sandbox-side names expected by agent CLIs.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? TryBuildSandboxEnvironment(AgentKind agent)
    {
        if (agent == AgentKind.Claude)
        {
            var key = Environment.GetEnvironmentVariable("CODEYBOX_CLAUDE_API_KEY")
                ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            return string.IsNullOrEmpty(key)
                ? null
                : new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = key };
        }

        if (agent == AgentKind.Codex)
        {
            var key = Environment.GetEnvironmentVariable("CODEYBOX_CODEX_API_KEY")
                ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            return string.IsNullOrEmpty(key)
                ? null
                : new Dictionary<string, string> { ["OPENAI_API_KEY"] = key };
        }

        if (agent == AgentKind.Gemini)
        {
            var key = Environment.GetEnvironmentVariable("CODEYBOX_GEMINI_API_KEY")
                ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            return string.IsNullOrEmpty(key)
                ? null
                : new Dictionary<string, string> { ["GEMINI_API_KEY"] = key };
        }

        if (agent == AgentKind.Cursor)
        {
            var json = Environment.GetEnvironmentVariable("CODEYBOX_CURSOR_AUTH_JSON");
            if (string.IsNullOrEmpty(json))
            {
                var path = Environment.GetEnvironmentVariable("CODEYBOX_CURSOR_AUTH_FILE")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "credentials.json");
                if (File.Exists(path))
                    json = File.ReadAllText(path);
            }
            return string.IsNullOrEmpty(json)
                ? null
                : new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = json };
        }

        if (agent == AgentKind.Opencode)
        {
            var json = Environment.GetEnvironmentVariable("OPENCODE_AUTH_JSON");
            if (string.IsNullOrEmpty(json))
            {
                var path = Environment.GetEnvironmentVariable("CODEYBOX_OPENCODE_AUTH_FILE");
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    json = File.ReadAllText(path);
            }
            return string.IsNullOrEmpty(json)
                ? null
                : new Dictionary<string, string> { ["OPENCODE_AUTH_JSON"] = json };
        }

        return null;
    }

    public static string LowCostModelId(AgentKind agent) => agent.Value switch
    {
        "claude" => "claude-haiku-4-5-20251001",
        "codex" => "gpt-4o-mini",
        "gemini" => "gemini-2.0-flash",
        "cursor" => "composer-2.5",
        "opencode" => "deepseek/deepseek-coder",
        _ => throw new ArgumentOutOfRangeException(nameof(agent)),
    };

    public static IAgentRunner CreateRunner(AgentKind agent) => agent.Value switch
    {
        "claude" => new CodeyBox.Agents.Claude.ClaudeAgentRunner(),
        "codex" => new CodeyBox.Agents.Codex.CodexAgentRunner(),
        "gemini" => new CodeyBox.Agents.Gemini.GeminiAgentRunner(),
        "cursor" => new CodeyBox.Agents.Cursor.CursorAgentRunner(),
        "opencode" => new CodeyBox.Agents.Opencode.OpencodeAgentRunner(),
        _ => throw new ArgumentOutOfRangeException(nameof(agent)),
    };

    private static bool ProbeMultipass()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "multipass",
                ArgumentList = { "version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return false;
            p.WaitForExit(5_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
