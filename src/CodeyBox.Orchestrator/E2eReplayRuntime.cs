using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        if (artifact.Readiness is { } readiness && readiness.Argv.Count > 0)
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

        for (var i = 0; i < artifact.Steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = artifact.Steps[i];
            if (step.Argv.Count == 0)
            {
                _logger.LogWarning("E2E step {Index} has empty argv; treating as failure.", i);
                stepResults.Add(new E2eStepResult { Passed = false, ExitCode = -1, StdoutTail = string.Empty, StderrTail = "empty argv" });
                sw.Stop();
                return Fail("step had empty argv", "EmptyStep", i, stepResults, assertionResults, sw.ElapsedMilliseconds);
            }

            SandboxExecResult result;
            try
            {
                result = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = step.Argv,
                    Stdin = step.Stdin,
                    WorkingDirectory = step.WorkingDirectory,
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "E2E step {Index} threw during exec.", i);
                stepResults.Add(new E2eStepResult { Passed = false, ExitCode = -1, StdoutTail = string.Empty, StderrTail = ex.Message });
                sw.Stop();
                return Fail($"step {i} threw: {ex.Message}", "ExecException", i, stepResults, assertionResults, sw.ElapsedMilliseconds);
            }

            var passed = !step.FailOnNonZeroExit || result.ExitCode == 0;
            stepResults.Add(new E2eStepResult
            {
                ExitCode = result.ExitCode,
                StdoutTail = Tail(result.Stdout),
                StderrTail = Tail(result.Stderr),
                Passed = passed,
            });

            if (!passed)
            {
                sw.Stop();
                return Fail($"step {i} exited {result.ExitCode}", "StepFailed", i, stepResults, assertionResults, sw.ElapsedMilliseconds);
            }

            if (step.DelayAfterMs is { } delay && delay > 0)
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

        for (var i = 0; i < artifact.Assertions.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var assertion = artifact.Assertions[i];
            if (assertion.Argv.Count == 0)
            {
                assertionResults.Add(new E2eAssertionResult
                {
                    Description = assertion.Description,
                    Passed = false,
                    Detail = "empty argv",
                });
                sw.Stop();
                return Fail($"assertion {i} had empty argv", "EmptyAssertion", i, stepResults, assertionResults, sw.ElapsedMilliseconds);
            }

            SandboxExecResult result;
            try
            {
                result = await sandbox.ExecAsync(new SandboxExec { Argv = assertion.Argv }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "E2E assertion {Index} threw during exec.", i);
                assertionResults.Add(new E2eAssertionResult
                {
                    Description = assertion.Description,
                    Passed = false,
                    Detail = $"exec exception: {ex.Message}",
                });
                sw.Stop();
                return Fail($"assertion {i} threw: {ex.Message}", "AssertionException", i, stepResults, assertionResults, sw.ElapsedMilliseconds);
            }

            var (ok, detail) = EvaluateAssertion(assertion, result);
            assertionResults.Add(new E2eAssertionResult
            {
                Description = assertion.Description,
                Passed = ok,
                Detail = detail,
            });

            if (!ok)
            {
                sw.Stop();
                return Fail($"assertion {i} failed: {detail}", "AssertionFailed", i, stepResults, assertionResults, sw.ElapsedMilliseconds);
            }
        }

        sw.Stop();
        return new E2eRunResult
        {
            Passed = true,
            Summary = $"{artifact.Steps.Count} steps, {artifact.Assertions.Count} assertions, {sw.ElapsedMilliseconds} ms",
            StepResults = stepResults,
            AssertionResults = assertionResults,
            DurationMs = sw.ElapsedMilliseconds,
        };
    }

    private static (bool ok, string detail) EvaluateAssertion(E2eReplayAssertion a, SandboxExecResult r)
    {
        if (r.ExitCode != a.ExpectExitCode)
        {
            return (false, $"exit {r.ExitCode} != expected {a.ExpectExitCode}");
        }

        if (!string.IsNullOrEmpty(a.ExpectStdoutContains)
            && r.Stdout.IndexOf(a.ExpectStdoutContains, StringComparison.Ordinal) < 0)
        {
            return (false, $"stdout missing substring '{Truncate(a.ExpectStdoutContains, 80)}'");
        }

        if (!string.IsNullOrEmpty(a.ExpectStdoutNotContains)
            && r.Stdout.IndexOf(a.ExpectStdoutNotContains, StringComparison.Ordinal) >= 0)
        {
            return (false, $"stdout contained forbidden substring '{Truncate(a.ExpectStdoutNotContains, 80)}'");
        }

        return (true, "ok");
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
                var result = await sandbox.ExecAsync(new SandboxExec { Argv = probe.Argv }, ct);
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
        var tail = last is null ? "no attempts" : $"last exit {last.ExitCode}, stderr: {Truncate(last.Stderr, 200)}";
        return (false, tail);
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
            return s ?? string.Empty;
        return s[^OutputTailBytes..];
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? string.Empty : s[..max];
}
