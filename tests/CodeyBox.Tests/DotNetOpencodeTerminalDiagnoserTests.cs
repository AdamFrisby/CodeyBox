using CodeyBox.Agents.DotNetOpencode;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="DotNetOpencodeTerminalDiagnoser"/> (internal — the
/// test project has InternalsVisibleTo). Error frames pin the shapes recorded
/// live against 0.1.0-ci.20260905083303.33955573552.1; infrastructure text
/// pins the shell/runtime failures that must surface as a named cause rather
/// than "produced no changes".
/// </summary>
public sealed class DotNetOpencodeTerminalDiagnoserTests
{
    [Fact]
    public void NullAndEmpty_ReturnsNull()
    {
        Assert.Null(DotNetOpencodeTerminalDiagnoser.TryExtractTerminalError(null, null));
        Assert.Null(DotNetOpencodeTerminalDiagnoser.TryExtractTerminalError("", ""));
    }

    [Fact]
    public void RecordedProvider401_LiftedWithKindMessageAndStatus()
    {
        // Recorded live 2026-09-16 (bogus Anthropic key).
        const string stdout =
            "{\"type\":\"step_start\",\"timestamp\":1789512734124,\"sessionID\":\"ses_0a74541ec001ry8nCxA7V2gTzb\",\"part\":{\"id\":\"prt_0a74555ac001QmCuITT448cuhf\",\"sessionID\":\"ses_0a74541ec001ry8nCxA7V2gTzb\",\"messageID\":\"msg_0a745489a001WHQtkGpO5IBS2h\",\"type\":\"step-start\"}}\n" +
            "{\"type\":\"error\",\"timestamp\":1789512734146,\"sessionID\":\"ses_0a74541ec001ry8nCxA7V2gTzb\",\"error\":{\"type\":\"provider.auth\",\"message\":\"Provider request failed with HTTP 401.\",\"status\":401}}\n";

        var error = DotNetOpencodeTerminalDiagnoser.TryExtractTerminalError(stdout, null);

        Assert.NotNull(error);
        Assert.Contains("provider.auth", error!, StringComparison.Ordinal);
        Assert.Contains("HTTP 401", error!, StringComparison.Ordinal);
        Assert.Contains("401", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordedNoModel_Lifted()
    {
        // Recorded live 2026-09-16 (no configured model).
        const string stdout =
            "{\"type\":\"error\",\"timestamp\":1789512652804,\"sessionID\":\"ses_0a7440db5001svgNvxOKaMl9i7\",\"error\":{\"type\":\"provider.invalid-request\",\"message\":\"No available model is present in the configured catalog.\"}}\n";

        var error = DotNetOpencodeTerminalDiagnoser.TryExtractTerminalError(stdout, null);

        Assert.NotNull(error);
        Assert.Contains("provider.invalid-request", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstErrorFrame_Wins()
    {
        const string stdout =
            "{\"type\":\"error\",\"timestamp\":1,\"sessionID\":\"ses_1\",\"error\":{\"type\":\"provider.auth\",\"message\":\"first\"}}\n" +
            "{\"type\":\"error\",\"timestamp\":2,\"sessionID\":\"ses_1\",\"error\":{\"type\":\"unknown\",\"message\":\"second\"}}\n";

        var error = DotNetOpencodeTerminalDiagnoser.TryExtractTerminalError(stdout, null);

        Assert.NotNull(error);
        Assert.Contains("first", error!, StringComparison.Ordinal);
        Assert.DoesNotContain("second", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingBinaryOnStderr_NamesTheCause()
    {
        // exit 127 inside the guest: the binary name in argv is what the
        // shell reports, so the diagnostic names the broken install rather
        // than a model decision.
        const string stderr = "bash: line 1: dotnet-opencode: command not found\n";

        var error = DotNetOpencodeTerminalDiagnoser.TryExtractTerminalError("done\n", stderr);

        Assert.NotNull(error);
        Assert.Contains("dotnet-opencode", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRuntimeOnStderr_NamesTheCause()
    {
        const string stderr = "You must install or update .NET to run this application.\nFramework: 'Microsoft.NETCore.App', version '11.0.0-preview.7.26381.103' (x64)\n";

        var error = DotNetOpencodeTerminalDiagnoser.TryExtractTerminalError(string.Empty, stderr);

        Assert.NotNull(error);
        Assert.Contains(".NET", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRipgrepOnStderr_NamesTheCause()
    {
        // Recorded live 2026-09-16 (default-format run without rg on PATH).
        const string stderr = "Error: Ripgrep is unavailable on PATH and in the existing OpenCode binary cache. Supply OPENCODE_DOTNET_RIPGREP as an absolute executable path; this host does not download or substitute a shell.\n";

        var error = DotNetOpencodeTerminalDiagnoser.TryExtractTerminalError(string.Empty, stderr);

        Assert.NotNull(error);
        Assert.Contains("Ripgrep", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanRun_ReturnsNull()
    {
        const string stdout =
            "{\"type\":\"step_start\",\"timestamp\":1,\"sessionID\":\"ses_1\",\"part\":{\"id\":\"prt_1\",\"sessionID\":\"ses_1\",\"messageID\":\"msg_1\",\"type\":\"step-start\"}}\n" +
            "{\"type\":\"step_finish\",\"timestamp\":2,\"sessionID\":\"ses_1\",\"part\":{\"id\":\"prt_1\",\"sessionID\":\"ses_1\",\"messageID\":\"msg_1\",\"type\":\"step-finish\",\"tokens\":{\"input\":10,\"output\":5,\"cache\":{\"read\":0,\"write\":0}}}}\n";

        Assert.Null(DotNetOpencodeTerminalDiagnoser.TryExtractTerminalError(stdout, "some benign log\n"));
    }

    [Fact]
    public void LongErrorMessage_TruncatedToCap()
    {
        var longMessage = new string('x', DotNetOpencodeTerminalDiagnoser.MaxDiagnosticChars + 100);
        var stdout = "{\"type\":\"error\",\"timestamp\":1,\"sessionID\":\"ses_1\",\"error\":{\"type\":\"unknown\",\"message\":\"" + longMessage + "\"}}\n";

        var error = DotNetOpencodeTerminalDiagnoser.TryExtractTerminalError(stdout, null);

        Assert.NotNull(error);
        Assert.True(error!.Length <= DotNetOpencodeTerminalDiagnoser.MaxDiagnosticChars + 10);
    }
}
