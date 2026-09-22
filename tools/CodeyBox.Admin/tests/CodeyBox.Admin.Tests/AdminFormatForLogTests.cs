using CodeyBox.Admin.Web.Services;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Route parameters are declared as unconstrained strings and ASP.NET
/// URL-decodes the path segment, so a "%0A" in a URL arrives as a real
/// newline. Anything logged from one must not be able to forge a log line.
/// </summary>
public sealed class AdminFormatForLogTests
{
    [Fact]
    public void ForLog_StripsLineBreaks_SoALogLineCannotBeForged()
    {
        var forged = "abc\n2026-09-22 INF Queue drained by operator";
        var safe = AdminFormat.ForLog(forged);

        Assert.DoesNotContain('\n', safe);
        Assert.DoesNotContain('\r', safe);
        // The text survives so the odd value is still diagnosable.
        Assert.Contains("Queue drained by operator", safe, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\u0085")]   // NEL — a line break to some log viewers
    [InlineData("\u001b")]   // ESC — terminal escape sequences in a tailed log
    [InlineData("\u0000")]
    public void ForLog_ReplacesEveryControlCharacter(string control)
    {
        var safe = AdminFormat.ForLog($"id{control}tail");

        Assert.Equal("id�tail", safe);
    }

    [Fact]
    public void ForLog_LeavesOrdinaryIdsUntouched()
    {
        const string id = "d70b3b155b644578871119e130447a35";
        Assert.Equal(id, AdminFormat.ForLog(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ForLog_EmptyInput_IsEmpty(string? value)
    {
        Assert.Equal("", AdminFormat.ForLog(value));
    }

    [Fact]
    public void ForLog_LongValue_TakesTheHeapPathAndStillSanitises()
    {
        // Longer than the stackalloc threshold, so the heap branch is covered.
        var value = new string('a', 300) + "\n" + new string('b', 10);
        var safe = AdminFormat.ForLog(value);

        Assert.Equal(311, safe.Length);
        Assert.DoesNotContain('\n', safe);
    }
}
