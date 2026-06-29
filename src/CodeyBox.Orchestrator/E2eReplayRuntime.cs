using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Pure-replay (no LLM) execution engine for <see cref="E2eReplayArtifact"/>.
/// Runs the optional readiness probe, then steps, then assertions against a
/// live sandbox. Records pass/fail with enough detail that a dashboard can
/// surface the first failing step + the stdout/stderr tail without re-running
/// the case.
///
/// <para>The "cheap-model selector-repair fallback" the brief mentions is a
/// separate concern. The runtime exposes the seam by recording
/// <see cref="E2eRunResult.FailedStepIndex"/> on failure; a future post-fail
/// helper could read the seam and attempt a fix. This commit does not build
/// that hook.</para>
/// </summary>
public sealed class E2eReplayRuntime : IE2eReplayRuntime
{
    private readonly ILogger<E2eReplayRuntime> _logger;
    private const int OutputTailBytes = 4096;
    private const int OutputCaptureBytes = 16 * 1024;
    private const string ReplayDriverBinary = "codeybox-e2e-replay";
    private static readonly JsonSerializerOptions ArtifactJson = new(JsonSerializerDefaults.Web);

    public E2eReplayRuntime(ILogger<E2eReplayRuntime> logger)
    {
        _logger = logger;
    }

    public async Task<E2eRunResult> ExecuteAsync(E2eReplayArtifact artifact, ISandbox sandbox, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(sandbox);

        var sw = Stopwatch.StartNew();
        var stepResults = new List<E2eStepResult>();
        var assertionResults = new List<E2eAssertionResult>();

        if (!E2eReplayArtifactValidation.TryValidate(artifact, out var schemaKind, out var schemaDetail))
        {
            sw.Stop();
            return Fail(schemaDetail, schemaKind, failedIndex: -1, stepResults, assertionResults, sw.ElapsedMilliseconds);
        }

        if (artifact.Readiness is { Url.Length: > 0 } readiness)
        {
            var ready = await RunReadinessAsync(readiness, sandbox, ct);
            if (!ready.passed)
            {
                sw.Stop();
                return new E2eRunResult
                {
                    Passed = false,
                    Summary = $"readiness probe never succeeded: {ready.detail}",
                    FailureKind = "ReadinessProbe",
                    FailedStepIndex = null,
                    StepResults = stepResults,
                    AssertionResults = assertionResults,
                    DurationMs = sw.ElapsedMilliseconds,
                };
            }
        }

        if (artifact.Steps.Count == 0 && artifact.Assertions.Count == 0)
        {
            sw.Stop();
            return new E2eRunResult
            {
                Passed = true,
                Summary = $"readiness succeeded, {sw.ElapsedMilliseconds} ms",
                StepResults = stepResults,
                AssertionResults = assertionResults,
                DurationMs = sw.ElapsedMilliseconds,
            };
        }

        var replay = await RunReplayDriverAsync(artifact, sandbox, ct);
        sw.Stop();
        return replay with
        {
            Summary = replay.Passed
                ? $"{artifact.Steps.Count} steps, {artifact.Assertions.Count} assertions, {sw.ElapsedMilliseconds} ms"
                : replay.Summary,
            DurationMs = sw.ElapsedMilliseconds,
        };
    }

    private async Task<E2eRunResult> RunReplayDriverAsync(E2eReplayArtifact artifact, ISandbox sandbox, CancellationToken ct)
    {
        SandboxExecResult result;
        try
        {
            result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = [ReplayDriverBinary, "--artifact-json-stdin"],
                Stdin = JsonSerializer.Serialize(artifact, ArtifactJson),
                WorkingDirectory = "/work",
                MaxStdoutBytes = OutputCaptureBytes,
                MaxStderrBytes = OutputCaptureBytes,
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "E2E replay driver threw during exec.");
            return Fail($"replay driver threw: {ex.Message}", "ExecException", -1, [], [], 0);
        }

