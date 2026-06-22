using System.Text.Json;

namespace CodeyBox.Agents.Claude.AcpBridge;

/// <summary>
/// Typed view of the one-shot <c>hello</c> envelope the host writes to the
/// bridge's stdin at start-of-turn. Mirrors the JS bridge's <c>config</c>
/// object 1:1 so the host-side transport keeps shipping the same payload.
/// </summary>
internal sealed record BridgeConfig
{
    public required bool AutoApprovePermissions { get; init; }
    public required bool AutoAnswerQuestions { get; init; }
    public required string ClaudeBinary { get; init; }
    public required IReadOnlyList<string> ClaudeArgs { get; init; }
    public required string WorkingDirectory { get; init; }
    public required IReadOnlyDictionary<string, string> ClaudeEnv { get; init; }
    public required string? LockDir { get; init; }
    public required int TurnTimeoutSeconds { get; init; }

    /// <summary>
    /// Defaults match the JS bridge so the bridge can run even if the host
    /// forgets to ship a field (forward-compat with older hosts).
    /// </summary>
    public static BridgeConfig Default { get; } = new()
    {
        AutoApprovePermissions = true,
        AutoAnswerQuestions = true,
        ClaudeBinary = "claude",
        ClaudeArgs = Array.Empty<string>(),
        WorkingDirectory = Environment.CurrentDirectory,
        ClaudeEnv = new Dictionary<string, string>(StringComparer.Ordinal),
        LockDir = null,
        TurnTimeoutSeconds = 900,
    };

    /// <summary>
    /// Builds a config from a hello envelope JSON object. Missing fields fall
    /// back to the corresponding <see cref="Default"/> value.
    /// </summary>
    public static BridgeConfig FromHello(JsonElement root)
    {
        var args = new List<string>();
        if (root.TryGetProperty("claudeArgs", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in argsEl.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.String)
                {
                    var s = a.GetString();
                    if (s is not null) args.Add(s);
                }
            }
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("claudeEnv", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in envEl.EnumerateObject())
            {
                if (kv.Value.ValueKind == JsonValueKind.String)
                {
                    var v = kv.Value.GetString();
                    if (v is not null) env[kv.Name] = v;
                }
            }
        }

        return new BridgeConfig
        {
            AutoApprovePermissions = ReadBool(root, "autoApprovePermissions", Default.AutoApprovePermissions),
            AutoAnswerQuestions = ReadBool(root, "autoAnswerQuestions", Default.AutoAnswerQuestions),
            ClaudeBinary = ReadString(root, "claudeBinary", Default.ClaudeBinary),
            ClaudeArgs = args,
            WorkingDirectory = ReadString(root, "workingDirectory", Default.WorkingDirectory),
            ClaudeEnv = env,
            LockDir = root.TryGetProperty("lockDir", out var ld) && ld.ValueKind == JsonValueKind.String
                ? ld.GetString()
                : null,
            TurnTimeoutSeconds = Math.Max(10, ReadInt(root, "turnTimeoutSeconds", Default.TurnTimeoutSeconds)),
        };
    }

    private static bool ReadBool(JsonElement obj, string name, bool fallback)
    {
        if (!obj.TryGetProperty(name, out var el)) return fallback;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static int ReadInt(JsonElement obj, string name, int fallback)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            return fallback;
        return el.TryGetInt32(out var v) ? v : fallback;
    }

    private static string ReadString(JsonElement obj, string name, string fallback)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return fallback;
        return el.GetString() ?? fallback;
    }
}
