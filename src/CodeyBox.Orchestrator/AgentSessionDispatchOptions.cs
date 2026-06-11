namespace CodeyBox.Orchestrator;

/// <summary>
/// Orchestrator-owned dispatch gate for the resumable agent-session pipeline.
/// Carries only the orchestration-side decision ("is the resumable session
/// path enabled at all") so <see cref="PipelineRunner"/> never needs to
/// reference a provider-specific options shape
/// to make its dispatch decision. The composition root reads the underlying
/// per-provider options at registration time and projects the master switch
/// into this struct.
///
/// <para>The field is mutable on a singleton so an
/// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/> change handler
/// can flip the dispatch decision without an orchestrator restart, mirroring
/// the per-provider options pattern. Reads of <see cref="Enabled"/> are atomic
/// for the boolean primitive.</para>
/// </summary>
public sealed class AgentSessionDispatchOptions
{
    /// <summary>
    /// Master orchestrator gate. <c>false</c> (the default) keeps every work
    /// item on the legacy independent-phase pipeline regardless of whether a
    /// session runner is registered. <c>true</c> hands the dispatch decision
    /// to the per-item / per-project gate composition in
    /// <see cref="PipelineRunner.ShouldEnterClaudeSessionMode"/>.
    /// </summary>
    public bool Enabled { get; set; }
}
