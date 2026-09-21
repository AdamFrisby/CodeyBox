using System.ComponentModel;
using System.Text.Json;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Fetches devin model identifiers by running
/// <c>devin models list --format json</c> on the host and parsing the JSON
/// array. The catalog is account-scoped, so this requires the CLI to be
/// installed AND authenticated on the orchestrator host — the same
/// <c>devin auth login</c> that produced the credentials file we ship to the
/// sandbox. When the binary or auth is absent the probe reports Failed, and
/// model validation falls back to the <see cref="DevinKnownModels"/> seed.
/// </summary>
public sealed class DevinModelListProbe : IAgentModelListProbe
{
    internal const int MaxModelIds = 1024;
    private const int MaxLoggedStderrChars = 500;

    /// <summary>Linux/macOS ENOENT from <see cref="System.Diagnostics.Process.Start"/>.</summary>
    private const int LinuxEnoent = 2;

    private readonly IDevinCliRunner _runner;
    private readonly string _binary;
    private readonly ILogger<DevinModelListProbe>? _log;

    public AgentKind Kind => AgentKind.Devin;

    public DevinModelListProbe(
        IDevinCliRunner runner,
        string? binary = null,
        ILogger<DevinModelListProbe>? log = null)
    {
        _runner = runner;
        _binary = binary ?? ResolveBinary();
        _log = log;
    }

    private static string ResolveBinary() =>
        Environment.GetEnvironmentVariable("CODEYBOX_DEVIN_BINARY") ?? "devin";

    public async Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
    {
        try
        {
            var run = await _runner.RunModelsListAsync(_binary, ct).ConfigureAwait(false);
            if (run.ExitCode == 1 && string.IsNullOrEmpty(run.Stdout) && string.IsNullOrEmpty(run.Stderr))
                return AgentModelListResult.Failed("devin CLI failed to start");
            if (IsCliNotFoundExitCode(run.ExitCode))
                return AgentModelListResult.Failed("devin CLI not found");
            if (run.ExitCode != 0)
            {
                LogStderrAtDebug(run.Stderr, run.ExitCode);
                return AgentModelListResult.Failed($"devin models list exited {run.ExitCode}");
            }

            var ids = ParseModelsOutput(run.Stdout);
            if (ids.Count == 0)
            {
                LogStderrAtDebug(run.Stderr, exitCode: 0);
                _log?.LogDebug("devin models list produced no parseable model ids");
                return AgentModelListResult.Failed("no models parsed from devin models list output");
            }

            _log?.LogDebug("devin models list produced {Count} model id(s)", ids.Count);
            return AgentModelListResult.Success(ids);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return AgentModelListResult.Failed("timeout");
        }
        catch (Exception ex) when (IsCliNotFound(ex))
        {
            return AgentModelListResult.Failed("devin CLI not found");
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "devin models probe failed");
            return AgentModelListResult.Failed($"devin models list failed ({ex.GetType().Name})");
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
            "devin models list stderr at exit {ExitCode} (len {Len}): {Stderr}",
            exitCode, trimmed.Length, capped);
    }

    /// <summary>
    /// Parses <c>devin models list --format json</c> output. The emitted shape
    /// is a JSON array of ClientModelConfig objects whose id field is
    /// <c>model_or_alias</c> (falling back to <c>model_id</c>/
    /// <c>modelOrAlias</c> for protojson camelCase output). A bare array of
    /// strings, or a wrapper object carrying a <c>models</c> array, is
    /// tolerated so a CLI revision that reshapes the document degrades to a
    /// smaller list rather than zero ids.
    /// </summary>
    internal static IReadOnlyList<string> ParseModelsOutput(string stdout, int maxIds = MaxModelIds)
    {
        var ids = new List<string>(Math.Min(32, maxIds));
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("models", out var models))
            {
                CollectFromArray(models, ids, maxIds);
            }
            else
            {
                CollectFromArray(root, ids, maxIds);
            }
        }
        catch (JsonException)
        {
            // Unparseable output is surfaced by the caller as "no models
            // parsed" — returning an empty list keeps that contract intact.
        }
        return ids;
    }

    private static void CollectFromArray(JsonElement element, List<string> ids, int maxIds)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in element.EnumerateArray())
        {
            if (ids.Count >= maxIds) break;
            var id = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : TryGetModelId(item);
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (ids.Contains(id, StringComparer.Ordinal)) continue;
            ids.Add(id);
        }
    }

    private static readonly string[] ModelIdFields =
        ["model_or_alias", "modelOrAlias", "model_id", "modelId", "alias"];

    private static string? TryGetModelId(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var field in ModelIdFields)
        {
            if (item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }

    private static bool IsCliNotFound(Exception ex) =>
        ex is FileNotFoundException
        || (ex is Win32Exception w32 && w32.NativeErrorCode == LinuxEnoent);

    private static bool IsCliNotFoundExitCode(int exitCode) => exitCode == 127;
}
