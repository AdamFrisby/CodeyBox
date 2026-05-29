using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Cursor;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CursorSmokeProbe"/>. Unlike Claude/Codex/Gemini's
/// smoke probes, this one performs no HTTP call — it only checks that the
/// credential bundle carries <c>CODEYBOX_CURSOR_AUTH_JSON</c> (or a host
/// auth file). Quota visibility is handled separately by
/// <see cref="CursorQuotaProbe"/>. These tests pin that contract so an
/// editor rename or inverted check would fail loudly.
/// </summary>
public sealed class CursorSmokeProbeTests
{
    private static CursorSmokeProbe NewProbe() =>
        new(NullLogger<CursorSmokeProbe>.Instance);

    private static AgentCredential CredWithAuthJson(string raw = "{\"token\":\"x\"}") =>
        new(AgentKind.Cursor,
            new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = raw },
            new Dictionary<string, string>());

    private static AgentCredential EmptyCred() =>
        new(AgentKind.Cursor,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

    [Fact]
    public async Task Kind_IsCursor()
    {
        Assert.Equal(AgentKind.Cursor, NewProbe().Kind);
    }

    [Fact]
    public async Task CredentialBundleContainsAuthJson_ReturnsOk()
    {
        var result = await NewProbe().SmokeTestAsync(CredWithAuthJson(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task CredentialBundleContainsAuthJson_OkRegardlessOfValueContents()
    {
        // The probe deliberately does not validate the JSON shape — that is
        // the runner's responsibility on the first real CLI invocation.
        // This pins that contract so a future "helpful" addition of JSON
        // parsing here would break the test and force a deliberate decision.
        var result = await NewProbe().SmokeTestAsync(CredWithAuthJson("not-even-json"), CancellationToken.None);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task EmptyCredentialBundle_ReturnsFail_WithConfigurationHint()
    {
        var result = await NewProbe().SmokeTestAsync(EmptyCred(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("no Cursor credential configured", result.FailureReason);
        // The failure reason must point operators at the two host-side env
        // var names they can set; otherwise a startup smoke-gate failure
        // gives them no actionable hint.
        Assert.Contains("CODEYBOX_CURSOR_AUTH_FILE", result.FailureReason);
        Assert.Contains("CODEYBOX_CURSOR_AUTH_JSON", result.FailureReason);
    }

    [Fact]
    public async Task CredentialWithUnrelatedEnvVars_ReturnsFail()
    {
        // A credential bundle that carries other env vars but lacks the
        // load-bearing CODEYBOX_CURSOR_AUTH_JSON key must fail the gate.
        var credential = new AgentCredential(
            AgentKind.Cursor,
            new Dictionary<string, string> { ["UNRELATED"] = "value" },
            new Dictionary<string, string>());

        var result = await NewProbe().SmokeTestAsync(credential, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("no Cursor credential configured", result.FailureReason);
    }

    [Fact]
    public async Task Duration_IsReported()
    {
        var result = await NewProbe().SmokeTestAsync(CredWithAuthJson(), CancellationToken.None);
        Assert.True(result.Duration >= TimeSpan.Zero);
    }
}
