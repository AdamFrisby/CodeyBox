using System.Security.Claims;

namespace CodeyBox.Admin.Web.Services;

/// <summary>
/// The admin's authentication rules that are worth stating once and testing
/// directly, rather than leaving inline in startup where they cannot be exercised.
/// </summary>
public static class AdminAuthPolicies
{
    /// <summary>Rate-limit policy applied to the local password login endpoint.</summary>
    public const string LocalLoginRateLimit = "local-login";

    /// <summary>
    /// Value of the <see cref="ClaimTypes.AuthenticationMethod"/> claim stamped on
    /// a principal signed in through the local password login.
    /// </summary>
    public const string LocalAuthenticationMethod = "codeybox-local-password";

    /// <summary>
    /// True when <paramref name="user"/> signed in through the local password login.
    ///
    /// <para>The admin's authorization policy uses this to exempt local sign-ins
    /// from the email-domain backstop: that backstop checks the email an identity
    /// provider asserts, and a local sign-in has none, so without the exemption a
    /// configured domain list would lock the operator out of their own password.</para>
    ///
    /// <para>Matched by exact equality on the claim value, never by substring, and
    /// only from an authenticated identity — the claim is stamped by our own login
    /// handler into a cookie the data-protection stack signs, so it cannot be
    /// supplied by a client.</para>
    /// </summary>
    public static bool IsLocalSignIn(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Identities.Any(identity => identity.IsAuthenticated
            && identity.HasClaim(ClaimTypes.AuthenticationMethod, LocalAuthenticationMethod));
    }

    /// <summary>
    /// Whether the local username/password login endpoint exists.
    ///
    /// <para>Always in Development. Outside it, only when a password has been
    /// explicitly configured: an operator who never set one gets no password
    /// login at all, so there is nothing to guess at. Configuring the password is
    /// the opt-in — it is what makes the admin safe to expose beyond loopback when
    /// no identity provider is available (Google OAuth refuses plain-HTTP redirect
    /// URIs for anything but localhost, so it cannot protect a LAN address).</para>
    ///
    /// <para>A whitespace-only password counts as unset. It is not a credential,
    /// and treating it as one would register a login whose password is trivial.</para>
    /// </summary>
    public static bool LocalLoginEnabled(bool isDevelopment, string? configuredPassword) =>
        isDevelopment || !string.IsNullOrWhiteSpace(configuredPassword);
}
