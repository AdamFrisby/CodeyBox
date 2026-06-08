using System.Collections.Concurrent;
using CodeyBox.Agents.Claude;

namespace CodeyBox.Api;

/// <summary>
/// Pure binding helpers that copy a <see cref="ClaudeSessionOptions"/> snapshot
/// into the mutable <see cref="ClaudeSessionWorkerOptions"/> singleton the
/// worker reads on every dispatch. Extracted from <c>Program.cs</c> so the
/// hot-reload path (config string -> enum parse + override-dictionary swap) is
/// directly testable without standing up the full DI container.
/// </summary>
internal static class ClaudeSessionOptionsBinder
{
    public static void Apply(
        ClaudeSessionWorkerOptions target,
        ClaudeSessionOptions src)
    {
        target.Enabled = src.Enabled;
        target.EmitTurnMetrics = src.EmitTurnMetrics;
        target.Transport = ParseTransport(src.Transport);
        ReplaceOverrides(
            target.TransportOverridesByAgentClassMember,
            src.TransportOverridesByAgentClassMember);
        ReplaceOverrides(
            target.TransportOverridesByProject,
            src.TransportOverridesByProject);
    }

    public static ClaudeSessionTransport ParseTransport(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? ClaudeSessionTransport.Print
            : Enum.TryParse<ClaudeSessionTransport>(value, ignoreCase: true, out var parsed)
                ? parsed
                : ClaudeSessionTransport.Print;

    public static void ReplaceOverrides(
        ConcurrentDictionary<string, ClaudeSessionTransport> target,
        IReadOnlyDictionary<string, string>? source)
    {
        target.Clear();
        if (source is null) return;
        foreach (var (key, value) in source)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            target[key] = ParseTransport(value);
        }
    }
}
