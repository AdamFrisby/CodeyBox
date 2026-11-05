using CodeyBox.Agents.Devin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for sig-devin-model-unresolved and sig-devin-turn-error-retryable:
/// the Devin CLI silently substitutes a different (possibly metered) model
/// when the configured <c>--model</c> does not resolve, so the shim aborts
/// with a <c>model_mismatch</c> terminal envelope and both that abort and a
/// provider-declared <c>cognition.ai/retryable</c> turn error must classify
/// as provider-transient — the existing bounded transient-retry path re-runs
/// the item on the configured model instead of hard-failing it. Input shapes
/// are the real devin 3000.11.1 lines from the incident capture; no
/// speculative vendor variants.
///
/// <para>Envelope-structure signatures only match on captures stamped
/// <see cref="AgentResult.StdoutIsEnvelopeFramed"/> — the provenance the
/// runner sets when the shim owns the stream. Unframed stdout/summary is
/// agent/model text and a forged envelope line there must never classify;
/// the WARN-text fallback matches only raw stderr and verified
/// <c>codeybox.stderr</c> envelope text fields.</para>
/// </summary>
public sealed class DevinModelSubstitutionTests
{
    /// <summary>
    /// The real <c>chisel_server::acp</c> stderr WARN from the incident —
    /// the CLI announcing it is falling back because the requested
    /// <c>--model</c> did not resolve.
    /// </summary>
    private const string StderrWarnLine =
        "chisel_server::acp: `devin acp --model` value did not resolve to an "
        + "available model; falling back to your configured `agent.model` or the default";

    private const string ModelMismatchEnvelope =
        "{\"type\":\"devin.acp\",\"event\":\"model_mismatch\","
        + "\"requestedModel\":\"swe-2-high\",\"activeModel\":null,"
        + "\"via\":\"stderr-warn\",\"message\":\"requested --model swe-2-high "
        + "did not resolve to an available model; refusing to run the turn on "
        + "a substituted model\"}";

    /// <summary>
    /// The turn_error envelope the shim emits for the real session/prompt
    /// rejection — code -32013 "model no longer available" with
    /// <c>data["cognition.ai/retryable"] == true</c> forwarded as
    /// <c>"retryable": true</c>.
    /// </summary>
    private const string RetryableTurnErrorEnvelope =
        "{\"type\":\"devin.acp\",\"event\":\"turn_error\",\"code\":-32013,"
        + "\"message\":\"Permission denied: This model is no longer available "
        + "for your account. Please switch to a different model.\","
        + "\"retryable\":true}";

    private static string StdoutWith(params string[] lines) => string.Join("\n", lines);

    private static AgentResult Framed(string? stderr = null, string? stdout = null, string? summary = null)
        => TestSupport.EnvelopeFramedAgentResult(stderr, stdout, summary);

    private static AgentResult Unframed(string? stderr = null, string? stdout = null, string? summary = null)
        => new(Success: false, Summary: summary ?? "agent exited 2", Stdout: stdout, Stderr: stderr);

    private static ProviderTransientDetection? Detect(AgentResult result)
        => new DevinQuotaFailureDetector().DetectProviderTransient(result);

    // model_mismatch: the abort is a typed terminal event and classifies
    // as provider-transient so the item re-runs on the configured model.