        var stepResults = BuildSyntheticStepResults(artifact, result);
        var assertionResults = BuildSyntheticAssertionResults(artifact, result);
        if (result.OutputLimitExceeded)
        {
            return Fail("replay driver exceeded output capture limit", "OutputLimitExceeded", -1, stepResults, assertionResults, 0);
        }

        if (result.ExitCode == 127)
        {
            return Fail("replay driver is not installed in the E2E image", "ReplayDriverUnavailable", -1, stepResults, assertionResults, 0);
        }

        if (result.ExitCode != 0)
        {
            return Fail($"replay driver exited {result.ExitCode}: {Tail(result.Stderr)}", "ReplayDriverFailed", -1, stepResults, assertionResults, 0);
        }

        return new E2eRunResult
        {
            Passed = true,
            Summary = "replay driver completed",
            StepResults = stepResults,
            AssertionResults = assertionResults,
        };
    }

    private static async Task<(bool passed, string detail)> RunReadinessAsync(E2eReadinessProbe probe, ISandbox sandbox, CancellationToken ct)
    {
        var attempts = Math.Max(1, probe.MaxAttempts);
        var delay = Math.Max(0, probe.DelayMs);
        SandboxExecResult? last = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["curl", "-fsS", "--max-time", "5", probe.Url!],
                    MaxStdoutBytes = OutputCaptureBytes,
                    MaxStderrBytes = OutputCaptureBytes,
                }, ct);
                last = result;
                if (result.ExitCode == 0)
                {
                    return (true, $"ready after {attempt + 1} attempts");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = new SandboxExecResult(-1, string.Empty, ex.Message);
            }

            if (attempt + 1 < attempts && delay > 0)
            {
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
            }
        }
        var tail = last is null ? "no attempts" : $"last exit {last.ExitCode}, stderr: {RawOutputRedactor.TruncateToBytes(Tail(last.Stderr), 200)}";
        return (false, tail);
    }

    private static IReadOnlyList<E2eStepResult> BuildSyntheticStepResults(E2eReplayArtifact artifact, SandboxExecResult result)
    {
        var passed = result.ExitCode == 0 && !result.OutputLimitExceeded;
        var count = Math.Max(1, artifact.Steps.Count);
        var rows = new List<E2eStepResult>(count);
        for (var i = 0; i < count; i++)
        {
            rows.Add(new E2eStepResult
            {
                ExitCode = result.ExitCode,
                StdoutTail = i == 0 ? Tail(result.Stdout) : string.Empty,
                StderrTail = i == 0 ? Tail(result.Stderr) : string.Empty,
                Passed = passed,
            });
        }
        return rows;
    }

    private static IReadOnlyList<E2eAssertionResult> BuildSyntheticAssertionResults(E2eReplayArtifact artifact, SandboxExecResult result)
    {
        var passed = result.ExitCode == 0 && !result.OutputLimitExceeded;
        var rows = new List<E2eAssertionResult>(artifact.Assertions.Count);
        foreach (var assertion in artifact.Assertions)
        {
            rows.Add(new E2eAssertionResult
            {
                Description = assertion.Description,
                Passed = passed,
                Detail = passed ? "ok" : Tail(result.Stderr),
            });
        }
        return rows;
    }

    private static E2eRunResult Fail(string summary, string kind, int failedIndex, IReadOnlyList<E2eStepResult> steps, IReadOnlyList<E2eAssertionResult> assertions, long durationMs)
        => new()
        {
            Passed = false,
            Summary = summary,
            FailureKind = kind,
            FailedStepIndex = failedIndex,
            StepResults = steps,
            AssertionResults = assertions,
            DurationMs = durationMs,
        };

    private static string Tail(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= OutputTailBytes)
            return RawOutputRedactor.Redact(s ?? string.Empty);
        return RawOutputRedactor.Redact(s[^OutputTailBytes..]);
    }
}
