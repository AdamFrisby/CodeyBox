using System.Text.RegularExpressions;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.CavemanCode;

/// <summary>
/// Fetches caveman-code model identifiers by running
/// <c>caveman-code --list-models</c> on the host and parsing the provider
/// table from stdout.
///
/// <para>The table shape (<c>provider  model  context  max-out  thinking
///   images</c>, columns joined with two spaces) and the
/// <c>No models available. Set API keys in environment variables.</c>
/// empty-state line are verified against 0.65.2. The registry answers
/// offline from environment keys alone, so the probe needs the operator to
/// have provider keys (or the <c>CODEYBOX_CAVEMAN_*</c> host vars) visible to
/// the API host; otherwise it fails gracefully and
/// <c>AgentClassConfigValidator</c> skips validation with a warning.</para>
/// </summary>
public sealed partial class CavemanCodeModelListProbe : IAgentModelListProbe
{
    internal const int MaxModelIds = 1024;
    private const int MaxLoggedStderrChars = 500;

    private readonly ICavemanCodeCliRunner _runner;
    private readonly string _binary;
    private readonly ILogger<CavemanCodeModelListProbe>? _log;

    public AgentKind Kind => AgentKind.CavemanCode;

    public CavemanCodeModelListProbe(
        ICavemanCodeCliRunner runner,
        string? binary = null,
        ILogger<CavemanCodeModelListProbe>? log = null)
    {
        _runner = runner;
        _binary = binary ?? ResolveBinary();
        _log = log;
    }

    private static string ResolveBinary() =>
        Environment.GetEnvironmentVariable("CODEYBOX_CAVEMANCODE_BINARY") ?? "caveman-code";

    public async Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
    {
        try
        {
            var run = await _runner.RunListModelsAsync(_binary, ct).ConfigureAwait(false);
            if (run.ExitCode == 1 && string.IsNullOrEmpty(run.Stdout) && string.IsNullOrEmpty(run.Stderr))
                return AgentModelListResult.Failed("caveman-code CLI failed to start");
            if (run.ExitCode == 127)
                return AgentModelListResult.Failed("caveman-code CLI not found");
            if (run.ExitCode != 0)
            {
                LogStderrAtDebug(run.Stderr, run.ExitCode);
                return AgentModelListResult.Failed($"caveman-code --list-models exited {run.ExitCode}");
            }

            var ids = ParseModelsOutput(run.Stdout);
            if (ids.Count == 0)
            {
                LogStderrAtDebug(run.Stderr, exitCode: 0);
                _log?.LogDebug("caveman-code --list-models produced no parseable model ids");
                return AgentModelListResult.Failed("no models parsed from caveman-code --list-models output");
            }

            _log?.LogDebug("caveman-code --list-models listed {Count} model id(s)", ids.Count);
            return AgentModelListResult.Success(ids);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return AgentModelListResult.Failed("timeout");
        }
        catch (Exception ex) when (ex is FileNotFoundException
            || (ex is System.ComponentModel.Win32Exception w32 && w32.NativeErrorCode == 2))
        {
            return AgentModelListResult.Failed("caveman-code CLI not found");
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "caveman-code --list-models probe failed");
            return AgentModelListResult.Failed($"caveman-code --list-models failed ({ex.GetType().Name})");
        }
    }

    private void LogStderrAtDebug(string stderr, int exitCode)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return;
        var trimmed = stderr.Trim();
        var capped = trimmed.Length > MaxLoggedStderrChars
            ? trimmed[..MaxLoggedStderrChars] + "…"
            : trimmed;
        _log?.LogDebug(
            "caveman-code --list-models stderr at exit {ExitCode} (len {Len}): {Stderr}",
            exitCode, trimmed.Length, capped);
    }

    /// <summary>
    /// Parses <c>provider/model</c> and bare <c>model</c> ids from the CLI
    /// table. Columns are joined with (at least) two spaces, so rows are
    /// split on multi-space runs: single-spaced log noise never yields two
    /// columns. The header row and the <c>No models available…</c>
    /// empty-state line match no provider charset and are skipped. Each row
    /// contributes the <c>provider/model</c> form first (the documented
    /// <c>--model</c> form needing no <c>--provider</c>) and the bare model
    /// id second, so both operator spellings validate.
    /// </summary>
    internal static IReadOnlyList<string> ParseModelsOutput(string stdout, int maxIds = MaxModelIds)
    {
        var ids = new List<string>(Math.Min(32, maxIds));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var columns = MultiSpaceRegex().Split(line.Trim());
            if (columns.Length < 2) continue;
            var provider = columns[0];
            var model = columns[1];
            // Header row ("provider  model  context …") matches the token
            // charsets below, so it is excluded by name, not by shape.
            if (provider.Equals("provider", StringComparison.OrdinalIgnoreCase)
                && model.Equals("model", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!ProviderTokenRegex().IsMatch(provider)) continue;
            if (!ModelTokenRegex().IsMatch(model)) continue;
            foreach (var id in new[] { $"{provider}/{model}", model })
            {
                if (seen.Add(id))
                {
                    ids.Add(id);
                    if (ids.Count >= maxIds) return ids;
                }
            }
        }

        return ids;
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex("^[a-z0-9_.-]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProviderTokenRegex();

    [GeneratedRegex("^[a-z0-9_.\\-/:+]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModelTokenRegex();
}
