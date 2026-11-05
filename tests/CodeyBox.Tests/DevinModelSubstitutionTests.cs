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
/// agent/model text and a forged envelope line there must never classify.
/// The WARN-text fallback matches only verified
/// <c>codeybox.stderr</c> envelope text fields that precede the first
/// turn-proof envelope while a strictly pre-prompt envelope
/// (<c>session_started</c>/<c>mode_set</c>/handshake-stage <c>fatal</c>)
/// is still in the window: once the turn runs, fd 2 also carries
/// tool-subprocess stderr the agent controls, and mid-turn-legal kinds
/// prove nothing about which side of the prompt the window sits on — so
/// the marker phrase is a forgery vector there, and raw stderr is never
/// scanned at all. Envelopes after <c>turn_complete</c> are never shim
/// output and are ignored everywhere.</para>
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
        // The via discriminator must survive into the diagnostic so
        // operators can tell WHICH substitution signal fired.
        Assert.Contains("(via stderr-warn)", outcome.Diagnostic, StringComparison.Ordinal);
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
    public void ModelMismatchEnvelope_AfterTurnComplete_ReturnsNull()
    {
        // Post-terminal forgery: the shim exits immediately after
        // turn_complete, so a devin.acp envelope positioned after it is
        // pipe-injected, never shim output — it must not classify, and the
        // outcome extraction must keep reporting the genuine turn_complete
        // so a completed run is never flipped to failure.
        var stdout = StdoutWith(
            "{\"type\":\"devin.acp\",\"event\":\"turn_complete\",\"stopReason\":\"end_turn\"}",
            ModelMismatchEnvelope);

        Assert.Null(Detect(Framed(stdout: stdout)));
        Assert.Equal(
            DevinAcpOutcome.TerminalEvent.TurnComplete,
            DevinAcpOutcome.Extract(stdout).Event);
    }

    [Fact]
    public void StderrWarnInsideStderrEnvelope_FramedCapture_ClassifiesInfraTransport()
    {
        // The WARN always reaches the capture folded inside a
        // codeybox.stderr envelope — the fallback must still recognise it
        // (e.g. a shim killed between the WARN and its abort envelope).
        // It sits after session_started but before any turn-proof envelope,
        // so it is provably pre-prompt fd-2 output.
        var stdout = StdoutWith(
            "{\"type\":\"devin.acp\",\"event\":\"session_started\",\"sessionId\":\"s-1\"}",
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
            "{\"type\":\"devin.acp\",\"event\":\"session_started\",\"sessionId\":\"s-1\"}",
            "{\"type\":\"codeybox.stderr\",\"text\":\"" + StderrWarnLine + "\"}");

        Assert.Null(Detect(Unframed(stdout: stdout)));
    }

    [Fact]
    public void StderrWarnInsideStderrEnvelope_WithoutAnyDevinAcpEnvelope_ReturnsNull()
    {
        // Retained-tail edge: a post-terminal stderr flood can fill the
        // whole scan window with codeybox.stderr lines and no devin.acp
        // evidence at all. With no handshake envelope proving the shim was
        // mid-handshake, agent text must not pose as the WARN fallback.
        var stdout = StdoutWith(
            "{\"type\":\"codeybox.stderr\",\"text\":\"" + StderrWarnLine + "\"}");

        Assert.Null(Detect(Framed(stdout: stdout)));
    }

    [Fact]
    public void StderrWarnInsideStderrEnvelope_AfterPromptSent_ReturnsNull()
    {
        // THE forgery vector: a tool subprocess prints the marker phrase to
        // fd 2 mid-turn (e.g. `ls "did not resolve to an available model"`).
        // Once prompt_sent exists, fd 2 is agent/model-controlled — the
        // phrase is text, not provider evidence, and must never classify.
        var stdout = StdoutWith(
            "{\"type\":\"devin.acp\",\"event\":\"session_started\",\"sessionId\":\"s-1\"}",
            "{\"type\":\"devin.acp\",\"event\":\"prompt_sent\",\"promptBytes\":10}",
            "{\"type\":\"codeybox.stderr\",\"text\":\"ls: cannot access '"
                + StderrWarnLine + "': No such file or directory\"}",
            "{\"type\":\"devin.acp\",\"event\":\"turn_complete\",\"stopReason\":\"end_turn\"}");

        Assert.Null(Detect(Framed(stdout: stdout)));
    }

    [Fact]
    public void StderrWarnInsideStderrEnvelope_TurnEvidenceWithoutPromptSent_ReturnsNull()
    {
        // prompt_sent itself can scroll out of the retained tail on a long
        // turn — but turn_complete/turn_error/session_update envelopes can
        // only exist post-prompt, so they prove the same boundary and the
        // marker still must not classify.
        var stdout = StdoutWith(
            "{\"type\":\"devin.acp\",\"event\":\"session_update\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"text\":\"hi\"}}}",
            "{\"type\":\"codeybox.stderr\",\"text\":\"" + StderrWarnLine + "\"}",
            "{\"type\":\"devin.acp\",\"event\":\"turn_complete\",\"stopReason\":\"end_turn\"}");

        Assert.Null(Detect(Framed(stdout: stdout)));
    }

    [Fact]
    public void StderrWarnInsideStderrEnvelope_BeforePromptSent_ClassifiesEvenWhenTurnRan()
    {
        // A marker relayed BEFORE the first turn-proof envelope genuinely
        // predates the prompt — fd 2 was still CLI-only stderr, so the WARN
        // is provider-authentic even though the (grace-expired) turn went
        // on to run. The substitution really happened; classifying it
        // transient re-runs the item on the configured model.
        var stdout = StdoutWith(
            "{\"type\":\"devin.acp\",\"event\":\"session_started\",\"sessionId\":\"s-1\"}",
            "{\"type\":\"codeybox.stderr\",\"text\":\"" + StderrWarnLine + "\"}",
            "{\"type\":\"devin.acp\",\"event\":\"prompt_sent\",\"promptBytes\":10}",
            "{\"type\":\"devin.acp\",\"event\":\"turn_error\",\"code\":-32013,\"message\":\"x\"}");

        var detection = Detect(Framed(stdout: stdout));

        Assert.NotNull(detection);
        Assert.Equal(DevinModelMismatchDetector.Signature, detection!.MatchedSignature);
    }

    [Fact]
    public void StderrWarnInsideStderrEnvelope_MidTurnLegalEnvelopesOnly_ReturnsNull()
    {
        // Retained-tail edge: prompt_sent and the early turn-proof
        // envelopes can scroll out of the 64 KiB window during a quiet
        // tool-execution stretch, leaving only mid-turn-legal kinds
        // (config_option_update session_updates, notifications,
        // protocol_error, unhandled_request). Those prove nothing about
        // which side of the prompt the window sits on, so a post-prompt
        // forged marker positioned before the window's first turn-proof
        // envelope must NOT classify — no strictly pre-prompt envelope
        // remains as evidence.
        var stdout = StdoutWith(
            "{\"type\":\"devin.acp\",\"event\":\"session_update\",\"update\":{\"sessionUpdate\":\"config_option_update\",\"configOptions\":[{\"id\":\"model\",\"category\":\"model\",\"currentValue\":\"swe-2-high\"}]}}",
            "{\"type\":\"devin.acp\",\"event\":\"notification\",\"method\":\"unknown/method\"}",
            "{\"type\":\"codeybox.stderr\",\"text\":\"ls: cannot access '"
                + StderrWarnLine + "': No such file or directory\"}",
            "{\"type\":\"devin.acp\",\"event\":\"session_update\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"text\":\"hi\"}}}",
            "{\"type\":\"devin.acp\",\"event\":\"turn_complete\",\"stopReason\":\"end_turn\"}");

        Assert.Null(Detect(Framed(stdout: stdout)));
    }

    [Fact]
    public void StderrWarnInsideStderrEnvelope_HandshakeFatalStillCountsAsPrePromptEvidence()
    {
        // A shim killed between the WARN and its abort envelope can leave a
        // handshake-stage fatal as the only devin.acp evidence — that stage
        // can only exist pre-prompt, so the fallback still fires.
        var stdout = StdoutWith(
            "{\"type\":\"devin.acp\",\"event\":\"fatal\",\"stage\":\"session/new\",\"code\":-32603,\"message\":\"boom\"}",
            "{\"type\":\"codeybox.stderr\",\"text\":\"" + StderrWarnLine + "\"}");

        var detection = Detect(Framed(stdout: stdout));

        Assert.NotNull(detection);
        Assert.Equal(DevinModelMismatchDetector.Signature, detection!.MatchedSignature);
    }

    [Fact]
    public void StderrWarnOnRawStderr_ReturnsNull()
    {
        // Raw stderr is never scanned for the WARN: on framed runs the
        // exec wrapper pins the CLI's fd 2 into codeybox.stderr envelopes
        // (the WARN cannot appear there), and on unframed captures stderr
        // is agent/model text — including reconstructed results whose
        // stderr slot carries exec stdout.
        Assert.Null(new DevinQuotaFailureDetector().DetectProviderTransient(StderrWarnLine, null, null));
        Assert.Null(Detect(Unframed(stderr: StderrWarnLine)));
        Assert.Null(Detect(Framed(stderr: StderrWarnLine,
            stdout: "{\"type\":\"devin.acp\",\"event\":\"session_started\",\"sessionId\":\"s-1\"}")));
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
    public void RetryableTurnErrorEnvelope_AfterTurnComplete_ReturnsNull()
    {
        // Same post-terminal rule for the shared bounded enumerator: a
        // forged retryable turn_error appended after turn_complete is
        // pipe-injected noise — never examined, never classified.
        var stdout = StdoutWith(
            "{\"type\":\"devin.acp\",\"event\":\"turn_complete\",\"stopReason\":\"end_turn\"}",
            RetryableTurnErrorEnvelope);

        Assert.Null(Detect(Framed(stdout: stdout)));
        Assert.Equal(
            DevinAcpOutcome.TerminalEvent.TurnComplete,
            DevinAcpOutcome.Extract(stdout).Event);
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

    // Bounded tail scans: the signatures are TERMINAL envelopes — the last
    // lines of the capture — so the examined set must favour the tail of
    // the char-bounded window. The turn_error detector uses the shared
    // last-MaxEnvelopesExamined bound; the model_mismatch detector scans
    // EVERY typed envelope in the window because its pre-prompt proof
    // needs the interleaved codeybox.stderr ordering. A chatty turn
    // (agent_message_chunk deltas, stderr relay lines) must never push a
    // terminal envelope out of view on either path.

    private static string[] ChattySessionUpdates(int count)
        => Enumerable.Range(0, count)
            .Select(i => "{\"type\":\"devin.acp\",\"event\":\"session_update\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"text\":\"chunk" + i + "\"}}}")
            .ToArray();

    [Fact]
    public void ModelMismatchEnvelope_PastSharedEnvelopeCap_StillDetected()
    {
        // The model_mismatch scan has no envelope-count cap — it examines
        // every typed envelope in the char window — so a capture carrying
        // more devin.acp envelopes than MaxEnvelopesExamined still detects.
        var lines = ChattySessionUpdates(DevinAcpEnvelope.MaxEnvelopesExamined + 40)
            .Append(ModelMismatchEnvelope)
            .ToArray();

        var detection = Detect(Framed(stdout: StdoutWith(lines)));

        Assert.NotNull(detection);
        Assert.Equal(DevinModelMismatchDetector.Signature, detection!.MatchedSignature);
    }

    [Fact]
    public void RetryableTurnErrorEnvelope_BeyondEnvelopeCap_StillDetected()
    {
        var lines = ChattySessionUpdates(DevinAcpEnvelope.MaxEnvelopesExamined + 40)
            .Append(RetryableTurnErrorEnvelope)
            .ToArray();

        var detection = Detect(Framed(stdout: StdoutWith(lines)));

        Assert.NotNull(detection);
        Assert.Equal(DevinRetryableTurnErrorDetector.Signature, detection!.MatchedSignature);
    }

    [Fact]
    public void RetryableTurnErrorEnvelope_StderrEnvelopeFlood_DoesNotConsumeEnvelopeCap()
    {
        // codeybox.stderr relay lines are typed envelopes on the same
        // stream but are not devin.acp events — a stderr flood must not
        // push the terminal turn_error out of the examined set either.
        var stderrFlood = Enumerable.Range(0, DevinAcpEnvelope.MaxEnvelopesExamined + 40)
            .Select(i => "{\"type\":\"codeybox.stderr\",\"text\":\"tool noise " + i + "\"}");
        var lines = ChattySessionUpdates(5)
            .Concat(stderrFlood)
            .Append(RetryableTurnErrorEnvelope)
            .ToArray();

        var detection = Detect(Framed(stdout: StdoutWith(lines)));

        Assert.NotNull(detection);
        Assert.Equal(DevinRetryableTurnErrorDetector.Signature, detection!.MatchedSignature);
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
