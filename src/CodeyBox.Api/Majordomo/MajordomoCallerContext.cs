using CodeyBox.Core;
using Microsoft.AspNetCore.Http;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// Resolves the calling API principal into the two majordomo-facing
/// identities: the identity label used for audit/ledger accounting, and the
/// <see cref="WorkInitiator"/> stamped on committed mutations. One
/// implementation shared by the MCP executor and the proposal endpoints so
/// principal decoding cannot drift between the agent and operator surfaces.
/// </summary>
internal static class MajordomoCallerContext
{
    /// <summary>
    /// The caller's identity label: the API-client name, the auth-disabled
    /// sentinel when the host runs without authentication, or
    /// <c>"unknown"</c> when no principal is bound to the request.
    /// </summary>
    public static string Identity(HttpContext? context) =>
        TryGetPrincipal(context) is { } principal
            ? ApiKeyAuth.IsAuthenticationDisabled(principal)
                ? ApiKeyAuth.AuthenticationDisabledClientName
                : principal.Name
            : "unknown";

    /// <summary>
    /// The principal's configured fixed initiator, or
    /// <paramref name="fallback"/> when the request carries no principal.
    /// </summary>
    public static WorkInitiator Initiator(HttpContext? context, WorkInitiator fallback) =>
        TryGetPrincipal(context)?.FixedInitiator ?? fallback;

    private static ApiClientPrincipal? TryGetPrincipal(HttpContext? context) =>
        context is not null && ApiKeyAuth.TryGetPrincipal(context, out var principal)
            ? principal
            : null;
}
