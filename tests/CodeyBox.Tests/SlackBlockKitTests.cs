using CodeyBox.Core;
using CodeyBox.SlackPlugin;

namespace CodeyBox.Tests;

/// <summary>
/// Direct input→output tests for the Slack Block Kit core: severity mapping,
/// mrkdwn escaping, truncation, and the button-value binding contract shared
/// with the host-side inbound parser.
/// </summary>
public sealed class SlackBlockKitTests
{
    [Theory]
    [InlineData(NotificationSeverity.Information, "good", ":information_source:")]
    [InlineData(NotificationSeverity.Warning, "warning", ":warning:")]
    [InlineData(NotificationSeverity.Critical, "danger", ":rotating_light:")]
    public void Severity_MapsToColorAndEmoji(NotificationSeverity severity, string color, string emoji)
    {
        Assert.Equal(color, SlackBlockKit.ColorFor(severity));
        Assert.Equal(emoji, SlackBlockKit.EmojiFor(severity));
    }

    [Fact]
    public void EscapeMrkdwn_NeutralisesControlCharacters()
    {
        Assert.Equal("&amp; &lt;tag&gt;", SlackBlockKit.EscapeMrkdwn("& <tag>"));
        Assert.Equal(string.Empty, SlackBlockKit.EscapeMrkdwn(null));
    }

    [Fact]
    public void Truncate_MarksTheCut()
    {
        Assert.Equal("abc", NotificationRendering.Truncate("abc", 10));
        var cut = NotificationRendering.Truncate(new string('x', 100), 20);
        Assert.Equal(20, cut.Length);
        Assert.EndsWith("… (truncated)", cut);
    }

    [Fact]
    public void ButtonValue_RoundTrips()
    {
        var value = SlackBlockKit.EncodeButtonValue("work-1", "q-001", "Use rollbacks", "work-1:q-001");
        Assert.NotNull(value);

        Assert.True(SlackBlockKit.TryDecodeButtonValue(value, out var w, out var q, out var a, out var c));
        Assert.Equal("work-1", w);
        Assert.Equal("q-001", q);
        Assert.Equal("Use rollbacks", a);
        Assert.Equal("work-1:q-001", c);
    }

    [Fact]
    public void ButtonValue_OversizedBinding_ReturnsNull()
    {
        Assert.Null(SlackBlockKit.EncodeButtonValue("work-1", "q-001", new string('A', 1950), "work-1:q-001"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("""{"w":"a"}""")]
    public void ButtonValue_Malformed_DoesNotDecode(string? value)
    {
        Assert.False(SlackBlockKit.TryDecodeButtonValue(value, out _, out _, out _, out _));
    }

    [Fact]
    public void AgnesUrl_NeedsBaseAndWorkItem()
    {
        Assert.Equal("https://agnes.example.invalid/workitems/work-1",
            NotificationLinks.AgnesWorkItemUrl("https://agnes.example.invalid/", "work-1"));
        Assert.Null(NotificationLinks.AgnesWorkItemUrl("", "work-1"));
        Assert.Null(NotificationLinks.AgnesWorkItemUrl("https://agnes.example.invalid/", null));
        Assert.Null(NotificationLinks.AgnesWorkItemUrl("not a url", "work-1"));
        // One safe-link policy for every provider: a non-http(s) base is
        // refused rather than rendered.
        Assert.Null(NotificationLinks.AgnesWorkItemUrl("ftp://agnes.example.invalid", "work-1"));
    }
}
