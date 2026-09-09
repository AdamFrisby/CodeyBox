using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// Real <see cref="IE2eReplayAuthoringDriver"/>: brings the app under test up in
/// a sandbox via <see cref="IAppUnderTestHarness"/>, drives a cheap-model
/// computer-use exploration with <see cref="CheapModelCuaAuthor"/>, and emits
/// the deterministic replay artifact JSON to attach to the test case.
///
/// <para><b>Cheap models only.</b> The wrapped <see cref="CheapModelCuaAuthor"/>
/// enforces the cheap-model allowlist at construction, so a frontier model id
/// is rejected here up front — authoring can never run on the coding fleet.
/// The exploration itself runs on the cloud E2E pool (the harness's sandbox
/// provider), never the local coding-worker fleet.</para>
///
/// <para>Re-authoring is the same flow re-run: on a broken replay the gate calls
/// back in with the previous artifact / failing result as context, the harness
/// relaunches a fresh session, and the author re-explores — refreshing the
/// artifact. This is the trigger wired to the author's re-explore path.</para>
/// </summary>
public sealed class CheapModelCuaAuthoringDriver : IE2eReplayAuthoringDriver
{
    private readonly IAppUnderTestHarness _harness;
    private readonly IE2eAuthoringTargetResolver _targetResolver;
    private readonly CheapModelCuaAuthor _author;
    private readonly string _modelId;
    private readonly ILogger<CheapModelCuaAuthoringDriver> _logger;

    /// <param name="harness">Brings the app up in a cloud-pool sandbox, ready to drive.</param>
    /// <param name="targetResolver">Maps a test case to its app-launch recipe + exploration plan.</param>
    /// <param name="options">Authoring options — the cheap model id is validated by the wrapped author.</param>
    /// <param name="modelClient">
    /// Cheap-model computer-use client used to plan exploration turns. Required
    /// unless every resolved plan supplies its own explorer; when null the
    /// author throws if it has to build a default explorer.
    /// </param>
    public CheapModelCuaAuthoringDriver(
        IAppUnderTestHarness harness,
        IE2eAuthoringTargetResolver targetResolver,
        CheapModelCuaAuthorOptions? options = null,
        IComputerUseModelClient? modelClient = null,
        TimeProvider? timeProvider = null,
        ILogger<CheapModelCuaAuthoringDriver>? logger = null)
    {
        _harness = harness ?? throw new ArgumentNullException(nameof(harness));
        _targetResolver = targetResolver ?? throw new ArgumentNullException(nameof(targetResolver));
        options ??= new CheapModelCuaAuthorOptions();
        _modelId = options.ModelId;
        // Constructing the author eagerly validates the cheap-model allowlist
        // (throws for a frontier id) so misconfiguration fails at wiring time.
        _author = new CheapModelCuaAuthor(options, timeProvider, modelClient);
        _logger = logger ?? NullLogger<CheapModelCuaAuthoringDriver>.Instance;
    }

    public async Task<E2eReplayAuthoringOutcome> AuthorAsync(
        TestCase testCase,
        E2eReplayAuthoringRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(request);

        var target = await _targetResolver.ResolveAsync(testCase, ct).ConfigureAwait(false);
        if (target is null)
            return E2eReplayAuthoringOutcome.Unresolved(
                $"no app-launch recipe / exploration plan is configured for e2e case '{testCase.Name}' ({testCase.Id})");

        try
        {
            await using var session = await _harness.LaunchAsync(target.Recipe, ct).ConfigureAwait(false);
            var result = await _author.ExploreAndEmitAsync(session, target.Plan, ct: ct).ConfigureAwait(false);
            var artifactJson = E2eReplayArtifactEmitter.SerializeArtifact(result.Artifact);
            _logger.LogInformation(
                "Cheap-model {Model} {Kind} e2e replay for test case {TestCaseId} ({Steps} step(s)).",
                result.AuthorModelId,
                request.IsReauthoring ? "re-authored" : "authored",
                testCase.Id,
                result.Artifact.Steps.Count);
            return E2eReplayAuthoringOutcome.Success(artifactJson, result.AuthorModelId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A launch/exploration/emission failure means we could not produce a
            // working replay for this case — report it as unresolved (the gate
            // blocks). Never swallow it into a fake success.
            _logger.LogWarning(ex, "Cheap-model authoring failed for e2e case {TestCaseId} ({Name}).", testCase.Id, testCase.Name);
            return E2eReplayAuthoringOutcome.Unresolved($"cheap-model authoring failed: {ex.Message}");
        }
    }
}
