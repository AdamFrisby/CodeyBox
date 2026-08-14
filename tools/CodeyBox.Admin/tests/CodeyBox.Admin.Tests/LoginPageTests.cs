using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using LoginPage = CodeyBox.Admin.Web.Components.Pages.Login;

namespace CodeyBox.Admin.Tests;

public sealed class LoginPageTests : BunitContext
{
    [Fact]
    public void GoogleLogin_UsesFullBrowserNavigation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBoxAdmin:Authentication:Google:ClientId"] = "client-id",
                ["CodeyBoxAdmin:Authentication:Google:ClientSecret"] = "client-secret",
            })
            .Build();
        Services.AddSingleton<IConfiguration>(configuration);

        var cut = Render<LoginPage>();
        var link = cut.Find("a[href^='/account/google-login']");

        Assert.Equal("false", link.GetAttribute("data-enhance-nav"));
    }
}
