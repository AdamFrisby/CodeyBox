using System.Text.Json;

namespace CodeyBox.Tests;

public sealed class SecurityAuditToolProvisioningConfigTests
{
    [Fact]
    public void ProductionAppsettings_ProvisionsSecurityAuditToolsAndSemgrepNetwork()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(AppsettingsPath()));
        var codeyBox = doc.RootElement.GetProperty("CodeyBox");
        var runcmd = codeyBox.GetProperty("MultipassExtraRuncmd")
            .EnumerateArray()
            .Select(e => e.GetString() ?? string.Empty)
            .ToArray();
        var auditHosts = codeyBox.GetProperty("AuditToolAllowedHosts")
            .EnumerateArray()
            .Select(e => e.GetString() ?? string.Empty)
            .ToArray();

        Assert.Contains(runcmd, cmd =>
            cmd.Contains("GITLEAKS_VERSION=8.29.0", StringComparison.Ordinal) &&
            cmd.Contains("sha256sum -c -", StringComparison.Ordinal) &&
            cmd.Contains("gitleaks_${GITLEAKS_VERSION}_linux_${GITLEAKS_ARCH}.tar.gz", StringComparison.Ordinal));
        Assert.Contains(runcmd, cmd =>
            cmd.Contains("python3 -m pip install", StringComparison.Ordinal) &&
            cmd.Contains("semgrep==1.168.0", StringComparison.Ordinal) &&
            !cmd.Contains("--only-binary", StringComparison.Ordinal));
        Assert.Contains(runcmd, cmd => cmd.Contains("gitleaks version | grep -Fx 8.29.0", StringComparison.Ordinal));
        Assert.Contains(runcmd, cmd => cmd.Contains("semgrep --version | grep -Fx 1.168.0", StringComparison.Ordinal));

        Assert.Contains("semgrep.dev", auditHosts);
        Assert.Contains("registry.semgrep.dev", auditHosts);
        Assert.Contains("api.semgrep.dev", auditHosts);
    }

    private static string AppsettingsPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CodeyBox.Api", "appsettings.json");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("Could not locate src/CodeyBox.Api/appsettings.json from the test output directory.");
    }
}
