using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly IOptionsMonitor<E2eExecutionOptions>? _options;
    private const int OutputTailBytes = 4096;
    private const int OutputCaptureBytes = 1024 * 1024;
    private const string ReplayDriverBinary = "node";
    private static readonly JsonSerializerOptions ArtifactJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public E2eReplayRuntime(
        ILogger<E2eReplayRuntime> logger,
        IOptionsMonitor<E2eExecutionOptions>? options = null)
    {
        _logger = logger;
        _options = options;
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

        if (!E2eReplayOriginPolicy.TryValidateReplayNavigationTargets(artifact, CurrentAllowedOrigins(), out var navigationDetail))
        {
            sw.Stop();
            return Fail(navigationDetail, "NavigationUrlRejected", failedIndex: -1, stepResults, assertionResults, sw.ElapsedMilliseconds);
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
                    Summary = ready.detail,
                    FailureKind = ready.failureKind,
                    FailedStepIndex = null,
                    StepResults = stepResults,
                    AssertionResults = assertionResults,
                    DurationMs = sw.ElapsedMilliseconds,
                };
            }
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
                Argv = [ReplayDriverBinary, "-e", ReplayDriverScript],
                Stdin = JsonSerializer.Serialize(ToReplayDriverInput(artifact), ArtifactJson),
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

        if (result.OutputLimitExceeded)
        {
            return Fail("replay driver exceeded output capture limit", "OutputLimitExceeded", -1, [], [], 0);
        }

        if (result.ExitCode == 127)
        {
            return Fail("node/playwright replay driver prerequisites are not installed in the E2E image", "ReplayDriverUnavailable", -1, [], [], 0);
        }

        var parsed = TryParseDriverResult(result.Stdout, out var driverResult, out var parseError);
        if (!parsed || driverResult is null)
        {
            if (result.ExitCode != 0)
            {
                return Fail($"replay driver exited {result.ExitCode}: {Tail(result.Stderr)}", "ReplayDriverFailed", -1, [], [], 0);
            }

            return Fail($"replay driver returned invalid result JSON: {parseError}", "ReplayDriverProtocolError", -1, [], [], 0);
        }

        var redactedDriverResult = RedactDriverResult(driverResult);

        if (result.ExitCode != 0 && redactedDriverResult.Passed)
        {
            return redactedDriverResult with
            {
                Passed = false,
                FailureKind = "ReplayDriverFailed",
                Summary = $"replay driver exited {result.ExitCode}: {Tail(result.Stderr)}",
                FailedStepIndex = -1,
            };
        }

        return redactedDriverResult;
    }

    private async Task<(bool passed, string failureKind, string detail)> RunReadinessAsync(E2eReadinessProbe probe, ISandbox sandbox, CancellationToken ct)
    {
        if (!E2eReplayOriginPolicy.TryValidateReadinessUrl(probe.Url, CurrentAllowedOrigins(), out var uri, out var rejectedDetail))
        {
            return (false, "ReadinessUrlRejected", rejectedDetail);
        }

        var resolved = await ResolveReadinessAddressAsync(uri!, sandbox, ct);
        if (!resolved.allowed)
        {
            return (false, resolved.failureKind, resolved.detail);
        }

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
                    Argv =
                    [
                        "curl",
                        "-fsS",
                        "--max-time",
                        "5",
                        "--resolve",
                        $"{uri!.IdnHost}:{E2eReplayOriginPolicy.EffectivePort(uri)}:{resolved.ip}",
                        uri.ToString(),
                    ],
                    MaxStdoutBytes = OutputCaptureBytes,
                    MaxStderrBytes = OutputCaptureBytes,
                }, ct);
                last = result;
                if (result.ExitCode == 0)
                {
                    return (true, string.Empty, $"ready after {attempt + 1} attempts");
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
        return (false, "ReadinessProbe", $"readiness probe never succeeded: {tail}");
    }

    private async Task<(bool allowed, string failureKind, string detail, string? ip)> ResolveReadinessAddressAsync(
        Uri uri,
        ISandbox sandbox,
        CancellationToken ct)
    {
        if (IPAddress.TryParse(uri.IdnHost, out var literal))
        {
            return E2eReplayOriginPolicy.IsBlockedMetadataIp(literal)
                ? (false, "ReadinessUrlRejected", $"readiness.url resolves to disallowed metadata address {literal}", null)
                : (true, string.Empty, string.Empty, literal.ToString());
        }

        SandboxExecResult result;
        try
        {
            result = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["getent", "ahosts", uri.IdnHost],
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
            return (false, "ReadinessProbe", $"readiness DNS resolution failed: {ex.Message}", null);
        }

        if (result.ExitCode != 0)
        {
            return (false, "ReadinessProbe", $"readiness DNS resolution failed: {Tail(result.Stderr)}", null);
        }

        IPAddress? first = null;
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (token is null || !IPAddress.TryParse(token, out var ip))
                continue;
            if (E2eReplayOriginPolicy.IsBlockedMetadataIp(ip))
                return (false, "ReadinessUrlRejected", $"readiness.url resolves to disallowed metadata address {ip}", null);
            first ??= ip;
        }

        return first is null
            ? (false, "ReadinessProbe", $"readiness DNS resolution returned no usable addresses for {uri.IdnHost}", null)
            : (true, string.Empty, string.Empty, first.ToString());
    }

    private ReplayDriverInput ToReplayDriverInput(E2eReplayArtifact artifact)
    {
        var allowed = CurrentAllowedOrigins();
        return new ReplayDriverInput(
            artifact.Name,
            artifact.Readiness,
            artifact.Steps,
            artifact.Assertions,
            allowed
                .Where(static origin => Uri.TryCreate(origin, UriKind.Absolute, out _))
                .Select(static origin => E2eReplayOriginPolicy.NormalizeOrigin(new Uri(origin)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
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

    private static E2eRunResult RedactDriverResult(E2eRunResult result)
        => result with
        {
            Summary = Tail(result.Summary),
            StepResults = result.StepResults
                .Select(static step => step with
                {
                    StdoutTail = Tail(step.StdoutTail),
                    StderrTail = Tail(step.StderrTail),
                })
                .ToArray(),
            AssertionResults = result.AssertionResults
                .Select(static assertion => assertion with
                {
                    Detail = Tail(assertion.Detail),
                })
                .ToArray(),
        };

    private static bool TryParseDriverResult(string stdout, out E2eRunResult? result, out string error)
    {
        result = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(stdout))
        {
            error = "stdout was empty";
            return false;
        }

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!line.StartsWith('{'))
                continue;

            try
            {
                result = JsonSerializer.Deserialize<E2eRunResult>(line, ResultJson);
                if (result is null)
                {
                    error = "JSON deserialized to null";
                    return false;
                }
                return true;
            }
            catch (JsonException ex)
            {
                error = ex.Message;
            }
        }

        if (string.IsNullOrEmpty(error))
            error = "stdout contained no JSON result line";
        return false;
    }

    private IReadOnlyList<string> CurrentAllowedOrigins() =>
        _options?.CurrentValue.AllowedReadinessOrigins
        ?? new E2eExecutionOptions().AllowedReadinessOrigins;

    private sealed record ReplayDriverInput(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("readiness")] E2eReadinessProbe? Readiness,
        [property: JsonPropertyName("steps")] IReadOnlyList<E2eReplayStep> Steps,
        [property: JsonPropertyName("assertions")] IReadOnlyList<E2eReplayAssertion> Assertions,
        [property: JsonPropertyName("__codeyboxAllowedOrigins")] IReadOnlyList<string> AllowedOrigins);

    private const string ReplayDriverScript =
        """
        const fs = require('fs');
        const dns = require('dns').promises;
        const net = require('net');

        function tail(value) {
          const s = String(value || '');
          return s.length > 4096 ? s.slice(s.length - 4096) : s;
        }

        function result(passed, summary, failureKind, failedStepIndex, stepResults, assertionResults, startedAt) {
          return {
            passed,
            summary,
            failedStepIndex,
            failureKind,
            stepResults,
            assertionResults,
            durationMs: Date.now() - startedAt
          };
        }

        function okStep() {
          return { exitCode: 0, stdoutTail: '', stderrTail: '', passed: true };
        }

        function failStep(error) {
          return { exitCode: 1, stdoutTail: '', stderrTail: tail(error && error.stack ? error.stack : error), passed: false };
        }

        function okAssertion(assertion) {
          return { description: assertion.description || null, passed: true, detail: 'ok' };
        }

        function failAssertion(assertion, error) {
          return { description: assertion.description || null, passed: false, detail: tail(error && error.stack ? error.stack : error) };
        }

        function normalizeOrigin(raw) {
          const u = new URL(String(raw));
          if (u.protocol !== 'http:' && u.protocol !== 'https:') throw new Error(`non-http URL is not allowed: ${raw}`);
          if (u.username || u.password) throw new Error(`userinfo is not allowed in URL: ${raw}`);
          return u.origin;
        }

        function buildAllowedOriginSet(artifact) {
          const origins = Array.isArray(artifact.__codeyboxAllowedOrigins) ? artifact.__codeyboxAllowedOrigins : [];
          return new Set(origins.map(normalizeOrigin));
        }

        function ensureAllowedUrl(raw, allowedOrigins, label) {
          const origin = normalizeOrigin(raw);
          if (!allowedOrigins.has(origin)) throw new Error(`${label} origin ${origin} is not allowed`);
          return origin;
        }

        function normalizeAddress(address) {
          let value = String(address || '').trim().toLowerCase();
          if (value.startsWith('[') && value.endsWith(']')) value = value.slice(1, -1);
          return value;
        }

        function isMetadataAddress(address) {
          const value = normalizeAddress(address);
          return value === '169.254.169.254'
            || value === '::ffff:169.254.169.254'
            || value === '::169.254.169.254'
            || value === '::ffff:a9fe:a9fe'
            || value === '0:0:0:0:0:ffff:a9fe:a9fe'
            || value === '::a9fe:a9fe'
            || value === 'fd00:ec2::254'
            || value === 'fd00:ec2:0:0:0:0:0:254'
            || value === 'fe80::a9fe:a9fe'
            || value === 'fe80:0:0:0:0:0:a9fe:a9fe';
        }

        async function buildHostResolverRules(allowedOrigins) {
          const rules = [];
          for (const origin of allowedOrigins) {
            const u = new URL(origin);
            const host = normalizeAddress(u.hostname);
            if (net.isIP(host)) {
              if (isMetadataAddress(host)) throw new Error(`allowed origin resolves to blocked metadata address ${host}`);
              continue;
            }

            let records;
            try {
              records = await dns.lookup(host, { all: true });
            } catch {
              continue;
            }

            const usable = records.find(r => !isMetadataAddress(r.address));
            if (!usable && records.length > 0) throw new Error(`allowed origin resolves only to blocked metadata addresses: ${host}`);
            if (usable) rules.push(`MAP ${host} ${usable.address}`);
          }
          return rules.length > 0 ? rules.join(',') : null;
        }

        async function performStep(page, step, allowedOrigins) {
          const action = String(step.action || '').toLowerCase();
          if (action === 'navigate') {
            ensureAllowedUrl(step.target, allowedOrigins, 'navigate target');
            await page.goto(step.target, { waitUntil: 'domcontentloaded' });
            ensureAllowedUrl(page.url(), allowedOrigins, 'final navigation URL');
          }
          else if (action === 'click') await page.locator(step.selector).click();
          else if (action === 'doubleclick') await page.locator(step.selector).dblclick();
          else if (action === 'fill') await page.locator(step.selector).fill(step.value || '');
          else if (action === 'press') await page.locator(step.selector).press(step.value || '');
          else if (action === 'select') await page.locator(step.selector).selectOption(step.value || '');
          else if (action === 'check') await page.locator(step.selector).check();
          else if (action === 'uncheck') await page.locator(step.selector).uncheck();
          else if (action === 'hover') await page.locator(step.selector).hover();
          else if (action === 'waitforselector') await page.locator(step.selector).waitFor({ state: 'visible' });
          else if (action === 'wait') {
            const ms = Number(step.value || step.target || step.delayAfterMs || 1000);
            await page.waitForTimeout(Number.isFinite(ms) && ms >= 0 ? ms : 1000);
            return;
          } else {
            throw new Error(`unsupported action: ${step.action}`);
          }
          if (step.delayAfterMs) await page.waitForTimeout(Number(step.delayAfterMs));
        }

        async function performAssertion(page, assertion) {
          const kind = String(assertion.kind || '').toLowerCase();
          if (kind === 'selectorvisible') {
            if (!(await page.locator(assertion.selector).first().isVisible())) throw new Error(`${assertion.selector} is not visible`);
          } else if (kind === 'selectorhidden') {
            if (await page.locator(assertion.selector).first().isVisible()) throw new Error(`${assertion.selector} is visible`);
          } else if (kind === 'selectortextcontains') {
            const text = await page.locator(assertion.selector).first().textContent();
            if (!String(text || '').includes(assertion.value || '')) throw new Error(`${assertion.selector} text did not contain expected value`);
          } else if (kind === 'urlcontains') {
            if (!page.url().includes(assertion.value || '')) throw new Error(`url did not contain ${assertion.value}`);
          } else if (kind === 'titlecontains') {
            const title = await page.title();
            if (!title.includes(assertion.value || '')) throw new Error(`title did not contain ${assertion.value}`);
          } else {
            throw new Error(`unsupported assertion kind: ${assertion.kind}`);
          }
        }

        (async () => {
          const startedAt = Date.now();
          const stepResults = [];
          const assertionResults = [];
          let output;
          let exitCode = 0;
          let browser;
          let artifact;
          try {
            artifact = JSON.parse(fs.readFileSync(0, 'utf8'));
            const allowedOrigins = buildAllowedOriginSet(artifact);
            if (allowedOrigins.size === 0) throw new Error('no allowed replay origins configured');
            let chromium;
            try {
              chromium = require('playwright').chromium;
            } catch (error) {
              output = result(false, `playwright is not installed: ${error.message}`, 'ReplayDriverUnavailable', -1, stepResults, assertionResults, startedAt);
              exitCode = 127;
              return;
            }

            const resolverRules = await buildHostResolverRules(allowedOrigins);
            const launchOptions = { headless: true };
            if (resolverRules) launchOptions.args = [`--host-resolver-rules=${resolverRules}`];
            browser = await chromium.launch(launchOptions);
            const context = await browser.newContext();
            await context.route('**/*', route => {
              const url = route.request().url();
              try {
                ensureAllowedUrl(url, allowedOrigins, 'request');
                return route.continue();
              } catch {
                return route.abort('blockedbyclient');
              }
            });
            const page = await context.newPage();
            for (let i = 0; i < (artifact.steps || []).length; i++) {
              try {
                await performStep(page, artifact.steps[i], allowedOrigins);
                stepResults.push(okStep());
              } catch (error) {
                stepResults.push(failStep(error));
                output = result(false, `step ${i} failed: ${error.message}`, 'StepFailed', i, stepResults, assertionResults, startedAt);
                exitCode = 1;
                return;
              }
            }
            for (let i = 0; i < (artifact.assertions || []).length; i++) {
              try {
                await performAssertion(page, artifact.assertions[i]);
                assertionResults.push(okAssertion(artifact.assertions[i]));
              } catch (error) {
                assertionResults.push(failAssertion(artifact.assertions[i], error));
                output = result(false, `assertion ${i} failed: ${error.message}`, 'AssertionFailed', (artifact.steps || []).length + i, stepResults, assertionResults, startedAt);
                exitCode = 1;
                return;
              }
            }
            output = result(true, `${(artifact.steps || []).length} steps, ${(artifact.assertions || []).length} assertions`, null, null, stepResults, assertionResults, startedAt);
          } catch (error) {
            output = result(false, `replay driver crashed: ${error.message}`, 'ReplayDriverFailed', -1, stepResults, assertionResults, startedAt);
            exitCode = 1;
          } finally {
            if (browser) await browser.close();
            console.log(JSON.stringify(output));
            process.exit(exitCode);
          }
        })();
        """;
}