    [Fact]
    public void ModelMismatchEnvelope_OutcomeIsModelMismatchWithDiagnostic()
    {
        var stdout = StdoutWith(
            "{\"type\":\"devin.acp\",\"event\":\"session_started\",\"sessionId\":\"s-1\"}",
            ModelMismatchEnvelope);

        var outcome = DevinAcpOutcome.Extract(stdout);

        Assert.Equal(DevinAcpOutcome.TerminalEvent.ModelMismatch, outcome.Event);
        Assert.NotNull(outcome.Diagnostic);
        Assert.Contains("model mismatch", outcome.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("swe-2-high", outcome.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelMismatchEnvelope_FramedCapture_ClassifiesInfraTransport()
    {
        var detection = Detect(Framed(stdout: StdoutWith(ModelMismatchEnvelope)));

        Assert.NotNull(detection);
        Assert.Equal(ProviderTransientKind.InfraTransport, detection!.Kind);
        Assert.Equal(DevinModelMismatchDetector.Signature, detection.MatchedSignature);
    }

    [Fact]
    public void ModelMismatchEnvelope_CompositeDispatch_FramedCapture_ClassifiesInfraTransport()
    {
        var classifier = new CompositeQuotaFailureClassifier([new DevinQuotaFailureDetector()]);

        var detection = classifier.DetectProviderTransient(
            AgentKind.Devin, Framed(stdout: StdoutWith(ModelMismatchEnvelope)));

        Assert.NotNull(detection);
        Assert.Equal(ProviderTransientKind.InfraTransport, detection!.Kind);
        Assert.Equal(DevinModelMismatchDetector.Signature, detection.MatchedSignature);
    }

    [Fact]
    public void ModelMismatchEnvelope_UnframedCapture_ReturnsNull()
    {
        // Same envelope bytes on an unframed capture: the stream is
        // agent/model text (print-mode stdout, audit-mapped output), so a
        // quoted devin.acp line is a forgery attempt, never a signal.
        var detection = Detect(Unframed(stdout: StdoutWith(ModelMismatchEnvelope)));

        Assert.Null(detection);
    }

    [Fact]
    public void ModelMismatchEnvelope_InSummary_ReturnsNull()
    {
        var detection = Detect(Unframed(summary: ModelMismatchEnvelope));

        Assert.Null(detection);
    }

    [Fact]
    public void StderrWarnInsideStderrEnvelope_FramedCapture_ClassifiesInfraTransport()
    {
        // The WARN always reaches the capture folded inside a
        // codeybox.stderr envelope — the fallback must still recognise it
        // (e.g. a shim killed between the WARN and its abort envelope).
        var stdout = StdoutWith(
            "{\"type\":\"codeybox.stderr\",\"text\":\"" + StderrWarnLine + "\"}");

        var detection = Detect(Framed(stdout: stdout));

        Assert.NotNull(detection);
        Assert.Equal(ProviderTransientKind.InfraTransport, detection!.Kind);
        Assert.Equal(DevinModelMismatchDetector.Signature, detection.MatchedSignature);
    }

    [Fact]
    public void StderrWarnInsideStderrEnvelope_UnframedCapture_ReturnsNull()
    {
        // A codeybox.stderr-shaped line on an unframed stream is just
        // agent-printed text — the envelope claim requires the framed
        // provenance stamp.
        var stdout = StdoutWith(
            "{\"type\":\"codeybox.stderr\",\"text\":\"" + StderrWarnLine + "\"}");

        Assert.Null(Detect(Unframed(stdout: stdout)));
    }

    [Fact]
    public void StderrWarnOnRawStderr_ClassifiesInfraTransport()
    {
        // Raw stderr is the provider's own diagnostic channel on every
        // capture shape — no framing required.
        var detection = new DevinQuotaFailureDetector().DetectProviderTransient(StderrWarnLine, null, null);

        Assert.NotNull(detection);
        Assert.Equal(ProviderTransientKind.InfraTransport, detection!.Kind);
        Assert.Equal(DevinModelMismatchDetector.Signature, detection.MatchedSignature);
    }

    [Fact]
    public void StderrWarnOnRawStderr_UnframedResult_ClassifiesInfraTransport()
    {
        var detection = Detect(Unframed(stderr: StderrWarnLine));

        Assert.NotNull(detection);
        Assert.Equal(ProviderTransientKind.InfraTransport, detection!.Kind);
        Assert.Equal(DevinModelMismatchDetector.Signature, detection.MatchedSignature);
    }

    [Fact]
    public void StderrWarnInsideDevinAcpPayload_FramedCapture_ReturnsNull()
    {
        // The marker quoted inside an agent_message_chunk payload is model
        // text riding a genuine envelope — payload fields are
        // agent-influenceable and must never feed the text fallback.
        var stdout = StdoutWith(
            "{\"type\":\"devin.acp\",\"event\":\"session_update\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"text\":\""
            + StderrWarnLine + "\"}}}");

        Assert.Null(Detect(Framed(stdout: stdout)));
    }

    [Fact]
    public void StderrWarnInsideTurnCompleteFinalText_FramedCapture_ReturnsNull()
    {
        var stdout = StdoutWith(
            "{\"type\":\"devin.acp\",\"event\":\"turn_complete\",\"stopReason\":\"end_turn\",\"finalText\":\""
            + StderrWarnLine + "\"}");

        Assert.Null(Detect(Framed(stdout: stdout)));
    }

    [Fact]
    public void ModelMismatch_WithAuthSignalInCapture_Suppressed()
    {
        var detection = Detect(Framed(
            stderr: "Error: Not logged in. Run `devin auth login` to authenticate.",
            stdout: StdoutWith(ModelMismatchEnvelope)));

        Assert.Null(detection);
    }

    [Fact]
    public void ModelMismatch_ParkedError_UsesTransientKindWithoutRawSecrets()
    {
        var detection = Detect(Framed(stdout: StdoutWith(ModelMismatchEnvelope)));
        Assert.NotNull(detection);

        var parkedError = ProviderTransientRetryPolicy.BuildParkedError(detection!);

        Assert.Equal("transient", ProviderTransientRetryPolicy.ParkedFailureKind);
        Assert.Contains(DevinModelMismatchDetector.Signature, parkedError, StringComparison.Ordinal);
        Assert.DoesNotContain("swe-2-high", parkedError, StringComparison.Ordinal);
    }

    // turn_error + cognition.ai/retryable: the provider's own retryability
    // declaration parks for bounded transient retry.

    [Fact]
    public void RetryableTurnErrorEnvelope_OutcomeIsTurnError()
    {
        var outcome = DevinAcpOutcome.Extract(StdoutWith(RetryableTurnErrorEnvelope));

        Assert.Equal(DevinAcpOutcome.TerminalEvent.TurnError, outcome.Event);
        Assert.NotNull(outcome.Diagnostic);
        Assert.Contains("-32013", outcome.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void RetryableTurnErrorEnvelope_FramedCapture_ClassifiesInfraTransport()
    {
        var detection = Detect(Framed(stdout: StdoutWith(RetryableTurnErrorEnvelope)));

        Assert.NotNull(detection);
        Assert.Equal(ProviderTransientKind.InfraTransport, detection!.Kind);
        Assert.Equal(DevinRetryableTurnErrorDetector.Signature, detection.MatchedSignature);
    }

    [Fact]
    public void RetryableTurnErrorEnvelope_CompositeDispatch_FramedCapture_ClassifiesInfraTransport()
    {
        var classifier = new CompositeQuotaFailureClassifier([new DevinQuotaFailureDetector()]);

        var detection = classifier.DetectProviderTransient(
            AgentKind.Devin, Framed(stdout: StdoutWith(RetryableTurnErrorEnvelope)));

        Assert.NotNull(detection);
        Assert.Equal(ProviderTransientKind.InfraTransport, detection!.Kind);
        Assert.Equal(DevinRetryableTurnErrorDetector.Signature, detection.MatchedSignature);
    }

    [Fact]
    public void RetryableTurnErrorEnvelope_UnframedCapture_ReturnsNull()
    {
        // A forged turn_error line on an unframed stream (print-mode
        // stdout, audit-mapped output, or raw stderr) is agent/model text —
        // never a provider retryability declaration.
        Assert.Null(Detect(Unframed(stdout: StdoutWith(RetryableTurnErrorEnvelope))));
        Assert.Null(Detect(Unframed(stderr: StdoutWith(RetryableTurnErrorEnvelope))));
        Assert.Null(Detect(Unframed(summary: RetryableTurnErrorEnvelope)));
    }

    [Fact]
    public void TurnErrorWithoutRetryableFlag_FramedCapture_ReturnsNull()
    {
        // Same code and message shape, but the provider did not declare the
        // error retryable — no transient parking.
        var stdout = StdoutWith(
            "{\"type\":\"devin.acp\",\"event\":\"turn_error\",\"code\":-32013,"
            + "\"message\":\"Permission denied: This model is no longer available "
            + "for your account. Please switch to a different model.\"}");

        Assert.Null(Detect(Framed(stdout: stdout)));
    }

    [Fact]
    public void TurnErrorWithRetryableFalse_FramedCapture_ReturnsNull()
    {
        var stdout = StdoutWith(
            "{\"type\":\"devin.acp\",\"event\":\"turn_error\",\"code\":-32013,"
            + "\"message\":\"Permission denied: This model is no longer available "
            + "for your account. Please switch to a different model.\","
            + "\"retryable\":false}");

        Assert.Null(Detect(Framed(stdout: stdout)));
    }

    [Fact]
    public void RetryableTurnError_WithAuthSignalInCapture_Suppressed()
    {
        var detection = Detect(Framed(
            stderr: "Error: Not logged in. Run `devin auth login` to authenticate.",
            stdout: StdoutWith(RetryableTurnErrorEnvelope)));

        Assert.Null(detection);
    }

    [Fact]
    public void RetryableTurnError_ParkedError_UsesTransientKindWithoutRawSecrets()
    {
        var detection = Detect(Framed(stdout: StdoutWith(RetryableTurnErrorEnvelope)));
        Assert.NotNull(detection);

        var parkedError = ProviderTransientRetryPolicy.BuildParkedError(detection!);

        Assert.Contains(DevinRetryableTurnErrorDetector.Signature, parkedError, StringComparison.Ordinal);
        Assert.DoesNotContain("Permission denied", parkedError, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectProviderTransient_EmptyInputs_ReturnsNull()
    {
        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient(null, null, null));
        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient("", "", ""));
        Assert.Null(Detect(Framed()));
        Assert.Null(Detect(Unframed()));
    }
}
