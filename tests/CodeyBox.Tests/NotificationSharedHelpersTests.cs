using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Direct input→output tests for the platform-neutral helpers in Core that
/// every notification provider plugin shares: the single safe-link policy,
/// the Agnes deep-link composition, work-item binding, truncation, and the
/// post-timeout floor.
/// </summary>
public sealed class NotificationSharedHelpersTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://files.example.invalid/x")]
    [InlineData("https://user:secret@example.invalid/x")]
    public void SafeLinkUri_RejectsUnsafeOrNonAbsolute(string? url)
    {
        Assert.Null(NotificationLinks.SafeLinkUri(url));
    }

    [Fact]
    public void SafeLinkUri_AcceptsHttpAndHttps_CanonicalForm()
    {
        Assert.Equal("https://example.invalid/a%20b",
            NotificationLinks.SafeLinkUri("https://example.invalid/a b")?.AbsoluteUri);
        Assert.Equal("http://example.invalid/x",
            NotificationLinks.SafeLinkUri("http://example.invalid/x")?.AbsoluteUri);
    }

    [Fact]
    public void AgnesWorkItemUrl_ComposesCanonicalLink()
    {
        Assert.Equal("https://agnes.example.invalid/workitems/work-1",
            NotificationLinks.AgnesWorkItemUrl("https://agnes.example.invalid/", "work-1"));
        Assert.Equal("https://agnes.example.invalid/workitems/work%201",
            NotificationLinks.AgnesWorkItemUrl("https://agnes.example.invalid", "work 1"));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://agnes.example.invalid")]
    [InlineData("https://agnes.example.invalid/?x=1")]
    [InlineData("https://user:pw@agnes.example.invalid")]
    public void AgnesWorkItemUrl_RejectsUnsafeBase(string baseUrl)
    {
        Assert.Null(NotificationLinks.AgnesWorkItemUrl(baseUrl, "work-1"));
    }

    [Fact]
    public void WorkItemIdFor_PrefersActionThenCorrelationToken()
    {
        var fromAction = new Notification
        {
            ConditionId = "c",
            Title = "t",
            Actions = [new NotificationAction
            {
                Label = "l",
                Value = "v",
                WorkItemId = "work-action",
                QuestionId = "q-1",
            }],
            CorrelationToken = "work-token:q-1",
        };
        Assert.Equal("work-action", NotificationBinding.WorkItemIdFor(fromAction));

        var fromToken = new Notification
        {
            ConditionId = "c",
            Title = "t",
            CorrelationToken = "work-token:q-1",
        };
        Assert.Equal("work-token", NotificationBinding.WorkItemIdFor(fromToken));

        var unbound = new Notification { ConditionId = "c", Title = "t" };
        Assert.Null(NotificationBinding.WorkItemIdFor(unbound));
    }

    [Fact]
    public void PostTimeoutOrDefault_ValidConfigured_Wins()
    {
        Assert.Equal(TimeSpan.FromSeconds(42), NotificationDelivery.PostTimeoutOrDefault(42));
        Assert.Equal(TimeSpan.FromSeconds(1), NotificationDelivery.PostTimeoutOrDefault(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void PostTimeoutOrDefault_Invalid_FallsBackAndWarns(int configured)
    {
        var logger = new CapturingLogger<NotificationSharedHelpersTests>();

        var timeout = NotificationDelivery.PostTimeoutOrDefault(configured, logger);

        Assert.Equal(
            TimeSpan.FromSeconds(NotificationDelivery.DefaultPostTimeoutSeconds), timeout);
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && e.Message.Contains("PostTimeoutSeconds"));
    }
}
