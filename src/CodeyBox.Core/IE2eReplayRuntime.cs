using System.Threading;
using System.Threading.Tasks;

namespace CodeyBox.Core;

/// <summary>
/// Deterministic replay engine: given a parsed artifact and a live sandbox,
/// run the steps + assertions and return a structured result. NO model in the
/// loop — the runtime is the executable contract that committed test cases must
/// pass against the pre-baked app stack.
///
/// <para>The interface exists for testability: the dispatcher takes one
/// instance, but tests inject a fake to assert routing + parallelism without
/// having to stand up a real sandbox.</para>
/// </summary>
public interface IE2eReplayRuntime
{
    Task<E2eRunResult> ExecuteAsync(E2eReplayArtifact artifact, ISandbox sandbox, CancellationToken ct = default);
}
