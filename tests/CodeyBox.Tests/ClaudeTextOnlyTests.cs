using System.Reflection;
using CodeyBox.Agents.Claude;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class ClaudeTextOnlyTests
{
    [Fact]
    public void ClaudeAgentRunner_DoesNotExposeDirectTextOnlyCapability()
    {
        Assert.False(typeof(ITextOnlyAgentRunner).IsAssignableFrom(typeof(ClaudeAgentRunner)));

        var declaredPublicMethods = typeof(ClaudeAgentRunner).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(declaredPublicMethods, method => method.Name == "RunTextOnlyAsync");
        Assert.DoesNotContain(declaredPublicMethods, method => method.Name == "GetTextOnlyUnavailabilityReason");
    }
}
