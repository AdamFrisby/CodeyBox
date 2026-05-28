using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Opencode;

/// <summary>
/// Fetches opencode model identifiers by running <c>opencode models</c> on the
/// host and parsing <c>provider/model</c> lines from stdout.
/// </summary>
public sealed partial class OpencodeModelListProbe : IAgentModelListProbe
{
    internal const int MaxModelIds = 1024;
    private const int MaxLoggedStderrChars = 500;

    private readonly IOpencodeCliRunner _runner;
    private readonly string _binary;
    private readonly ILogger<OpencodeModelListProbe>? _log;

    public AgentKind Kind => AgentKind.Opencode;

    public OpencodeModelListProbe(ILogger<OpencodeModelListProbe>? log = null)
        : this(new DefaultProcessRunner(), null, log)
    {
    }

    public OpencodeModelListProbe(
        IProcessRunner processRunner,
        string? binary = null,
        ILogger<OpencodeModelListProbe>? log = null)
        : this(new DefaultOpencodeCliRunner(processRunner), binary, log)
    {
    }

    internal OpencodeModelListProbe(
        IOpencodeCliRunner runner,
        string? binary = null,
        ILogger<OpencodeModelListProbe>? log = null)
    {
        _runner = runner;
        _binary = binary ?? ResolveBinary();
        _log = log;
    }

    private static string ResolveBinary() =>
        Environment.GetEnvironmentVariable("CODEYBOX_OPENCODE_BINARY") ?? "opencode";

    public async Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
    {
        try
        {
            var run = await _runner.RunModelsAsync(_binary, ct).ConfigureAwait(false);
            if (run.ExitCode == 1 && string.IsNullOrEmpty(run.Stdout) && string.IsNullOrEmpty(run.Stderr))
                return AgentModelListResult.Failed("opencode CLI failed to start");
            if (OpencodeCliErrors.IsCliNotFoundExitCode(run.ExitCode))
                return AgentModelListResult.Failed("opencode CLI not found");
            if (run.ExitCode != 0)
            {
                LogStderrAtDebug(run.Stderr, run.ExitCode);
                return AgentModelListResult.Failed($"opencode models exited {run.ExitCode}");
            }

            var ids = ParseModelsOutput(run.Stdout);
            if (ids.Count == 0)
            {
                LogStderrAtDebug(run.Stderr, exitCode: 0);
                _log?.LogDebug("opencode models produced no parseable model ids");
                return AgentModelListResult.Failed("no models parsed from opencode models output");
            }

            _log?.LogDebug("opencode models listed {Count} model id(s)", ids.Count);
            return AgentModelListResult.Success(ids);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return AgentModelListResult.Failed("timeout");
        }
        catch (Exception ex) when (OpencodeCliErrors.IsCliNotFound(ex))
        {
            return AgentModelListResult.Failed("opencode CLI not found");
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "opencode models probe failed");
            return AgentModelListResult.Failed($"opencode models failed ({ex.GetType().Name})");
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
            "opencode models stderr at exit {ExitCode} (len {Len}): {Stderr}",
            exitCode, trimmed.Length, capped);
    }

    /// <summary>
    /// Parses <c>provider/model</c> lines from CLI stdout. Ignores blank lines,
    /// banners, and preamble such as "Loading providers...".
    /// </summary>
    internal static IReadOnlyList<string> ParseModelsOutput(string stdout, int maxIds = MaxModelIds)
    {
        var ids = new List<string>(Math.Min(32, maxIds));
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (!ModelLineRegex().IsMatch(line)) continue;
            ids.Add(line);
            if (ids.Count >= maxIds) break;
        }
        return ids;
    }

    [GeneratedRegex("^[a-z0-9_.-]+/[a-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelLineRegex();
}
