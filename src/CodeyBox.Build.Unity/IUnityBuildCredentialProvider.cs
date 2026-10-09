namespace CodeyBox.Build.Unity;

/// <summary>
/// Host-side Unity API-token resolution. The sandbox never sees credentials:
/// the host implements this (operator-owned configuration) and the adapter
/// only holds the token for the duration of one call. No default
/// implementation reads environment variables or files; operator wiring is an
/// explicit activation step documented in
/// <c>docs/concepts/unity-build-automation.md</c>.
/// </summary>
public interface IUnityBuildCredentialProvider
{
    Task<string> GetApiTokenAsync(CancellationToken ct);
}

/// <summary>
/// Fail-closed credential provider used until the operator wires a real one:
/// every call throws, so no unauthenticated request ever leaves the host.
/// </summary>
public sealed class NullUnityBuildCredentialProvider : IUnityBuildCredentialProvider
{
    public Task<string> GetApiTokenAsync(CancellationToken ct) =>
        throw new UnityBuildAuthException(
            "No Unity Build Automation credential provider is configured. " +
            "See docs/concepts/unity-build-automation.md activation steps.");
}
