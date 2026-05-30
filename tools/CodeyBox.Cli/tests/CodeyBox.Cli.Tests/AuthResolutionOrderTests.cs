using System.Text.Json;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class AuthResolutionOrderTests
{
    private string? _capturedKey;
    private string? _capturedUrl;

    private Func<ResolvedConfig, CodeyBoxClient> MakeCapturingFactory()
    {
        return config =>
        {
            _capturedKey = config.ApiKey;
            _capturedUrl = config.ApiBaseUrl;
            return new CodeyBoxClient(
                new HttpClient(new FakeHttpMessageHandler(_ => SampleData.WorkItemListResponse([])))
                { BaseAddress = new Uri(config.ApiBaseUrl) });
        };
    }

    private static void SetupConfigFile(string tempDir, string url, string key)
    {
        Directory.CreateDirectory(tempDir);
        var config = new CliConfig { ApiBaseUrl = url, ApiKey = key };
        var json = JsonSerializer.Serialize(config, CliJsonContext.Default.CliConfig);
        File.WriteAllText(Path.Combine(tempDir, "config.json"), json);
    }

    [Fact]
    public async Task Auth_FlagOverridesEnvVar()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "env-key");
        SetupConfigFile(tempDir, "http://localhost:5050", "file-key");

        using var output = new TestOutput();
        try
        {
            await CliApp.InvokeAsync(["queue", "ls", "--api-key", "flag-key"], MakeCapturingFactory());

            Assert.Equal("flag-key", _capturedKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", null);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Auth_EnvVarOverridesConfigFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "env-key");
        SetupConfigFile(tempDir, "http://localhost:5050", "file-key");

        using var output = new TestOutput();
        try
        {
            await CliApp.InvokeAsync(["queue", "ls"], MakeCapturingFactory());

            Assert.Equal("env-key", _capturedKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", null);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Auth_ConfigFileOverridesDefault()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        SetupConfigFile(tempDir, "http://localhost:5050", "file-key");

        using var output = new TestOutput();
        try
        {
            await CliApp.InvokeAsync(["queue", "ls"], MakeCapturingFactory());

            Assert.Equal("file-key", _capturedKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", null);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Auth_FlagUrlOverridesEnvUrl()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", "http://env-host:1111");
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "any-key");

        using var output = new TestOutput();
        try
        {
            await CliApp.InvokeAsync(["queue", "ls", "--api-url", "http://flag-host:9999"], MakeCapturingFactory());

            Assert.Equal("http://flag-host:9999", _capturedUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", null);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Auth_DefaultBaseUrl_UsedWhenNothingSet()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", null);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "any-key");

        using var output = new TestOutput();
        try
        {
            await CliApp.InvokeAsync(["queue", "ls"], MakeCapturingFactory());

            Assert.Equal("http://localhost:5036", _capturedUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", null);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
