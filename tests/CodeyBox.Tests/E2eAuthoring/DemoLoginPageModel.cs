using System.Text.Json;
using CodeyBox.ExploratoryTesting;

namespace CodeyBox.Tests.E2eAuthoring;

/// <summary>
/// In-memory model of the demo login fixture page. Shared by the computer-use
/// sandbox (exploration) and the Playwright stub (replay) so author and replay
/// agree on selectors and DOM state transitions.
/// </summary>
public sealed class DemoLoginPageModel
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool LoggedIn { get; private set; }

    public bool TryLogin()
    {
        if (Email.Trim() == "alice@example.com" && ResolvePassword(Password) == "secret")
        {
            LoggedIn = true;
            return true;
        }

        return false;
    }

    public static string ResolvePassword(string value)
        => value == E2eReplaySensitiveValueRedaction.PasswordPlaceholder ? "secret" : value;

    public string AccessibilityTreeJson()
    {
        var nodes = new List<object>();
        if (!LoggedIn)
        {
            nodes.Add(new { role = "textbox", name = "Email", selector = "#email", testId = "email-input", bounds = new { x = 50, y = 100, w = 256, h = 32 } });
            nodes.Add(new { role = "textbox", name = "Password", selector = "#password", testId = "password-input", bounds = new { x = 50, y = 160, w = 256, h = 32 } });
            nodes.Add(new { role = "button", name = "Log in", selector = "#login-btn", testId = "login-button", bounds = new { x = 50, y = 210, w = 96, h = 36 } });
        }
        else
        {
            nodes.Add(new { role = "status", name = "Welcome banner", selector = "#welcome", testId = "welcome-banner", bounds = new { x = 50, y = 100, w = 320, h = 32 } });
        }

        return JsonSerializer.Serialize(new { nodes });
    }

    public TraceAccessibilityDescriptor? DescribePoint(int x, int y)
    {
        if (!LoggedIn)
        {
            if (Contains(50, 100, 256, 32, x, y))
                return new TraceAccessibilityDescriptor { Role = "textbox", Name = "Email", ElementType = "css:#email" };
            if (Contains(50, 160, 256, 32, x, y))
                return new TraceAccessibilityDescriptor { Role = "textbox", Name = "Password", ElementType = "css:#password" };
            if (Contains(50, 210, 96, 36, x, y))
                return new TraceAccessibilityDescriptor { Role = "button", Name = "Log in", ElementType = "css:#login-btn" };
        }
        else if (Contains(50, 100, 320, 32, x, y))
        {
            return new TraceAccessibilityDescriptor { Role = "status", Name = "Welcome banner", ElementType = "css:#welcome" };
        }

        return null;
    }

    private static bool Contains(int x, int y, int w, int h, int px, int py)
        => px >= x && px < x + w && py >= y && py < y + h;
}
