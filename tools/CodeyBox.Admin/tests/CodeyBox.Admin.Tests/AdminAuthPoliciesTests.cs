using System.Security.Claims;
using CodeyBox.Admin.Web.Services;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// The rules that decide whether a password login exists at all, and whether a
/// signed-in principal is exempt from the email-domain backstop. Both are
/// authorization decisions, so each case below is one the admin must not get wrong
/// once it listens beyond loopback.
/// </summary>
public sealed class AdminAuthPoliciesTests
{
    [Theory]
    [InlineData(true, null, true)]         // Development always has it
    [InlineData(true, "", true)]
    [InlineData(false, "s3cret-value", true)] // Production: opted in by configuring a password
    [InlineData(false, null, false)]       // Production, never configured: no endpoint to attack
    [InlineData(false, "", false)]
    [InlineData(false, "   ", false)]      // whitespace is not a credential
    [InlineData(false, "\t\n", false)]
    public void LocalLoginEnabled_OnlyOutsideDevelopmentWhenAPasswordIsConfigured(
        bool isDevelopment, string? password, bool expected)
    {
        Assert.Equal(expected, AdminAuthPolicies.LocalLoginEnabled(isDevelopment, password));
    }

    [Fact]
    public void IsLocalSignIn_RecognisesThePrincipalOurLoginHandlerIssues()
    {
        var user = Principal(authenticated: true,
            new Claim(ClaimTypes.Name, "admin"),
            new Claim(ClaimTypes.AuthenticationMethod, AdminAuthPolicies.LocalAuthenticationMethod));

        Assert.True(AdminAuthPolicies.IsLocalSignIn(user));
    }

    [Fact]
    public void IsLocalSignIn_IgnoresTheClaimOnAnUnauthenticatedIdentity()
    {
        // The exemption must come from a signed-in session, never from a claim
        // that merely appears on an anonymous identity.
        var user = Principal(authenticated: false,
            new Claim(ClaimTypes.AuthenticationMethod, AdminAuthPolicies.LocalAuthenticationMethod));

        Assert.False(AdminAuthPolicies.IsLocalSignIn(user));
    }

    [Theory]
    [InlineData("codeybox-local-password-extra")] // exact match, never prefix
    [InlineData("xcodeybox-local-password")]      // never suffix
    [InlineData("CODEYBOX-LOCAL-PASSWORD")]       // never case-folded
    [InlineData("Google")]
    public void IsLocalSignIn_RequiresAnExactAuthenticationMethod(string method)
    {
        var user = Principal(authenticated: true,
            new Claim(ClaimTypes.AuthenticationMethod, method));

        Assert.False(AdminAuthPolicies.IsLocalSignIn(user));
    }

    [Fact]
    public void IsLocalSignIn_AnIdentityProviderSignInIsNotLocal()
    {
        // A Google or Cloudflare principal must still face the domain backstop.
        var user = Principal(authenticated: true,
            new Claim(ClaimTypes.Email, "someone@example.com"));

        Assert.False(AdminAuthPolicies.IsLocalSignIn(user));
    }

    private static ClaimsPrincipal Principal(bool authenticated, params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticated ? "Cookies" : null));
}
